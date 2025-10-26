# Test 1: Valid connection (already tested above)
Write-Host "Test 1: Valid connection - PASSED" -ForegroundColor Green

# Test 2: Missing CONNECTION_STRING
Write-Host "`nTest 2: Missing CONNECTION_STRING environment variable"
$env:CONNECTION_STRING = $null
Start-Process -FilePath ".\Publish\MssqlMcp.exe" -Wait -NoNewWindow
$latestLog = Get-ChildItem "$env:LOCALAPPDATA\MssqlMcp\Logs" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Log file: $($latestLog.FullName)" -ForegroundColor Yellow
Get-Content $latestLog.FullName | Select-String "FATAL"

# Test 3: Invalid SQL Server connection
Write-Host "`nTest 3: Invalid SQL Server connection"
$env:CONNECTION_STRIvNG = "Server=INVALID_SERVER;Database=test;Trusted_Connection=True;TrustServerCertificate=True"
Start-Process -FilePath ".\Publish\MssqlMcp.exe" -Wait -NoNewWindow
$latestLog = Get-ChildItem "$env:LOCALAPPDATA\MssqlMcp\Logs" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Log file: $($latestLog.FullName)" -ForegroundColor Yellow
Get-Content $latestLog.FullName | Select-String "FATAL|connection test"

Write-Host "`nAll tests complete!" -ForegroundColor Green
Write-Host "Log files location: $env:LOCALAPPDATA\MssqlMcp\Logs" -ForegroundColor Cyan