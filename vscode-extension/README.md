# MSSQL-MCP

SQL Server for AI agents in VS Code, Cursor and Claude. The extension bundles the MSSQL-MCP server (a .NET 10 single-file exe) and gives you:

- **Named connections** with Windows, SQL login and Microsoft Entra authentication. Passwords are kept in VS Code SecretStorage only.
- **An object explorer** with DDL scripts and a data view.
- **An MCP server for agents**: VS Code agent mode gets it automatically; Cursor, Claude Desktop and Claude Code with one command.
- **Read-only connections** that the server enforces, and an optional **AI Insights** layer that caches what agents learn about your schema.

> **Windows x64 only.** The package is built for `win32-x64`; the bundled server is a Windows executable.

## Features

### Object explorer

The **MSSQL-MCP** view in the activity bar lists your connections. Each open connection shows:

```text
<connection>
├─ Tables
│   └─ <table>  ─ Indexes, Foreign Keys, Triggers
├─ Views
│   └─ <view>   ─ Indexes
├─ Stored Procedures
├─ Table-Valued Functions
├─ Scalar Functions
├─ Database Triggers
├─ Types
└─ Security
    ├─ Logins
    ├─ Server Roles
    ├─ Database Users
    └─ Database Roles
```

- **Show DDL** (inline icon) opens the object's script in a read-only editor. Scripts are written from the catalog views, SSMS style: `CREATE TABLE` with keys, defaults, checks, indexes, foreign keys and the description; `CREATE OR ALTER` (SQL Server 2016 SP1+) or `ALTER` for programmable objects. Anything the script cannot express is listed as `-- WARNING:` lines at the top. Passwords, password hashes and SIDs are never scripted: SQL logins and application roles get the placeholder `N'<password not scripted - set before running>'`.
- **Data View** (inline icon on tables and views) shows the first rows in a grid. The row count is the `msSqlMcp.dataViewRows` setting (default 500, max 10000); when there are more rows, the view says it is truncated.
- **Filter** shows only objects whose name contains the text you type (case-insensitive).
- Closed connections stay in the list; right-click **Open** to browse them again.

### MCP server for agents

VS Code agent mode lists **MSSQL-MCP** as an MCP server automatically. It serves every **open** connection (23 tools: inspection, `read_data`, `execute_sql`, `script_object`, insights, connection management).

**The multi-connection rule.** With exactly one open connection, agents may omit the `connection` argument. With more than one, every tool call **must** name its connection: there is no default connection. The server tells agents this in its instructions, in each tool's description and in `list_connections`, and an unnamed call returns an error that lists the valid names.

When you add, edit, open or close a connection, or change a setting that affects the server, the server definition gets a new version, and VS Code should prompt you to restart the server so agents see the new set. If it does not, restart **MSSQL-MCP** from the MCP server list. A changed password alone does not change the version; restart the server after changing one.

## Setup

1. Install the extension and open the **MSSQL-MCP** view.
2. Click **Add Connection** (`+`) and answer the prompts:
   - **Authentication**: Windows integrated, SQL login (user + password), Microsoft Entra interactive, Microsoft Entra default credential, or a raw connection string (without a password).
   - **Server** (`host`, `host\instance` or `host,port`) and **Database**.
   - **Encryption**:

     | Option | Use it when |
     |--------|-------------|
     | Mandatory (verify certificate) | The server certificate is trusted by this machine. |
     | Mandatory, trust server certificate | Self-signed / on-prem certificates on a trusted network (encrypted, but not protected against a man-in-the-middle). |
     | Optional (no encryption) | Legacy servers only; traffic, including SQL login credentials, may be unencrypted. |
     | Strict (TDS 8) | SQL Server 2022+ with a trusted certificate. |

   - **Read-only?** (default yes) and **AI Insights** for this connection.
3. Right-click the connection and choose **Test Connection**.

The text `${env:` is not accepted in any field, because the server would expand it from its own environment.

## Using it from Cursor, Claude Desktop and Claude Code

Run **Register with Cursor / Claude...** (the view's title bar, or the Command Palette) and pick the clients. The extension:

- copies the server exe to a per-user folder, so the registration survives extension upgrades;
- writes your open connections to a per-user `connections.json` in the extension's storage folder;
- merges an `ms-sql` entry into `~/.cursor/mcp.json` and `%APPDATA%\Claude\claude_desktop_config.json` (and the Microsoft Store copy of Claude Desktop, when present). Each file gets a timestamped `.bak` first, and a file with invalid JSON is never overwritten;
- registers Claude Code with `claude mcp add-json --scope user`. When `claude` is not on `PATH`, it offers a Windows PowerShell command to copy instead.

Restart the client afterwards.

### SQL logins: `MSSQLMCP_PWD_<NAME>` placeholders

When a connection uses a SQL login, you choose how `connections.json` stores its password:

- **Write passwords to a per-user file**: the password is written in clear text to the file (per-user folder). Only after you confirm.
- **Use `${env:}` placeholders** (recommended): the file contains `Password="${env:MSSQLMCP_PWD_<NAME>}"`, and you define that user environment variable yourself. `<NAME>` is the connection name in upper case with every character other than `A-Z`, `0-9` and `_` replaced by `_`, e.g. `Prod-1` becomes `MSSQLMCP_PWD_PROD_1`. The quotes let the password contain `;`, `=` or `"`.

The extension keeps `connections.json` current when your connections change: closing, removing or making a connection read-only always takes effect in the file (restart the client to pick it up). A new connection that needs a new `MSSQLMCP_PWD_<NAME>` variable is left out of the file until you define the variable and choose **Re-register** (the warning names it), so clients never start with an unset variable. When no open connection is left, the file is deleted and registered clients cannot start the server until you open a connection and re-register.

## Read-only and Close

- **Read-only** connections refuse every write tool (`execute_sql`, `insert_data`, `update_data`, `create_table`, `drop_table`, `upsert_insight`, `install_insights_layer`, `refresh_insights`, `rebuild_baseline_insights`) and connect with `ApplicationIntent=ReadOnly`. They make no writes at all, including to the AI Insights tables. This is enforced by the server, not by SQL Server: also use a least-privilege login for anything that must never be written.
- **Close** removes a connection from what agents and the explorer can use, without deleting it. Its saved password is kept; **Open** brings it back. **Remove** deletes the connection and its saved password.

## How the explorer runs

The object explorer never uses the agent's server process. It starts its own private server process in which **every connection is forced read-only**, the AI Insights layer is off and ad-hoc connections are disabled, so browsing, DDL and the data view cannot modify anything. Connection settings inherited from your environment (`CONNECTION_STRING`, `MSSQL_CONNECTIONS_FILE`) are ignored by both processes.

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `msSqlMcp.insights` | `true` | AI Insights layer for the agent server (the explorer never uses it). |
| `msSqlMcp.allowAdhocConnections` | `false` | Lets agents open ad-hoc connections from a raw connection string. The server still refuses Windows / Entra identity, writable and file-attach connections unless the operator enables them (see the server README). |
| `msSqlMcp.dataViewRows` | `500` | Rows loaded by Data View (1-10000). |
| `msSqlMcp.serverPath` | bundled | Path to a different `MssqlMcp.exe`. |
| `msSqlMcp.logLevel` | `error` (installed) | Output channel verbosity: `off`, `error`, `warn`, `info`, `debug`, `trace`. |

## Privacy

- **Principal names are visible to agents.** Logins, users and roles are listed and scripted by the explorer and by the agent tools. They are often personal names (for example Active Directory accounts). Passwords, hashes and SIDs are never returned.
- **The data view is local only.** Rows are shown in the editor and are not sent anywhere; there is no export.
- **Agents see what they query.** Data returned by `read_data` goes to the AI model the agent uses. Use read-only connections and least-privilege logins for databases with personal data.
- **Trace logging.** At `msSqlMcp.logLevel` = `trace`, the Output channel records tool arguments and results, which may include SQL text and object definitions (for `read_data` only the row count is logged). Use `trace` only for troubleshooting and clear the channel afterwards.
- Passwords are never written to settings, logs, the Output channel, tree labels or DDL documents.

## License

MIT. See `LICENSE`.
