# Publishes a self-contained, single-file Release build of MssqlMcp.exe to the live release folder
# (default C:\Development\MCPs\MS-SQL-Release\), keeping the previous exe as MssqlMcp_yyyyMMdd_HHmm.exe.
#
# Steps: build into a staging folder -> smoke-start the new exe -> rename the live exe with a timestamp ->
# copy the new exe in -> verify SHA256. If anything fails after the rename, the previous exe is restored.
# Running MssqlMcp.exe processes keep the old binary until they restart (Windows allows renaming a running exe).
#
# Usage:
#   .\publish-release.ps1                 # publish
#   .\publish-release.ps1 -DryRun         # build + smoke-start only, show what would be renamed/copied
#   .\publish-release.ps1 -ReleaseDir D:\Some\Folder
# Extra args after -- are forwarded to dotnet publish (e.g. .\publish-release.ps1 -- --verbosity normal).
#
# Rollback: delete (or rename) MssqlMcp.exe in the release folder and rename the newest MssqlMcp_*.exe back to
# MssqlMcp.exe; restart the MCP clients (Cursor / Claude) so they start the restored exe.

[CmdletBinding()]
param(
    [string]$ReleaseDir = 'C:\Development\MCPs\MS-SQL-Release',
    [switch]$DryRun,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$PublishArgs
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$exeName = 'MssqlMcp.exe'
$staging = Join-Path ([IO.Path]::GetTempPath()) ("MssqlMcp-publish-" + [Guid]::NewGuid().ToString('N'))

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }

try {
    # 1. Build into staging (never straight into the live folder, so a failed build cannot break it).
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
    $p = [Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit(20000)) { $p.Kill(); throw "Smoke start: the new exe did not exit within 20 s." }
    $err = $p.StandardError.ReadToEnd()
    if ($p.ExitCode -ne 1 -or $err -notmatch 'FATAL') { throw "Smoke start: unexpected exit code $($p.ExitCode). stderr: $err" }
    Write-Host "Smoke start OK (exits with the expected 'no connection configured' error)." -ForegroundColor Green

    $newHash = Get-Sha256 $newExe
    $target = Join-Path $ReleaseDir $exeName
    $stamp = Get-Date -Format 'yyyyMMdd_HHmm'
    $backup = Join-Path $ReleaseDir ("MssqlMcp_{0}.exe" -f $stamp)
    for ($i = 2; Test-Path -LiteralPath $backup; $i++) { $backup = Join-Path $ReleaseDir ("MssqlMcp_{0}_{1}.exe" -f $stamp, $i) }

    if ($DryRun) {
        Write-Host "[DryRun] New exe: $newExe (SHA256 $newHash)" -ForegroundColor Yellow
        if (Test-Path -LiteralPath $target) { Write-Host "[DryRun] Would rename $target -> $backup" -ForegroundColor Yellow }
        Write-Host "[DryRun] Would copy the new exe to $target" -ForegroundColor Yellow
        return
    }

    if (-not (Test-Path -LiteralPath $ReleaseDir)) { New-Item -ItemType Directory -Path $ReleaseDir | Out-Null }

    # 3. Rename the live exe (works while it is running), 4. copy the new one, 5. verify; restore on any failure.
    $renamed = $false
    try {
        if (Test-Path -LiteralPath $target) {
            Rename-Item -LiteralPath $target -NewName (Split-Path $backup -Leaf)
            $renamed = $true
            Write-Host "Previous exe kept as $backup" -ForegroundColor Cyan
        }
        Copy-Item -LiteralPath $newExe -Destination $target
        if ((Get-Sha256 $target) -ne $newHash) { throw "SHA256 mismatch after copy." }
    }
    catch {
        Write-Host "Publish failed: $($_.Exception.Message). Restoring the previous exe..." -ForegroundColor Red
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
        if ($renamed) { Rename-Item -LiteralPath $backup -NewName $exeName }
        throw
    }

    Write-Host "Done. $target (SHA256 $newHash)" -ForegroundColor Green
    Write-Host "Restart MCP clients (Cursor / Claude) to pick up the new exe; running processes still use the old one." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
}
