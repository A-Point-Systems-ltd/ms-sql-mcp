# MSSQL MCP Server - Debugging Quick Reference

## Log File Location

### Default Location

**Windows**: `C:\Users\[USERNAME]\AppData\Local\MssqlMcp\Logs\`
**Quick Access**: Press `Win+R`, type `%LOCALAPPDATA%\MssqlMcp\Logs`, press Enter

**Linux/Mac**: `~/.local/share/MssqlMcp/Logs/`

### Custom Log Location (NEW Feature)

You can specify a custom log file path using the `LOG_FILE_PATH` environment variable in your MCP server configuration:

```json
{
  "mcpServers": {
    "MSSQL MCP": {
      "command": "path/to/MssqlMcp.exe",
      "env": {
        "CONNECTION_STRING": "Server=...;Database=...;...",
        "LOG_FILE_PATH": "C:\\MyLogs\\mssql-mcp.log"
      }
    }
  }
}
```

**LOG_FILE_PATH Options:**
- **Specific file**: `C:\MyLogs\mssql-mcp.log` - Always writes to this file
- **Directory**: `C:\MyLogs\` - Creates timestamped files (mssql-mcp-YYYY-MM-DD-HHMMSS.log)
- **Omitted**: Uses default location (%LOCALAPPDATA%\MssqlMcp\Logs\)

## Log File Format

Files are named: `mssql-mcp-YYYY-MM-DD-HHMMSS.log`

Example: `mssql-mcp-2025-10-26-133454.log`

## What the Log Contains

1. **Startup Information** (always logged):
   - Process ID
   - Working directory
   - Assembly location (exe/dll path)
   - .NET version
   - OS version
   - Machine name and user
   - Connection string (passwords masked)

2. **Connection Validation** (always checked):
   - CONNECTION_STRING environment variable presence
   - SQL Server connection test
   - Server name and database name on success
   - Detailed error with stack trace on failure

3. **Server Lifecycle** (tracked throughout):
   - MCP server initialization
   - Host building
   - Server running status
   - Shutdown status
   - Exit code

## PowerShell Commands

### View Latest Log
```powershell
Get-ChildItem $env:LOCALAPPDATA\MssqlMcp\Logs | Sort LastWriteTime -Desc | Select -First 1 | Get-Content
```

### Open Logs Folder
```powershell
explorer $env:LOCALAPPDATA\MssqlMcp\Logs
```

### Test Debug Build
```powershell
$env:CONNECTION_STRING="Server=YOUR_SERVER;Database=YOUR_DB;Trusted_Connection=True;TrustServerCertificate=True"
$env:LOG_FILE_PATH="C:\Temp\mcp-debug.log"
.\MssqlMcp\bin\Debug\net8.0\MssqlMcp.exe
```

### Test Release Build
```powershell
$env:CONNECTION_STRING="Server=YOUR_SERVER;Database=YOUR_DB;Trusted_Connection=True;TrustServerCertificate=True"
$env:LOG_FILE_PATH="C:\Temp\mcp-release.log"
.\Publish\MssqlMcp.exe
```

### Test with Custom Log Directory
```powershell
$env:CONNECTION_STRING="Server=YOUR_SERVER;Database=YOUR_DB;Trusted_Connection=True;TrustServerCertificate=True"
$env:LOG_FILE_PATH="C:\MyLogs\"
.\Publish\MssqlMcp.exe
```

## Common Error Patterns

### Missing CONNECTION_STRING
```
Connection String: NOT SET - This will cause connection failures!
FATAL: CONNECTION_STRING environment variable is not set!
```
**Fix**: Add `CONNECTION_STRING` to your MCP configuration's `env` section

### SQL Connection Failed
```
Testing SQL Server connection...
FATAL: SQL Server connection test FAILED: A network-related or instance-specific error...
Connection String (masked): Server=DC\DEV14; Database=DevERP; Trusted_Connection=True
```
**Fix**: Verify server name, ensure SQL Server is running, check firewall

### No Log File Created
**Symptom**: No log files in the folder  
**Cause**: Process crashed before logging started, or .NET runtime missing  
**Fix**: Install .NET 8.0 Runtime from https://dotnet.microsoft.com/download/dotnet/8.0

## Tested Configurations

✅ **Debug Build**: `C:\Development\MCPs\MS-SQL\MssqlMcp\bin\Debug\net8.0\MssqlMcp.exe`  
✅ **Release Build**: `C:\Development\MCPs\MS-SQL\Publish\MssqlMcp.exe`  
✅ **Connection Validation**: Pre-flight SQL connection test before MCP server starts  
✅ **Startup Logging**: Complete environment and process information  
✅ **Error Logging**: Detailed errors with stack traces and masked credentials  

## Next Steps for Troubleshooting

1. Run the MCP server with your configuration
2. Check the log file at `%LOCALAPPDATA%\MssqlMcp\Logs`
3. Look for "FATAL" errors or "connection test FAILED"
4. Follow the solutions in [TROUBLESHOOTING.md](****)
5. If still stuck, provide the log file contents when requesting help

## Build Date
2025-10-26