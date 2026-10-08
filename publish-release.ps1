# Publishes a self-contained, single-file Release build of MssqlMcp.exe to the live release folder
# (default C:\Development\MCPs\MS-SQL-Release\), keeping the previous exe as MssqlMcp_yyyyMMdd_HHmm.exe.
#
# Steps: build into a staging folder -> smoke-start the new exe -> rename the live exe with a timestamp ->
# copy the new exe in -> verify SHA256. If anything fails after the rename, the previous exe is restored.
# Running MssqlMcp.exe processes keep the old binary until they restart (Windows allows renaming a running exe).
#
# The same exe is also packed as
#   - the Claude Desktop extension: <ReleaseDir>\ClaudeDesktop\APoint-ms-sql.mcpb (install by opening it in Claude Desktop);
#   - the VS Code / Cursor extension: <ReleaseDir>\extension\APoint-ms-sql.vsix (install with install-APoint-ms-sql.ps1
#     there), also left as vscode-extension\ms-sql-mcp-win32-x64-<version>.vsix (older versions there are removed).
# Previous bundles are kept as <name>_yyyyMMdd_HHmm.<ext>. The VSIX is packaged locally like the publish workflow does
# (same content as the Marketplace build of the same commit, not byte-identical).
#
# Usage:
#   .\publish-release.ps1                 # publish exe + Claude Desktop bundle + VSIX
#   .\publish-release.ps1 -DryRun         # build + smoke-start + pack only, show what would be renamed/copied
#   .\publish-release.ps1 -SkipClaudeDesktop   # no .mcpb
#   .\publish-release.ps1 -SkipExtension       # no .vsix
#   .\publish-release.ps1 -ReleaseDir D:\Some\Folder
# Extra args after -- are forwarded to dotnet publish (e.g. .\publish-release.ps1 -- --verbosity normal).
#
# Rollback: delete (or rename) MssqlMcp.exe in the release folder and rename the newest MssqlMcp_*.exe back to
# MssqlMcp.exe; restart the MCP clients (Cursor / Claude) so they start the restored exe.

[CmdletBinding()]
param(
    [string]$ReleaseDir = 'C:\Development\MCPs\MS-SQL-Release',
    [switch]$DryRun,
    [switch]$SkipClaudeDesktop,
    [switch]$SkipExtension,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$PublishArgs
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$exeName = 'MssqlMcp.exe'
$staging = Join-Path ([IO.Path]::GetTempPath()) ("MssqlMcp-publish-" + [Guid]::NewGuid().ToString('N'))

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }

# Copies a packaged bundle over $target, keeping the previous file as <base>_<stamp><ext>, and verifies the copy.
# Bundles are not live (editors and Claude Desktop install a copy), so a plain replace is safe.
function Publish-Bundle([string]$source, [string]$target, [string]$stamp, [string]$label) {
    $dir = Split-Path $target
    $null = New-Item -ItemType Directory -Force $dir
    if (Test-Path -LiteralPath $target) {
        $base = [IO.Path]::GetFileNameWithoutExtension($target); $ext = [IO.Path]::GetExtension($target)
        $backup = Join-Path $dir ("{0}_{1}{2}" -f $base, $stamp, $ext)
        for ($i = 2; Test-Path -LiteralPath $backup; $i++) { $backup = Join-Path $dir ("{0}_{1}_{2}{3}" -f $base, $stamp, $i, $ext) }
        Rename-Item -LiteralPath $target -NewName (Split-Path $backup -Leaf)
        Write-Host "Previous $label kept as $backup" -ForegroundColor Cyan
    }
    Copy-Item -LiteralPath $source -Destination $target
    $hash = Get-Sha256 $target
    if ($hash -ne (Get-Sha256 $source)) { throw "SHA256 mismatch after copying the $label." }
    Write-Host "${label}: $target ($(Split-Path $source -Leaf), SHA256 $hash)" -ForegroundColor Green
}

try {
    # 1. Rebuild the embedded MCP Apps view (MssqlMcp/Apps/connections.html), then build into staging
    #    (never straight into the live folder, so a failed build cannot break it).
    Write-Host "Building the connections view ..." -ForegroundColor Cyan
    Push-Location "$repoRoot\apps\connections-ui"
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed in apps/connections-ui (exit $LASTEXITCODE)." }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "connections view build failed (exit $LASTEXITCODE)." }
    }
    finally { Pop-Location }

    Write-Host "Building into $staging ..." -ForegroundColor Cyan
    dotnet publish "$repoRoot\MssqlMcp\MssqlMcp.csproj" -c Release -p:PublishProfile=ReleaseSingleFile -o $staging @PublishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    $newExe = Join-Path $staging $exeName
    if (-not (Test-Path -LiteralPath $newExe)) { throw "Build output $newExe not found." }

    # 2. Smoke-start: with no connection configured the server must start and exit with its FATAL config error
    #    (exit code 1) within a few seconds. A crash or hang means the binary is not usable.
    $psi = New-Object Diagnostics.ProcessStartInfo $newExe
    $psi.UseShellExecute = $false; $psi.RedirectStandardError = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardInput = $true
    foreach ($v in 'CONNECTION_STRING', 'MSSQL_CONNECTIONS', 'MSSQL_CONNECTIONS_FILE') { $psi.EnvironmentVariables[$v] = '' }
    $psi.EnvironmentVariables['MSSQL_ALLOW_ADHOC_CONNECTIONS'] = 'false'
    $p = [Diagnostics.Process]::Start($psi)
    # Drain both pipes asynchronously so a chatty start cannot block on a full buffer.
    $outTask = $p.StandardOutput.ReadToEndAsync(); $errTask = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(20000)) { $p.Kill(); throw "Smoke start: the new exe did not exit within 20 s." }
    $null = $outTask.Result; $err = $errTask.Result
    if ($p.ExitCode -ne 1 -or $err -notmatch 'FATAL') { throw "Smoke start: unexpected exit code $($p.ExitCode). stderr: $err" }
    Write-Host "Smoke start OK (exits with the expected 'no connection configured' error)." -ForegroundColor Green

    $newHash = Get-Sha256 $newExe

    # 2b. Pack the same exe as the Claude Desktop bundle (in staging; copied to the release folder after the exe).
    $newBundle = $null
    if (-not $SkipClaudeDesktop) {
        $bundleStaging = Join-Path $staging 'mcpb'
        & "$repoRoot\packaging\mcpb\build-mcpb.ps1" -ExePath $newExe -OutDir $bundleStaging
        $newBundle = Get-ChildItem -LiteralPath $bundleStaging -Filter '*.mcpb' | Select-Object -First 1
        if (-not $newBundle) { throw "Claude Desktop bundle was not produced in $bundleStaging." }
    }
    $bundleTarget = Join-Path $ReleaseDir 'ClaudeDesktop\APoint-ms-sql.mcpb'

    # 2c. Package the VS Code / Cursor extension around the same exe, as the publish workflow does
    #     (stage-exe copies it into vscode-extension\bin; vscode:prepublish compiles; vsce packs win32-x64).
    $newVsix = $null
    if (-not $SkipExtension) {
        Push-Location "$repoRoot\vscode-extension"
        # npm / vsce print warnings on stderr; Windows PowerShell 5.1 turns those into terminating errors under
        # 'Stop'. Judge these native calls by exit code only (the outer preference is restored in finally).
        $outerPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            npm ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed in vscode-extension (exit $LASTEXITCODE)." }
            node scripts/check-version.mjs
            if ($LASTEXITCODE -ne 0) { throw "Server and extension versions differ (scripts/check-version.mjs)." }
            node scripts/stage-exe.mjs $newExe --sha256 $newHash.ToLowerInvariant()
            if ($LASTEXITCODE -ne 0) { throw "stage-exe failed (exit $LASTEXITCODE)." }
            $extVersion = node -p "require('./package.json').version"
            $newVsix = Join-Path $staging "ms-sql-mcp-win32-x64-$extVersion.vsix"
            npx vsce package --target win32-x64 -o $newVsix
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $newVsix)) { throw "vsce package failed (exit $LASTEXITCODE)." }
        }
        finally { $ErrorActionPreference = $outerPreference; Pop-Location }
    }
    $vsixTarget = Join-Path $ReleaseDir 'extension\APoint-ms-sql.vsix'

    $target = Join-Path $ReleaseDir $exeName
    $stamp = Get-Date -Format 'yyyyMMdd_HHmm'
    $backup = Join-Path $ReleaseDir ("MssqlMcp_{0}.exe" -f $stamp)
    for ($i = 2; Test-Path -LiteralPath $backup; $i++) { $backup = Join-Path $ReleaseDir ("MssqlMcp_{0}_{1}.exe" -f $stamp, $i) }

    if ($DryRun) {
        Write-Host "[DryRun] New exe: $newExe (SHA256 $newHash)" -ForegroundColor Yellow
        if (Test-Path -LiteralPath $target) { Write-Host "[DryRun] Would rename $target -> $backup" -ForegroundColor Yellow }
        Write-Host "[DryRun] Would copy the new exe to $target" -ForegroundColor Yellow
        if ($newBundle) { Write-Host "[DryRun] Would copy $($newBundle.Name) to $bundleTarget (previous kept with a timestamp)" -ForegroundColor Yellow }
        if ($newVsix) { Write-Host "[DryRun] Would copy $(Split-Path $newVsix -Leaf) to $vsixTarget (previous kept with a timestamp) and to vscode-extension\" -ForegroundColor Yellow }
        return
    }

    if (-not (Test-Path -LiteralPath $ReleaseDir)) { New-Item -ItemType Directory -Path $ReleaseDir | Out-Null }

    # 3. Rename the live exe (works while it is running), 4. copy the new one, 5. verify; restore on any failure.
    # $target is only ever removed when it is (possibly partially) OUR new copy: never the live exe.
    $hadLive = Test-Path -LiteralPath $target
    $renamed = $false; $copyStarted = $false
    try {
        if ($hadLive) {
            Rename-Item -LiteralPath $target -NewName (Split-Path $backup -Leaf)
            $renamed = $true
            Write-Host "Previous exe kept as $backup" -ForegroundColor Cyan
        }
        $copyStarted = $true
        Copy-Item -LiteralPath $newExe -Destination $target
        if ((Get-Sha256 $target) -ne $newHash) { throw "SHA256 mismatch after copy." }
    }
    catch {
        Write-Host "Publish failed: $($_.Exception.Message)" -ForegroundColor Red
        if ($hadLive -and -not $renamed) {
            # The rename failed: the live exe is untouched where it was. Do not remove anything.
            Write-Host "The live exe was not changed." -ForegroundColor Yellow
        }
        else {
            if ($copyStarted -and (Test-Path -LiteralPath $target)) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
            if ($renamed) {
                if (-not (Test-Path -LiteralPath $target)) {
                    try { Rename-Item -LiteralPath $backup -NewName $exeName; Write-Host "Previous exe restored." -ForegroundColor Yellow }
                    catch { Write-Host "Restore manually: rename $backup to $exeName ($($_.Exception.Message))" -ForegroundColor Red }
                }
                else { Write-Host "Restore manually: delete $target, then rename $backup to $exeName" -ForegroundColor Red }
            }
        }
        throw
    }

    Write-Host "Done. $target (SHA256 $newHash)" -ForegroundColor Green

    # 6. Bundles: keep the previous one with a timestamp, copy the new one, verify.
    if ($newBundle) { Publish-Bundle $newBundle.FullName $bundleTarget $stamp 'Claude Desktop bundle' }
    if ($newVsix) {
        Publish-Bundle $newVsix $vsixTarget $stamp 'VS Code extension'
        # The repo copy is a git-ignored build output: keep only the current version so a stale one is never installed.
        $localVsix = Join-Path "$repoRoot\vscode-extension" (Split-Path $newVsix -Leaf)
        Get-ChildItem -LiteralPath "$repoRoot\vscode-extension" -Filter 'ms-sql-mcp-win32-x64-*.vsix' |
            Where-Object { $_.FullName -ne $localVsix } | Remove-Item -Force
        Copy-Item -LiteralPath $newVsix -Destination $localVsix -Force
        Write-Host "Also at $localVsix. Install it with $(Join-Path $ReleaseDir 'extension\install-APoint-ms-sql.ps1') (close the editors first)." -ForegroundColor Green
    }
    Write-Host "Restart MCP clients (Cursor / Claude) to pick up the new exe; running processes still use the old one." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
}
