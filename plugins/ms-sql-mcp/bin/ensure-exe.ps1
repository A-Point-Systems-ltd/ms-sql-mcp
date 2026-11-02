# Makes sure the MssqlMcp.exe named in exe.json is in %LOCALAPPDATA%\APoint\ms-sql-mcp\<version>\ and prints its path.
# Called by start.cmd. stdout carries only the path (start.cmd reads it); messages go to stderr, which Claude Code logs.
# The download is checked against the SHA-256 in exe.json before it is used; a mismatch is deleted and never run.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

try {
    $spec = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'exe.json') | ConvertFrom-Json
    $root = Join-Path $env:LOCALAPPDATA 'APoint\ms-sql-mcp'
    $dir = Join-Path $root $spec.version
    $exe = Join-Path $dir 'MssqlMcp.exe'

    if (Test-Path -LiteralPath $exe) {
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash -eq $spec.sha256) { Write-Output $exe; exit 0 }
        [Console]::Error.WriteLine("ms-sql-mcp: $exe does not match the expected SHA-256; downloading it again.")
        Remove-Item -LiteralPath $exe -Force
    }

    $null = New-Item -ItemType Directory -Force $dir
    $tmp = Join-Path $dir ("MssqlMcp.{0}.download" -f [Guid]::NewGuid().ToString('N'))
    [Console]::Error.WriteLine("ms-sql-mcp: downloading MssqlMcp.exe $($spec.version) from $($spec.url)")
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $spec.url -OutFile $tmp
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $tmp).Hash
        if ($hash -ne $spec.sha256) { throw "SHA-256 mismatch: expected $($spec.sha256), got $hash." }
        # Another session may have finished the same download first; its copy has the same hash.
        if (-not (Test-Path -LiteralPath $exe)) { Move-Item -LiteralPath $tmp -Destination $exe }
    }
    finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } }

    # Older versions: best effort (a running exe is locked and stays until next time).
    Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -ne $spec.version } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Output $exe
}
catch {
    [Console]::Error.WriteLine("ms-sql-mcp: could not prepare MssqlMcp.exe: $($_.Exception.Message)")
    exit 1
}
