@echo off
rem Starts MssqlMcp.exe for the Claude Code plugin. stdout is the MCP protocol stream: nothing else may write to it.
rem Connections: set CONNECTION_STRING, MSSQL_CONNECTIONS or MSSQL_CONNECTIONS_FILE in your environment. Without any of
rem them the server uses the connection manager file shared with Claude Desktop (%APPDATA%\APoint-ms-sql\connections.json).
setlocal
rem powershell.exe gets nul as stdin: the MCP stdin pipe belongs to the exe.
set "MSSQLMCP_EXE="
for /f "usebackq delims=" %%P in (`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0ensure-exe.ps1" ^<nul`) do set "MSSQLMCP_EXE=%%P"
if not defined MSSQLMCP_EXE exit /b 1

if not defined CONNECTION_STRING if not defined MSSQL_CONNECTIONS if not defined MSSQL_CONNECTIONS_FILE if not defined MSSQL_MANAGED_CONNECTIONS_FILE set "MSSQL_MANAGED_CONNECTIONS_FILE=%APPDATA%\APoint-ms-sql\connections.json"
if not defined MSSQL_ALLOW_ADHOC_CONNECTIONS set "MSSQL_ALLOW_ADHOC_CONNECTIONS=false"

"%MSSQLMCP_EXE%" %*
exit /b %ERRORLEVEL%
