# Publishes a self-contained, single-file Release build to C:\Development\MCPs\MS-SQL-Release\
# (see MssqlMcp\Properties\PublishProfiles\ReleaseSingleFile.pubxml to change RID or output path).
# Usage: .\publish-release.ps1
# Extra args are forwarded to dotnet publish (e.g. .\publish-release.ps1 --verbosity normal).

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
dotnet publish "$repoRoot\MssqlMcp\MssqlMcp.csproj" `
    -c Release `
    -p:PublishProfile=ReleaseSingleFile `
    @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Done. Executable: C:\Development\MCPs\MS-SQL-Release\MssqlMcp.exe" -ForegroundColor Green
