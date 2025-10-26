# MSSQL MCP Server Troubleshooting Guide

## Overview
This guide helps you diagnose and resolve connection issues with the MSSQL MCP Server.

## Log File Location

The MCP server now creates detailed log files at:
- **Windows**: `%LOCALAPPDATA%\MssqlMcp\Logs\mssql-mcp-YYYY-MM-DD-HHMMSS.log`
- **Linux/Mac**: `~/.local/share/MssqlMcp/Logs/mssql-mcp-YYYY-MM-DD-HHMMSS.log`

Example Windows path: `C:\Users\YourUsername\AppData\Local\MssqlMcp\Logs\mssql-mcp-2025-10-26-150342.log`

**IMPORTANT**: Always check this log file first when troubleshooting connection issues!

## Common Error: "MCP error -32000: Connection closed"

This generic error can have several causes. The log file will contain the specific reason.

## Debugging Steps

### Step 1: Locate the Log File

1. Open File Explorer (Windows) or Terminal (Linux/Mac)
2. Navigate to the log directory:
   - **Windows**: Press `Win+R`, type `%LOCALAPPDATA%\MssqlMcp\Logs`, press Enter
   - **Linux/Mac**: `cd ~/.local/share/MssqlMcp/Logs`
3. Open the most recent log file (sorted by timestamp)

### Step 2: Review Startup Information

The log file contains critical information:
```
================================================================================
MSSQL MCP Server Starting - 2025-10-26 15:03:42
================================================================================
Process ID: 12345
Working Directory: C:\Development\MCPs\MS-SQL\Publish
Log File: C:\Users\...\AppData\Local\MssqlMcp\Logs\mssql-mcp-2025-10-26-150342.log
Assembly Location: C:\Development\MCPs\MS-SQL\Publish\MssqlMcp.exe
.NET Version: 8.0.11
OS Version: Microsoft Windows NT 10.0.20348.0
Machine Name: SERVER01
User: daniel.dev
Command Line Args: 
```

### Step 3: Check for Common Issues

#### Issue 1: Missing CONNECTION_STRING
**Symptom in log:**
```
Connection String: NOT SET - This will cause connection failures!
FATAL: CONNECTION_STRING environment variable is not set!
```

**Solution:**
The `CONNECTION_STRING` environment variable was not passed to the MCP server. Update your `.roo/mcp.json`:

```json
{
  "mcpServers": {
    "MSSQL TEST MCP": {
      "type": "stdio",
      "command": "C:\\Path\\To\\MssqlMcp.exe",
      "env": {
        "CONNECTION_STRING": "Server=YOUR_SERVER;Database=YOUR_DB;Trusted_Connection=True;TrustServerCertificate=True"
      }
    }
  }
}
```

#### Issue 2: SQL Server Connection Failed
**Symptom in log:**
```
Testing SQL Server connection...
FATAL: SQL Server connection test FAILED: A network-related or instance-specific error occurred...
Connection String (masked): Server=DC\DEV14; Database=DevERP; Trusted_Connection=True; TrustServerCertificate=True
```

**Common causes:**
1. **SQL Server not running** - Start SQL Server service
2. **Wrong server name** - Verify server name with `.\SQLCMD -L` or SQL Server Configuration Manager
3. **Firewall blocking connection** - Check Windows Firewall settings
4. **Authentication failure** - Ensure Windows Authentication is enabled or provide SQL credentials
5. **Database doesn't exist** - Create the database or change to an existing one

**Solutions:**

**For Windows Authentication:**
```json
"CONNECTION_STRING": "Server=SERVERNAME\\INSTANCENAME;Database=YourDB;Trusted_Connection=True;TrustServerCertificate=True"
```

**For SQL Server Authentication:**
```json
"CONNECTION_STRING": "Server=SERVERNAME;Database=YourDB;User Id=sa;Password=YourPassword;TrustServerCertificate=True"
```

**For LocalDB:**
```json
"CONNECTION_STRING": "Server=(localdb)\\MSSQLLocalDB;Database=YourDB;Integrated Security=true;TrustServerCertificate=True"
```

#### Issue 3: Missing .NET Runtime
**Symptom:**
- No log file created
- Process starts and immediately closes
- Windows error: "This application requires .NET Runtime 8.0"

**Solution:**
1. Download and install .NET 8.0 Runtime from: https://dotnet.microsoft.com/download/dotnet/8.0
2. Choose "Run desktop apps" or "Run console apps" runtime
3. Restart the MCP client after installation

#### Issue 4: Permission Denied
**Symptom in log:**
```
Failed to create log file: Access to the path '...' is denied.
```

**Solution:**
Ensure the user running the MCP server has write permissions to `%LOCALAPPDATA%` or `~/.local/share`

#### Issue 5: Wrong Architecture
**Symptom:**
- Process fails to start
- Error: "This assembly is built by a runtime newer than the currently loaded runtime"

**Solution:**
Ensure you're using a 64-bit version of .NET Runtime if the MCP server is compiled for x64.

## Testing Connection Manually

### Test 1: Verify .NET Runtime
```cmd
dotnet --version
```
Should show version 8.0.x or higher.

### Test 2: Test SQL Connection with SQLCMD
```cmd
sqlcmd -S SERVERNAME\INSTANCENAME -E -Q "SELECT @@VERSION"
```

### Test 3: Run MCP Server Manually
```cmd
cd C:\Path\To\Publish\Directory
set CONNECTION_STRING=Server=YOUR_SERVER;Database=YOUR_DB;Trusted_Connection=True;TrustServerCertificate=True
MssqlMcp.exe
```

If it starts successfully, you should see stderr output indicating the connection test passed.

## Log File Lifecycle

The server logs the following stages:
1. **Startup** - Process info, environment details
2. **Connection String Validation** - Checks if environment variable is set
3. **SQL Connection Test** - Validates actual SQL Server connectivity
4. **MCP Server Initialization** - Registers services and tools
5. **Host Building** - Builds the hosting infrastructure
6. **Server Running** - Indicates the server is accepting connections
7. **Shutdown** - Graceful or error shutdown with exit code

## Exit Codes

- `0` - Normal, graceful shutdown
- `1` - Error occurred (check log file for details)

## Advanced Debugging

### Enable Detailed SQL Client Logging

Add to your connection string:
```
;Log=C:\Temp\sqlclient.log
```

### Capture All stderr Output

When running manually, redirect stderr to a file:
```cmd
MssqlMcp.exe 2> debug-output.txt
```

### Check Process with Task Manager/Process Explorer

1. Start the MCP client
2. Open Task Manager
3. Look for `MssqlMcp.exe` process
4. If it's not running, the process crashed - check the log file
5. If it's running but not responding, there may be a stdio communication issue

## Getting Help

When requesting help, provide:
1. Full log file contents from `%LOCALAPPDATA%\MssqlMcp\Logs\`
2. Your `.roo/mcp.json` configuration (with passwords masked)
3. SQL Server version: `SELECT @@VERSION`
4. .NET Runtime version: `dotnet --version`
5. Operating system version

## Changelog

### 2025-10-26 - Enhanced Logging
- Added file-based logging to `%LOCALAPPDATA%\MssqlMcp\Logs\`
- Added startup diagnostics (process info, environment)
- Added pre-flight SQL connection validation
- Added connection string masking for security
- Added detailed error messages with stack traces
- Added exit code logging