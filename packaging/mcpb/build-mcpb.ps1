# Builds the Claude Desktop extension bundle (.mcpb) for APoint-ms-sql.
#
#   .\packaging\mcpb\build-mcpb.ps1                 -> Publish\mcpb\apoint-ms-sql-<version>.mcpb
#   .\packaging\mcpb\build-mcpb.ps1 -OutDir D:\out
#
# Steps: rebuild the embedded connections view, publish the self-contained single-file exe, stamp the manifest with
# the server version (MssqlMcp.csproj <Version>), smoke-start the exe, validate and pack with the mcpb CLI.
# Nothing outside the staging folder and -OutDir is written.
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'Publish\mcpb'),
    [string]$McpbCliVersion = '2.1.2'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$staging = Join-Path ([IO.Path]::GetTempPath()) ("apoint-ms-sql-mcpb-" + [Guid]::NewGuid().ToString('N'))

function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

try {
    $csproj = Get-Content -Raw "$repoRoot\MssqlMcp\MssqlMcp.csproj"
    if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw 'No <Version> in MssqlMcp.csproj.' }
    $version = $Matches[1]
    Write-Host "APoint-ms-sql $version -> .mcpb" -ForegroundColor Cyan

    # 1. The view is embedded in the exe, so rebuild it first.
    Push-Location "$repoRoot\apps\connections-ui"
    try {
        Invoke-Checked 'npm ci (connections-ui)' { npm ci --no-audit --no-fund }
        Invoke-Checked 'connections view build' { npm run build }
    }
    finally { Pop-Location }

    # 2. Self-contained single-file exe into staging\server.
    $serverDir = Join-Path $staging 'server'
    Invoke-Checked 'dotnet publish' {
        dotnet publish "$repoRoot\MssqlMcp\MssqlMcp.csproj" -c Release -p:PublishProfile=ReleaseSingleFile -o $serverDir
    }
    $exe = Join-Path $serverDir 'MssqlMcp.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Publish output $exe not found." }

    # 3. Smoke-start with an empty managed file in a temp folder: the server must answer MCP initialize.
    $smokeFile = Join-Path $staging 'smoke\connections.json'
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($v in 'CONNECTION_STRING', 'MSSQL_CONNECTIONS', 'MSSQL_CONNECTIONS_FILE') { $psi.EnvironmentVariables[$v] = '' }
    $psi.EnvironmentVariables['MSSQL_MANAGED_CONNECTIONS_FILE'] = $smokeFile
    $psi.EnvironmentVariables['USE_INSIGHTS_LAYER'] = 'false'
    $p = [Diagnostics.Process]::Start($psi)
    try {
        $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"mcpb-smoke","version":"0"}}}')
        $p.StandardInput.Flush()
        $read = $p.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(20000)) { throw 'Smoke test: no MCP initialize response within 20 s.' }
        if ($read.Result -notmatch '"serverInfo"') { throw "Smoke test: unexpected initialize response: $($read.Result)" }
        Write-Host 'Smoke test: initialize OK.' -ForegroundColor Green
    }
    finally {
        if (-not $p.HasExited) { $p.Kill() }
        $p.Dispose()
    }
    Remove-Item -Recurse -Force (Join-Path $staging 'smoke') -ErrorAction SilentlyContinue

    # 4. Manifest (stamped version) and legal files.
    $manifest = Get-Content -Raw "$PSScriptRoot\manifest.json" | ConvertFrom-Json
    $manifest.version = $version
    # No BOM: Windows PowerShell's Set-Content -Encoding utf8 writes one, and the mcpb CLI rejects it.
    [IO.File]::WriteAllText((Join-Path $staging 'manifest.json'), ($manifest | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    Copy-Item "$repoRoot\vscode-extension\LICENSE" (Join-Path $staging 'LICENSE')
    Copy-Item "$repoRoot\THIRD-PARTY-NOTICES.txt" (Join-Path $staging 'THIRD-PARTY-NOTICES.txt')

    # 5. Validate and pack.
    $null = New-Item -ItemType Directory -Force $OutDir
    $bundle = Join-Path $OutDir "apoint-ms-sql-$version.mcpb"
    Invoke-Checked 'mcpb validate' { npx -y "@anthropic-ai/mcpb@$McpbCliVersion" validate (Join-Path $staging 'manifest.json') }
    Invoke-Checked 'mcpb pack' { npx -y "@anthropic-ai/mcpb@$McpbCliVersion" pack $staging $bundle }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $bundle).Hash
    $size = [Math]::Round((Get-Item -LiteralPath $bundle).Length / 1MB, 1)
    Write-Host "Bundle: $bundle ($size MB)" -ForegroundColor Green
    Write-Host "SHA-256: $hash"
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}
