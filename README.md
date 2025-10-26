# Mssql SQL MCP Server (.NET 8)

> This project is forked from [Azure-Samples/SQL-AI-samples](https://github.com/Azure-Samples/SQL-AI-samples) and enhanced with additional database introspection tools.

This project is a .NET 8 console application implementing a Model Context Protocol (MCP) server for MSSQL Databases using the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Requirements

- **Minimum SQL Server Version**: SQL Server 2008 R2 (10.50) or later
- **Supported Versions**:
  - SQL Server 2008 R2
  - SQL Server 2012
  - SQL Server 2014
  - SQL Server 2016
  - SQL Server 2017
  - SQL Server 2019
  - SQL Server 2022
  - Azure SQL Database

## Features

- Provide connection string via environment variable `CONNECTION_STRING`.
- **MCP Tools Implemented**:
  - **Table Operations**:
    - ListTables: List all tables in the database
    - DescribeTable: Get comprehensive table details (schema, columns, indexes, constraints, foreign keys, triggers)
    - CreateTable: Create new tables
    - DropTable: Drop existing tables
  - **Data Operations**:
    - InsertData: Insert data into tables
    - ReadData: Read/query data from tables
    - UpdateData: Update values in tables
    - ExecuteSQL: Execute custom SQL commands (DDL, DML, queries)
  - **Stored Procedures**:
    - ListStoredProcedures: List all stored procedures with descriptions
    - GetStoredProc: Get stored procedure details including parameters and code
  - **Functions**:
    - ListTableFunctions: List all table-valued functions
    - ListScalarFunctions: List all scalar functions
    - GetFunction: Get function details including parameters and code
  - **Views**:
    - ListViews: List all views
    - DescribeView: Get view definition including columns and SQL code
  - **Triggers**:
    - ListTableTriggers: List all table triggers
    - GetTrigger: Get trigger details and SQL code
  - **System Objects**:
    - ListSysObjects: List sys.objects with optional type filtering
  - **Server Information**:
    - GetServerInfo: Get comprehensive SQL Server metadata (version, edition, hardware, database statistics)
- **Logging**:
  - Console logging using Microsoft.Extensions.Logging (stderr)
  - File-based logging with configurable location:
    - **Default**: `%LOCALAPPDATA%\MssqlMcp\Logs\` (Windows) or `~/.local/share/MssqlMcp/Logs/` (Linux/Mac)
    - **Custom**: Set via `LOG_FILE_PATH` environment variable in MCP configuration
  - Startup diagnostics and connection validation
  - Detailed error messages with stack traces
- **Unit Tests**: xUnit-based unit tests for all major components.

## Getting Started

### Prerequisites

- Access to a SQL Server or Azure SQL Database

### Setup

1. **Build ***

---
```sh
   cd MssqlMcp
   dotnet build
```
---


2. VSCode: **Start VSCode, and add MCP Server config to VSCode Settings**

Load the settings file in VSCode (Ctrl+Shift+P > Preferences: Open Settings (JSON)).

Add a new MCP Server with the following settings:

---
```json
    "MSSQL MCP": {
        "type": "stdio",
        "command": "C:\\src\\MssqlMcp\\MssqlMcp\\bin\\Debug\\net8.0\\MssqlMcp.exe",
        "env": {
            "CONNECTION_STRING": "Server=.;Database=test;Trusted_Connection=True;TrustServerCertificate=True",
            "LOG_FILE_PATH": "C:\\Logs\\mssql-mcp.log"
            }
}
```

**Note**: `LOG_FILE_PATH` is optional. You can specify:
- A specific file path: `C:\Logs\mssql-mcp.log`
- A directory path: `C:\Logs\` (creates timestamped files)
- Omit it to use the default location
---

NOTE: Replace the path "C:\\src\\SQL-AI-samples" with the location of your SQL-AI-samples repo on your machine.

e.g. your MCP settings should look like this if "MSSQL MCP" is your own MCP Server in VSCode settings:

---
```json
"mcp": {
    "servers": {
        "MSSQL MCP": {
            "type": "stdio",
            "command": "C:\\src\\SQL-AI-samples\\MssqlMcp\\MssqlMcp\\bin\\Debug\\net8.0\\MssqlMcp.exe",
                "env": {
                "CONNECTION_STRING": "Server=.;Database=test;Trusted_Connection=True;TrustServerCertificate=True",
                "LOG_FILE_PATH": "C:\\Logs\\mssql-mcp.log"
            }
    }
}
```
---

An example of using a connection string for Azure SQL Database:
---
```json
"mcp": {
    "servers": {
        "MSSQL MCP": {
            "type": "stdio",
            "command": "C:\\src\\SQL-AI-samples\\MssqlMcp\\MssqlMcp\\bin\\Debug\\net8.0\\MssqlMcp.exe",
                "env": {
                "CONNECTION_STRING": "Server=tcp:<servername>.database.windows.net,1433;Initial Catalog=<databasename>;Encrypt=Mandatory;TrustServerCertificate=False;Connection Timeout=30;Authentication=Active Directory Interactive"
            }
    }
}
```
---

**Run the MCP Server**

Save the Settings file, and then you should see the "Start" button appear in the settings.json.  Click "Start" to start the MCP Server. (You can then click on "Running" to view the Output window).

Start Chat (Ctrl+Shift+I), make sure Agent Mode is selected.

Click the tools icon, and ensure the "MSSQL MCP" tools are selected.

Then type in the chat window "List tables in the database" and hit enter. (If you have other tools loaded, you may need to specify "MSSQL MCP" in the initial prompt, e.g. "Using MSSQL MCP, list tables").

3. Claude Desktop: **Add MCP Server config to Claude Desktop**

Press File > Settings > Developer.
Press the "Edit Config" button (which will load the claude_desktop_config.json file in your editor).

Add a new MCP Server with the following settings:

---
```json
{
    "mcpServers": {
        "MSSQL MCP": {
            "command": "C:\\src\\SQL-AI-samples\\MssqlMcp\\MssqlMcp\\bin\\Debug\\net8.0\\MssqlMcp.exe",
            "env": {
                    "CONNECTION_STRING": "Server=.;Database=test;Trusted_Connection=True;TrustServerCertificate=True"
                }
        }
    }
}
```
---

Save the file, start a new Chat, you'll see the "Tools" icon, it should list 19 MSSQL MCP tools.

# Troubleshooting

## Quick Diagnosis

**If you get "MCP error -32000: Connection closed" without further details:**

1. Check the log file at:
   - **Custom location**: If you set `LOG_FILE_PATH` in your MCP config, check that location
   - **Default Windows**: `%LOCALAPPDATA%\MssqlMcp\Logs\mssql-mcp-*.log`
   - **Default Linux/Mac**: `~/.local/share/MssqlMcp/Logs/mssql-mcp-*.log`

2. The log file contains:
   - Process and environment information
   - Connection string validation
   - SQL Server connection test results
   - Detailed error messages and stack traces
   - Server startup sequence

3. Common issues and solutions:
   - **Missing CONNECTION_STRING**: Ensure the environment variable is set in your MCP configuration
   - **SQL Server connection failed**: Verify server name, database exists, authentication works
   - **Missing .NET Runtime**: Install .NET 8.0 Runtime from https://dotnet.microsoft.com/download/dotnet/8.0

**For complete troubleshooting steps, see: [TROUBLESHOOTING.md](documentation/****)**

## Other Known Issues

1. If you get a "Task canceled" error using "Active Directory Default", try "Active Directory Interactive".



