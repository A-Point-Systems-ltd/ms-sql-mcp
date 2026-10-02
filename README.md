# MSSQL MCP Server (.NET 10)

A [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server for Microsoft SQL Server and Azure SQL Database. Built with the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) (`ModelContextProtocol` 2.2.0) on .NET 10.

Forked from [Azure-Samples/SQL-AI-samples](https://github.com/Azure-Samples/SQL-AI-samples) and extended with unified object introspection, strict read/write SQL routing, and an optional **AI Insights** cache layer that helps LLM agents investigate databases with less repeated schema work.

## Table of contents

- [Requirements](#requirements)
- [Quick start](#quick-start)
- [MCP client configuration](#mcp-client-configuration)
- [Environment variables](#environment-variables)
- [Multiple connections](#multiple-connections)
- [MCP tools reference](#mcp-tools-reference)
- [AI Insights layer](#ai-insights-layer)
- [Response shape](#response-shape)
- [Build, publish, and test](#build-publish-and-test)
- [Project layout](#project-layout)
- [Security notes](#security-notes)
- [Troubleshooting](#troubleshooting)
- [License](#license)

## Requirements

| Component | Version |
|-----------|---------|
| .NET SDK / Runtime | **.NET 10.0** |
| SQL Server | **2008 R2 (10.50)** or later |
| Azure SQL Database | Supported |

Tested target versions include SQL Server 2008 R2 through 2022 and Azure SQL Database.

With a single `CONNECTION_STRING` (legacy mode) the server validates it and opens a test connection **before** starting the MCP transport. If either check fails, the process exits with code `1` and writes diagnostics to the log file. With several connections, startup behaves differently; see [Multiple connections](#multiple-connections).

## Quick start

```powershell
# Clone and build
cd MssqlMcp
dotnet build

# Run unit tests (no live DB required for most tests)
cd ..
dotnet test
```

Point your MCP client at the built executable:

```
MssqlMcp\bin\Debug\net10.0\MssqlMcp.exe
```

Set `CONNECTION_STRING` (or `MSSQL_CONNECTIONS`, see [Multiple connections](#multiple-connections)) in the MCP server environment (see [sample_mcp.json](sample_mcp.json) for a template).

**First prompt to try:** “List tables in the database” (the agent should call `list_objects` with `objectType=Table`).

## MCP client configuration

### Cursor

Add a project-level or user-level MCP config. Example (adjust paths and connection string):

```json
{
  "mcpServers": {
    "MSSQL-MCP": {
      "type": "stdio",
      "command": "C:\\Development\\MCPs\\MS-SQL\\MssqlMcp\\bin\\Debug\\net10.0\\MssqlMcp.exe",
      "env": {
        "CONNECTION_STRING": "Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True",
        "USE_INSIGHTS_LAYER": "true",
        "INSIGHTS_AUTOPOPULATE": "true",
        "LOG_FILE_PATH": "C:\\Logs\\mssql-mcp.log"
      }
    }
  }
}
```

Restart the MCP server after changing environment variables.

### VS Code (GitHub Copilot Agent)

Open **Settings (JSON)** and add under `mcp.servers`:

```json
"mcp": {
  "servers": {
    "MSSQL MCP": {
      "type": "stdio",
      "command": "C:\\path\\to\\MssqlMcp.exe",
      "env": {
        "CONNECTION_STRING": "Server=.;Database=test;Trusted_Connection=True;TrustServerCertificate=True"
      }
    }
  }
}
```

### Claude Desktop

Edit `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "MSSQL MCP": {
      "command": "C:\\path\\to\\MssqlMcp.exe",
      "env": {
        "CONNECTION_STRING": "Server=.;Database=test;Trusted_Connection=True;TrustServerCertificate=True"
      }
    }
  }
}
```

### Azure SQL (Entra ID interactive)

```json
"CONNECTION_STRING": "Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<database>;Encrypt=Mandatory;TrustServerCertificate=False;Connection Timeout=30;Authentication=Active Directory Interactive"
```

### Production-style single-file publish

```powershell
.\publish-release.ps1
```

This produces a self-contained `MssqlMcp.exe` (default output: `C:\Development\MCPs\MS-SQL-Release\`). Point MCP clients at that executable instead of the Debug build.

## Environment variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `CONNECTION_STRING` | One of the three connection variables | — | ADO.NET connection string for a single database (legacy mode, profile name `default`). Validated at startup; the process exits with code `1` if it cannot connect. |
| `MSSQL_CONNECTIONS` | One of the three connection variables | — | JSON array of named connections. See [Multiple connections](#multiple-connections). |
| `MSSQL_CONNECTIONS_FILE` | One of the three connection variables | — | Path to a file holding the same JSON array. |
| `MSSQL_ALLOW_ADHOC_CONNECTIONS` | No | disabled | Set to `true` to let `open_connection` register new connections from a raw connection string at runtime. |
| `MSSQL_ADHOC_ALLOW_INTEGRATED_AUTH` | No | disabled | Set to `true` to allow ad-hoc connections that use `Integrated Security` / `Trusted_Connection` or any `Authentication=Active Directory*` method (they send the server's own identity to the target host). |
| `MSSQL_ADHOC_ALLOW_WRITE` | No | disabled | Set to `true` to allow `open_connection` with `readOnly=false`. Without it, writable ad-hoc connections are refused. |
| `MSSQL_ADHOC_ALLOWED_HOSTS` | No | any host | Comma-separated host names. When set, ad-hoc connections are refused unless the host part of `Data Source` (without `tcp:`, instance or port) matches one of them, case-insensitively. |
| `USE_INSIGHTS_LAYER` | No | enabled | Opt-**out** switch. Set to `false`, `0`, `no`, `off`, or `disabled` to disable the AI Insights layer. Any other value (including unset) leaves it enabled. |
| `INSIGHTS_AUTOPOPULATE` | No | enabled | Opt-out. When enabled (and insights layer is on), introspection auto-creates mechanical baseline insights and attaches enrichment directives. Set to a falsey value to disable auto-population only. |
| `MSSQL_SCRIPT_RUNNER` | No | disabled | Set to `true` to register the extension-only `run_script`, `ddl_history` and `language_service` tools. For the VS Code extension's private runner process only; do not enable it for agent clients. See [Script runner (extension only)](#script-runner-extension-only), [DDL history (extension only)](#ddl-history-extension-only) and [IntelliSense (extension only)](#intellisense-extension-only). |
| `MSSQL_CONSOLE_LOG_LEVEL` | No | `Warning` | Minimum level of the console logger, which writes to stderr (stdout carries the MCP protocol). One of `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None` (case-insensitive). Some clients (Cursor) show every stderr line as `[error]`, so the default keeps routine `info:` lines out of their logs. When it is not set, the standard .NET configuration applies if it sets a level for the console: `Logging:Console:LogLevel:Default`, then `Logging:LogLevel:Default` (from `appsettings.json` or env such as `Logging__LogLevel__Default`); otherwise `Warning`. An invalid value writes one warning line to stderr and is ignored (the configuration, or `Warning`, applies). `FATAL:` startup messages are always written. The `LOG_FILE_PATH` log is not affected. |
| `LOG_FILE_PATH` | No | `%LOCALAPPDATA%\MssqlMcp\Logs\` (Windows) or `~/.local/share/MssqlMcp/Logs/` (Linux/macOS) | Full file path, or a directory (timestamped log files are created inside it). |

When both `USE_INSIGHTS_LAYER` and `INSIGHTS_AUTOPOPULATE` are enabled, the server also enables baseline row-count probing, baseline refresh during DDL scans, and `insightEnrichment` response directives (all derived from those two flags — there are no separate env vars for them).

## MCP tools reference

The server exposes **23 tools** through a single partial `Tools` class (plus the opt-in, extension-only `run_script`, `ddl_history` and `language_service`; see [Script runner (extension only)](#script-runner-extension-only)). MCP wire names are **snake_case** (pinned explicitly in `MssqlMcp/ToolNames.cs`). Legacy per-type list/get helpers (`ListTables`, `GetStoredProc`, etc.) remain as internal C# methods; clients should use the unified tools below.

> **Breaking change (.NET 10 / MCP SDK 2.x upgrade):** tool names changed from PascalCase (`ReadData`, `ExecuteSQL`, …) to snake_case (`read_data`, `execute_sql`, …). Update client tool allow-lists, auto-approve rules and saved prompts that reference the old names.

### Read-only inspection

| Tool | Purpose |
|------|---------|
| **list_objects** | List objects by `objectType`: `Table`, `View`, `StoredProcedure`, `TableFunction`, `ScalarFunction`, `Function` (scalar + table), `TableTrigger`, `SysObject`, `DatabaseTrigger`, `Type` (user-defined alias/table types), `Login`, `ServerRole`, `DatabaseUser`, `DatabaseRole`. **Privacy:** the security types return principal names (logins and users often identify people) and role membership to the calling agent and its LLM provider; passwords, hashes and SIDs are never returned. Optional `partialName` does a `LIKE` filter on name and `schema.name`. For `SysObject`, optional `sysObjectType` filters by `sys.objects.type` (`U`, `V`, `P`, `FN`, …). |
| **describe_table** | Full table metadata: columns (type, nullability, descriptions), indexes, constraints, foreign keys, triggers. Preferred over ad-hoc `sys.*` queries for one table. |
| **describe_view** | View metadata, column list, indexes (`name`, `type`, `isUnique`, `keys`), and full T-SQL definition. |
| **script_object** | Ready-to-run T-SQL DDL for one object: `Table`, `View`, `Index` (with `parent`), `ForeignKey`, `TableTrigger`, `StoredProcedure`, `TableFunction`, `ScalarFunction`, `DatabaseTrigger`, `Type`, `Login`, `ServerRole`, `DatabaseUser`, `DatabaseRole`. Returns `{ objectType, schema, name, form, ddl, warnings }`; `form` is `Create`, `CreateOrAlter` (SQL Server 2016 SP1+) or `Alter` for programmable objects. The optional `form` parameter (`create` by default, or `alter`) returns a `View`, `StoredProcedure`, `TableFunction`, `ScalarFunction`, `TableTrigger` or `DatabaseTrigger` as `CREATE OR ALTER` (2016 SP1+) or `ALTER` (older servers), ready to edit and re-run; for a view with indexes the script carries a warning, because altering an indexed view drops its indexes (the index statements in the script recreate them). Any other type with `form: "alter"`, or any other `form` value, is an error. Passwords, hashes and SIDs are never scripted (placeholders instead); unsupported features (partitioning, compression, XML/columnstore indexes, encrypted modules, ...) are listed in `warnings`. Principal names are returned (see privacy note above). Read-only. |
| **get_object** | Stored procedure, function, or trigger: parameters (where applicable) + definition. `objectType`: `StoredProcedure`, `Function`, or `Trigger`. Trigger names accept `name`, `schema.name`, or `schema.table.name`. |
| **read_data** | **All** read-only `SELECT` / `WITH … SELECT` queries. Use for user tables, `sys.*`, `INFORMATION_SCHEMA`, and DMVs. |
| **get_server_info** | Server version/edition, hardware DMVs (with graceful degradation), and user-database counts. |

### Data modification and DDL

| Tool | MCP flags | Purpose |
|------|-----------|---------|
| **create_table** | write | Run a `CREATE TABLE` statement. |
| **drop_table** | write, destructive | Run a `DROP TABLE` statement. Confirm with the user first. |
| **insert_data** | write | Run a single `INSERT` statement. |
| **update_data** | write, destructive | Run a single `UPDATE` statement. Always use a `WHERE` clause. |
| **execute_sql** | write, destructive | DDL/DML only (`INSERT`, `UPDATE`, `DELETE`, `MERGE`, `CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `EXEC`, …). **SELECT is rejected** — use `read_data`. Multi-batch `GO` scripts are not supported. |

### AI Insights (8 tools)

Requires `USE_INSIGHTS_LAYER` enabled (default) and **install_insights_layer** run once per database.

| Tool | Purpose |
|------|---------|
| **insights_check** | Reports layer install state, DDL audit presence, insight row count, and DDL processing watermark. |
| **install_insights_layer** | Idempotent install of `AIInsights` schema, `SchemaInsights`, `InsightHistory`, `DdlChangeWatermark`, `dbo.DDL_AuditLog`, and the database-level `DDL_Audit` trigger. |
| **get_insight** | Read cached insight + freshness for one object. |
| **upsert_insight** | Create or update a cached insight (UPDATE-then-INSERT; fingerprint captured server-side). |
| **list_insights** | Recent rows from `AIInsights.SchemaInsights`. |
| **get_insight_history** | Archived rows from `AIInsights.InsightHistory`. |
| **refresh_insights** | Process DDL backlog / fingerprint drift; returns recent summaries. |
| **rebuild_baseline_insights** | Bulk warm mechanical baselines (`schemaName?`, `objectType?`, `take` 1–2000). |

When `USE_INSIGHTS_LAYER=false`, insight-specific tools return errors or empty status; introspection tools skip insight enrichment.

### Connection management (3 tools)

| Tool | MCP flags | Purpose |
|------|-----------|---------|
| **list_connections** | read-only | Lists every registered connection: name, open/closed, read-only, server, database (never credentials), plus `connectionRequired` and `count`. Call it first in a session. |
| **open_connection** | write | Reopens a configured connection, or (only with `MSSQL_ALLOW_ADHOC_CONNECTIONS=true`) registers an ad-hoc one from `connectionString`. Ad-hoc connections are read-only unless `readOnly=false` (which needs `MSSQL_ADHOC_ALLOW_WRITE=true`); see [ad-hoc limits](#5-mssql_allow_adhoc_connections). The connection is tested (5 s cap) before it is registered. |
| **close_connection** | write | Closes a connection. Ad-hoc connections are forgotten; configured ones stay listed as closed and can be reopened. The last open connection cannot be closed. |

These three tools take no `connection` argument. Every other tool accepts an optional `connection` argument; see [Multiple connections](#multiple-connections).

### Read vs execute routing

`SqlStatementClassifier` parses every statement with the T-SQL parser (`Microsoft.SqlServer.TransactSql.ScriptDom`) and enforces a strict split. Every tool accepts **exactly one statement**; T-SQL needs no `;` between statements, so text such as `SELECT 1 WAITFOR DELAY '…'` counts as two statements and is rejected.

- **read_data** — a single `SELECT` / `WITH … SELECT`. `SELECT … INTO`, `OPENQUERY` / `OPENROWSET` / `OPENDATASOURCE` and linked-server (4-part) names are rejected. The query runs inside a transaction that is always rolled back. At most `maxRows` rows are returned (default 500, max 10000). `data` is always the row array; when more rows exist the response also carries top-level `truncated: true` and `maxRows`.
- **insert_data / update_data / create_table / drop_table** — only the matching statement type (`INSERT`, `UPDATE`, `CREATE TABLE`, `DROP TABLE`) is accepted, so `insert_data("DROP TABLE x")` is refused.
- **execute_sql** — single DDL/DML statements (including `SELECT … INTO`; a `CREATE PROCEDURE` body counts as one statement). Any plain `SELECT` is rejected with a message pointing to `read_data`. `SET`, `DECLARE`, `USE`, `WAITFOR` and `SHUTDOWN` are rejected as unsupported.

This keeps destructive operations behind an explicitly flagged tool and prevents accidental full-table reads through the write path.

### Script runner (extension only)

`run_script` is the query-window runner of the MSSQL-MCP VS Code extension. It is **not** one of the 23 agent tools: the server lists it only when the process environment has `MSSQL_SCRIPT_RUNNER=true`. **Do not enable it for agent clients** (Cursor, Copilot, Claude Desktop): it runs arbitrary multi-statement scripts on read/write connections.

- Arguments: `script` (required), `maxRows` (per result set, default 1000, clamped to 1..10000), `connection`.
- Splits the script on SSMS-style `GO` lines (`GO n` repeats a batch; `GO` inside strings, comments or `[identifiers]` does not split) and runs every batch on one session, so `SET` options and `#temp` tables carry across batches.
- Returns `data: { resultSets, messages, hadErrors, batches, elapsedMs }`. Each result set has `batch`, `columns` (`name`, `type`), `rows` (arrays in column order), `rowCount` (total) and `truncated`. Messages have `kind` (`info`, `rows`, `error`, `warning`), `text` and `line` (1-based script line, or null). Errors that name a procedure, function, view or trigger use the SSMS header `Msg n, Level l, State s, Procedure p, Line n`. When the batch is that module's own `CREATE` / `ALTER` (a compile error), the line is mapped to the script; otherwise (for example an error raised inside an `EXEC`'d procedure) it is the module's own line number and `line` is null. SQL errors are `error` messages and execution continues with the next batch, like SSMS; `success: false` only for an empty script or a connection that cannot be opened.
- **New session per run.** Each call opens its own connection outside the connection pool (`Pooling=false`) and closes it at the end, so session state from an earlier run (`SET TRANSACTION ISOLATION LEVEL`, other `SET` options, `sp_setapprole`, `EXECUTE AS` without `REVERT`) never reaches the next one. Within one run, all batches share that session.
- **Size caps.** A cap never stops execution: the script runs to the end, only what is kept and returned is limited, and errors are never lost silently (see the Errors row). Each cap adds one `warning` per run.

  | Cap | Limit | After the cap |
  |---|---|---|
  | Rows | 50000 per run, across all result sets | Result sets keep their columns and `rowCount` but have no rows, and `truncated: true`. |
  | Response size | about 32 MB per run (rendered cell or message text length x2 + 16 bytes each) | Later rows are counted in `rowCount` but not kept (`truncated: true`), and later `info` / `rows` messages are dropped. Errors and warnings are still kept: they do not need room in the budget. |
  | Result sets | 200 per run | Later result sets are read to the end but not returned. |
  | Messages | 10000 per run | Later `info` and `rows` messages are dropped; `error` and `warning` messages are kept. |
  | Errors | 1000 per run | After the first 1000 errors only the most recent one is kept (it replaces the previous one), after the warning `Error limit of 1000 reached; further errors were dropped (the last one is shown).` `hadErrors` stays true. |
  | String value | 65536 characters | Cut, with the suffix `… (truncated, N chars)` (N = full length). |
  | Binary value | 32768 bytes | Cut before hex encoding, with the suffix `… (truncated, N bytes)`. |
  | `GO n` | n at most 10000 | That batch is not run and gets an `error` message; later batches still run. |

- **Value formats.** Values that a JSON number (a double in the extension) would round are sent as exact strings: `decimal` / `numeric`, `money` / `smallmoney` (invariant culture, for example `"12345678901234.5678"`), and `bigint` values outside +/-2^53 (`"9223372036854775807"`). Smaller `bigint` values, `int`, `float` and `bit` stay JSON numbers or booleans; dates, times and `uniqueidentifier` are strings as before. Binary values (`varbinary`, `binary`, `timestamp`) are SSMS-style `0x` + uppercase hex (`"0x00FF10"`), no longer base64.
- **Cancel.** Cancelling the call (the client sends `notifications/cancelled` for its request id; the extension does this for Cancel and when the tab closes) cancels the running command on SQL Server, rolls back a transaction the script left open, and closes the session, so its locks are released. Statements that already completed outside a transaction, and transactions the script already committed, stay applied.
- On a read-only connection every batch must be a single read-only `SELECT` (the `read_data` rules) and runs inside a transaction that is always rolled back; other batches are refused with an `error` message that starts with `Read-only connection: only a single read-only SELECT per batch can run here.` and gives a short reason (no agent-tool advice). Comment-only batches are skipped silently there.
- When `MSSQL_SCRIPT_RUNNER` is not set, `run_script` is an unknown tool (no connection routing either).
- On a read/write connection, a transaction the script leaves open is rolled back at the end with a `warning`: each run uses a new session, so `COMMIT` in the same run.

### DDL history (extension only)

`ddl_history` backs the extension's per-connection DDL-history diff. Like `run_script`, it is **not** one of the 23 agent tools: it is listed and routed only when `MSSQL_SCRIPT_RUNNER=true`, and is otherwise an unknown tool. It reads `dbo.DDL_AuditLog`, which the `DDL_Audit` database trigger fills with one row per DDL statement (the same table and trigger that the AI Insights layer uses).

- Arguments: `action` (required: `status`, `install`, `list` or `get`, case-insensitive), `schema`, `name`, `id`, `top` (default 100, clamped to 1..500), `connection`.
- `status` returns `data: { tableExists, tableCompatible, triggerExists, triggerEnabled, canInstall, serverName, databaseName }`, plus `warnings` when an existing table is compatible but lossy, or when an existing `DDL_Audit_Writer` must not be used (the same principal check as `install`, without failing; `canInstall` is then false), and `loggingSuppressed: true` when `dbo.DDL_AuditLog` has DML triggers (the installed trigger then records nothing). `serverName` and `databaseName` are the server's own `@@SERVERNAME` and `DB_NAME()`, so the extension's consent modal names the database the connection actually reached. The trigger is the database-level trigger (`sys.triggers`, `parent_class = 0`) named `DDL_Audit`. `canInstall` is true when the connection is read/write, the table or the trigger is missing, and an existing table is compatible.
- `tableCompatible` is true when the table exists and the trigger can write to it. `max_length` is read in bytes, so nvarchar counts 2 bytes per character.
  - `HostName`, `LoginName`, `SchemaName`, `ObjectName`, `ObjectType` and `ProgramName` are `varchar` or `nvarchar` of at least 100 characters (or `max`), and `EventType` of at least 64.
  - `CommandText` is `nvarchar(max)` or `varchar(max)`. `varchar(max)` is compatible, with `warnings: ["CommandText is varchar(max); non-Latin text in DDL will be stored lossy."]`.
  - `CommandXML` is `xml`. A character `CommandXML` is refused: SQL Server has no implicit xml-to-character conversion, so `CREATE TRIGGER` itself fails on such a table with error 257.
  - None of those nine columns is computed or an identity.
  - `ID` exists with an integer type, and `PostTime` with a date/time type.
  - Every other column the trigger leaves out is an identity, has a default, is nullable, computed or `rowversion`.

  Otherwise install creates nothing, and the error lists each problem, for example `dbo.DDL_AuditLog exists but the DDL_Audit trigger cannot write to it: ObjectName varchar(10), needs at least 100 characters; HostName missing. Nothing was created.`
- `install` creates `dbo.DDL_AuditLog` if it is missing, then the `DDL_Audit` trigger if it is missing (from the same embedded script as `install_insights_layer`; SQL Server 2008 R2+). The batches run one by one, with no transaction around them. **It never alters, drops or enables an existing table or trigger**, which may belong to another team. Returns `data: { createdTable, createdTrigger, triggerEnabled }`. When an existing `DDL_Audit` is disabled, it stays disabled: `triggerEnabled` is false and `data.warning` is `DDL_Audit exists but is disabled; it was left unchanged.` When the table exists but is not compatible, install creates nothing (see above). If the trigger step fails, the error starts with `Creating the DDL_Audit trigger failed:`, and the user and the table this call created are dropped again (the message says whether that worked). Refused on read-only connections. A table this call creates also gets the index `IX_DDL_AuditLog_ObjectName` on `(ObjectName, ID) INCLUDE (SchemaName, LoginName, PostTime)`, for `list` and the trigger's own lookup. An existing table is never given an index.
- **The `DDL_Audit_Writer` user.** The trigger runs `WITH EXECUTE AS 'DDL_Audit_Writer'`, a loginless database user that the install creates when it is missing (`CREATE USER [DDL_Audit_Writer] WITHOUT LOGIN WITH DEFAULT_SCHEMA = dbo`) and grants `INSERT, SELECT ON dbo.DDL_AuditLog`, nothing else. It never runs as `dbo`: an orphaned `dbo` (a database restored after its owner login was dropped) would make `EXECUTE AS 'dbo'` fail inside every DDL statement (15517), and code that runs inside the audit insert never gets more than those two permissions.
  - If a principal named `DDL_Audit_Writer` already exists and is not a loginless SQL user (`type = 'S'`; on 2012+ also `authentication_type = 0`; on every version its SID must not belong to a login), install creates nothing and fails with `A database principal named DDL_Audit_Writer already exists and is not a loginless SQL user (WITHOUT LOGIN); nothing was created. Rename or drop it, or ask a DBA.` An existing loginless `DDL_Audit_Writer` is reused (and granted again) only when it has no more rights than the trigger needs: no role membership (besides `public`), no owned schema, and no granted or grant-with-grant permission other than `CONNECT` on the database and `INSERT` / `SELECT` on `dbo.DDL_AuditLog`. Otherwise install creates nothing, alters or revokes nothing on it, and fails with `A user named DDL_Audit_Writer already exists with more rights than INSERT/SELECT on dbo.DDL_AuditLog; nothing was created. Remove its extra rights or drop it, then retry.`
  - **Pre-flight:** after creating the user and the grant, install runs `EXECUTE AS USER = N'DDL_Audit_Writer'; SELECT 1; REVERT;` before it creates the trigger. If that fails, the user and the table this call created are dropped again, and the error is returned.
  - `install_insights_layer` uses the same routine (user, grant, pre-flight, trigger script).
- **Permission.** Install needs `db_owner`, or `ALTER ANY USER`, permission to grant on `dbo.DDL_AuditLog`, `IMPERSONATE` on the new user and `ALTER ANY DATABASE DDL TRIGGER`. A `db_ddladmin`-only account gets `Creating the DDL_Audit trigger failed: ...` and nothing is left behind.
- **The installed trigger does not block other users' DDL.** `install_insights_layer` uses the same hardened script. Existing triggers are never altered, so a trigger installed by an earlier version keeps its old behaviour.
  - It runs as `DDL_Audit_Writer`, so callers need no permission on `dbo.DDL_AuditLog`; a `db_ddladmin`-only deployment account works. It records `ORIGINAL_LOGIN()`, the real login, and the "last modified" message compares that login too. A `DENY` on the table to `public` also applies to `DDL_Audit_Writer`: the DDL still runs, unlogged.
  - When `dbo.DDL_AuditLog` has any DML trigger, the insert and the lookup are skipped and the session gets `DDL_Audit: this change was not logged: DDL_AuditLog has triggers.` Such a trigger could otherwise roll the DDL back or run code inside it.
  - It sets `ANSI_WARNINGS`, `ANSI_PADDING`, `CONCAT_NULL_YIELDS_NULL` and `ARITHABORT` ON and `NUMERIC_ROUNDABORT` OFF itself, so sessions with non-ANSI options (old ODBC or MS Access clients) do not fail with error 1934. `ANSI_NULLS` and `QUOTED_IDENTIFIER` cannot be changed inside a module, so the script sets them ON before `CREATE TRIGGER`.
  - The audit `INSERT` and the "last modified" `PRINT` run inside `TRY ... CATCH`, with `XACT_ABORT OFF`. If they fail (a truncation, a constraint), the DDL still commits, unlogged, and the session gets `DDL_Audit: this change was not logged (error N).` `XACT_ABORT OFF` is needed because triggers start with `XACT_ABORT ON`, under which even a caught error dooms the transaction.
  - **Limit:** an error that dooms the transaction by itself still rolls the DDL back, because SQL Server allows nothing else once `XACT_STATE()` is -1: for example severity 20+ errors and deadlocks, or a DML trigger on `dbo.DDL_AuditLog` that appears between the skip check and the insert. Verified on LocalDB that, without the skip, an error inside such a trigger dooms the DDL (3616) and a `ROLLBACK` there ends it (3609).
  - Every DDL statement may print a `last modified` line that names other logins who changed the object in the last month.
- **Rollback.** `DROP TRIGGER [DDL_Audit] ON DATABASE; DROP USER [DDL_Audit_Writer];` stops all logging at once. Optionally, after backing up any rows you want to keep, run `DROP TABLE dbo.DDL_AuditLog;`, which also drops `IX_DDL_AuditLog_ObjectName`. None of these affects any other object.
- IDs are `bigint`-safe end to end (`get` takes a 64-bit `id`; an existing table with a `bigint` `ID` works).
- `list` (needs `name`; `schema` is optional) returns the newest `top` entries for that object, newest first: `data: [{ id, postTime, loginName, hostName, programName, eventType, objectType, schemaName, length }]`. `postTime` is ISO 8601 server local time (`yyyy-MM-ddTHH:mm:ss.fff`); `length` is the length of the command text. When `schema` is given, entries with no schema are included too. `name` and `schema` are trimmed, then compared to their first 100 characters, as the trigger stores them.
- `get` (needs `id`) returns one entry with its full command text: `data: { id, postTime, loginName, eventType, objectType, schemaName, objectName, commandText }`. An unknown id is an error.
- `list` and `get` fail with `DDL history is not installed on this database (dbo.DDL_AuditLog is missing).` when the table is missing.
- On read-only connections `status`, `list` and `get` are plain `SELECT`s and write nothing.

### IntelliSense (extension only)

`language_service` gives the extension's query windows SSMS-style IntelliSense. Like `run_script`, it is **not** one of the 23 agent tools: it is listed and routed only when `MSSQL_SCRIPT_RUNNER=true` (26 tools then), and is otherwise an unknown tool. It is built on Microsoft's SqlParser (`Microsoft.SqlServer.Management.SqlParser`, the engine behind SSMS and Azure Data Studio IntelliSense) with an SMO metadata provider, through a small completion wrapper ported from [microsoft/sqltoolsservice](https://github.com/microsoft/sqltoolsservice) (MIT; each ported file under `MssqlMcp/LanguageService/` names its source).

- Arguments: `action` (required: `completion`, `hover`, `signatureHelp`, `warm` or `refresh`, case-insensitive), `text` (the document), `line` and `column` (1-based caret position), `connection`.
- `completion` returns `data: { items: [{ label, kind, detail, insertText, sortText }], isIncomplete, cacheState }`. `kind` is one of `table`, `view`, `column`, `procedure`, `function`, `keyword`, `schema`, `parameter`, `variable`, `database`, `type`, `snippet`, `other`. It covers tables after `FROM` / `JOIN`, `alias.` columns, schema-qualified names, CTE and derived-table columns, variables, built-in functions, and keywords where the parser has nothing else (no list inside comments).
- **Procedure parameters.** SqlParser offers a procedure's parameters only as signature help, so after `EXEC proc ` / `EXECUTE proc ` the server adds them from `Resolver.FindMethods`: `label` `@x`, `detail` the type (with `OUTPUT` where it applies), `insertText` `@x = `, sorted ahead of the `@@` globals. Parameters already given by name (`@x = 1`), and leading positional arguments, are not offered again. Inside a value (`@x = |`) none are offered. This part is our code, not sqltoolsservice's.
- `hover` returns `data: { contents, range? }`: `contents` is plain text (for example `column a(int, null)`), so render it as plain text or a code block. `range` is 1-based and end-exclusive. `data` is null when there is nothing to show.
- `signatureHelp` returns `data: { signatures: [{ label, documentation?, parameters: [{ label, documentation? }] }], activeSignature, activeParameter }` for built-in functions (`DATEADD(`) and user procedures (`EXEC dbo.p 1, `). `activeParameter` is 0-based, -1 when the caret is on none. `data` is null outside a call.
- `warm` starts building the metadata cache for the connection and returns at once: `data: { cacheState }`. `refresh` drops the cache for the connection and database, then warms it again (use it after DDL in another session).
- **Metadata cache.** One SMO metadata provider per (connection name, database), built lazily in the background on its own unpooled session and shared by all documents. One lock per entry serialises SqlParser, which is not thread-safe. A request waits for binding at most 2 seconds. After that, `completion` returns the keyword list with `cacheState: "loading"` and `isIncomplete: true`, and the build carries on; `hover` and `signatureHelp` return null. Entries are dropped when the connection is closed or removed (or an ad-hoc connection replaced), on `refresh`, and after 30 minutes unused. A failed build is retried after 30 seconds.
- **Database context.** The database is the profile's database (`Initial Catalog`; the login's default database when none is set). A leading `USE x` in the text is **not** followed for metadata.
- **Cancellation.** A cancelled call (`notifications/cancelled`, as the extension sends when the user keeps typing) stops waiting at once, and work that has not started is skipped.
- **Read-only and logging.** It only reads metadata (SMO catalog queries) and works on read-only profiles. Logs carry counts and timings, never the document text or object definitions. Documents over 2,000,000 characters are refused.
- **Licence.** SqlParser is not open source: it ships under the *SQL Server Shared Management Objects (SMO) License Terms*, reproduced in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) (SMO itself and SmoMetadataProvider are MIT). The owner approved this dependency. Whoever owns licensing for your distribution should still confirm the flow-down terms; this README is not legal advice.
- **Limitations.**
  - SMO 181.37.1 and SmoMetadataProvider 181.37.1 are built against Microsoft.Data.SqlClient 6.1.3; this server runs them on SqlClient 7.0.2 (assembly unification to the higher version). Completion, hover and signature help are verified on LocalDB with that combination, not on every authentication mode (for example Entra ID).
  - The first bind on a large database can take longer than 2 seconds; the extension should `warm` when a query window connects.
  - Single-file publish grows by about 5.5 MB (44.9 MB to 50.5 MB).

## Multiple connections

One server process can hold several named SQL Server connections. Nothing changes for existing single-connection setups.

### 1. Legacy: `CONNECTION_STRING`

Set only `CONNECTION_STRING` and the server behaves as before: one connection (internally named `default`, which is only a label, not a "default connection"), the `connection` argument is optional, and startup fails fast (exit code `1`) if the database is unreachable.

### 2. `MSSQL_CONNECTIONS`

A JSON array, one object per connection:

| Property | Required | Description |
|----------|----------|-------------|
| `name` | Yes | 1-64 letters, digits, `-`, `_` or `.`. Unique, compared case-insensitively. |
| `connectionString` | Yes | ADO.NET connection string. |
| `readOnly` | No (`false`) | When `true`, the write tools listed in [Read-only profiles](#6-read-only-profiles) are refused on this connection. |
| `insights` | No (`true`) | Enables the AI Insights layer for this connection. |

```json
[
  { "name": "prod", "connectionString": "Server=prod-sql;Database=App;Trusted_Connection=True;TrustServerCertificate=True", "readOnly": true },
  { "name": "dev",  "connectionString": "Server=DC\\DEV;Database=App;Trusted_Connection=True;TrustServerCertificate=True" }
]
```

The config is strict: unknown properties are rejected, and so is a `"default"` property (there is no default connection, see the rule below). Invalid config makes the server exit with code `1`. `CONNECTION_STRING`, `MSSQL_CONNECTIONS` and `MSSQL_CONNECTIONS_FILE` can be combined; all names must be unique.

### 3. `MSSQL_CONNECTIONS_FILE`

Path to a file containing the same JSON array. Connection strings may contain `${env:VAR}` placeholders, expanded at load time from the server's environment, so secrets stay out of the file. An unset variable is a startup error. Placeholders are expanded in `MSSQL_CONNECTIONS` as well.

```json
[
  { "name": "crm", "connectionString": "Server=sql01;Database=Crm;User Id=mcp_reader;Password=${env:CRM_PASSWORD};TrustServerCertificate=True", "readOnly": true }
]
```

**Quoting.** A placeholder can sit unquoted, inside double quotes or inside single quotes:

```text
Password=${env:CRM_PASSWORD}      (unquoted: value substituted raw)
Password="${env:CRM_PASSWORD}"    (quoted: any " in the value is doubled to "")
Password='${env:CRM_PASSWORD}'    (quoted: any ' in the value is doubled to '')
```

Use a quoted form for passwords. An unquoted value is inserted as is, so a password containing `;`, `=`, `"` or leading/trailing spaces breaks the connection string. When the placeholder is enclosed in double (or single) quotes, the server doubles every `"` (or `'`) in the substituted value, which is the ADO.NET escape rule, so the value is read back exactly as stored.

Inside a JSON string the quotes must be escaped: `"connectionString": "Server=sql01;Database=Crm;User Id=mcp_reader;Password=\"${env:CRM_PASSWORD}\""`.

> **Unreleased:** quoted placeholders (`Password="${env:X}"` and `Password='${env:X}'`) are escaped as described above; unquoted placeholders behave as before.

### 4. The rule: when is `connection` required?

- **Exactly one registered connection:** `connection` is optional.
- **More than one registered connection:** `connection` is **mandatory** on every data tool. There is **no default connection**.

"Registered" counts every connection: configured, legacy and ad-hoc, **open or closed**, so the rule does not flip when a connection is closed. Closing an ad-hoc connection forgets it and lowers the count; closing a configured one does not.

The JSON schema keeps `connection` optional so single-connection clients see no change; the rule is enforced when the call is made. A missing or unknown name returns an error that lists every connection (name, server, database, read-only, open), so an agent can retry in one step. Agents are told the rule by the server instructions, each tool's `connection` description, and `connectionRequired` in `list_connections`.

Startup in multi-connection mode: all targets are probed in parallel with a 5 s timeout, and unreachable targets are logged but do **not** stop the server (unlike legacy mode).

### 5. `MSSQL_ALLOW_ADHOC_CONNECTIONS`

Off by default. When set to `true`, `open_connection` accepts a `name` plus a raw `connectionString` and registers it at runtime. Because this lets an agent point the server at arbitrary hosts (an SSRF-like capability), ad-hoc connections are **read-only by default**, have the Insights layer disabled, and are probed with a 5 s timeout before registration. A configured connection cannot be redefined ad hoc. Adding an ad-hoc connection to a single-connection server makes `connection` mandatory from then on.

The connection string is parsed before anything is sent, and the operator controls what the agent may do:

| Agent asks for | Default | Operator switch |
|----------------|---------|-----------------|
| `Integrated Security` / `Trusted_Connection` / `Authentication=Active Directory*` | refused | `MSSQL_ADHOC_ALLOW_INTEGRATED_AUTH=true` |
| `readOnly=false` | refused | `MSSQL_ADHOC_ALLOW_WRITE=true` |
| Any host | allowed | `MSSQL_ADHOC_ALLOWED_HOSTS=host1,host2` limits it |
| `AttachDBFilename`, `User Instance`, `Failover Partner` | always refused | none |

An unparsable string is refused without echoing it. When the connection test fails, the agent only gets `Connection test failed for '<name>'.` (so the tool cannot be used to probe the network); the detail is written to the server log with the password masked. Re-opening an ad-hoc name with a new connection string replaces the entry and clears the old entry's connection pool.

### 6. Read-only profiles

A connection with `"readOnly": true` refuses these tools with an error:

`execute_sql`, `insert_data`, `update_data`, `create_table`, `drop_table`, `upsert_insight`, `install_insights_layer`, `refresh_insights`, `rebuild_baseline_insights`.

Inspection tools, `read_data`, `get_insight`, `list_insights`, `get_insight_history` and `insights_check` still work. A read-only connection makes **no writes at all**, including to the `AIInsights` tables: inspection tools still return cached insights, but on a read-only connection they never create a baseline, never archive a stale insight (it is reported as `StaleArchived` and left in place for a writable connection to archive), and background DDL processing is skipped. Read-only is enforced by this server, not by SQL Server: also use a least-privilege database login for connections that must never write.

### 7. Full `mcp.json` example

Two connections, one read-only, with the password kept out of the config through `MSSQL_CONNECTIONS_FILE` and `${env:...}`:

```json
{
  "mcpServers": {
    "MSSQL-MCP": {
      "type": "stdio",
      "command": "C:\\Development\\MCPs\\MS-SQL\\MssqlMcp\\bin\\Release\\net10.0\\win-x64\\MssqlMcp.exe",
      "env": {
        "MSSQL_CONNECTIONS_FILE": "C:\\Secrets\\mssql-connections.json",
        "CRM_PASSWORD": "<set in your secret store>",
        "USE_INSIGHTS_LAYER": "true",
        "LOG_FILE_PATH": "C:\\Logs\\mssql-mcp.log"
      }
    }
  }
}
```

with `C:\Secrets\mssql-connections.json`:

```json
[
  { "name": "crm-prod", "connectionString": "Server=prod-sql;Database=Crm;User Id=mcp_reader;Password=${env:CRM_PASSWORD};TrustServerCertificate=True", "readOnly": true },
  { "name": "crm-dev",  "connectionString": "Server=DC\\DEV;Database=Crm;Trusted_Connection=True;TrustServerCertificate=True" }
]
```

Inline `MSSQL_CONNECTIONS` works too (see `sample_mcp.json`), but nesting JSON inside a JSON string needs careful escaping; prefer the file for anything beyond a demo. No tool ever returns a connection string, and the startup log masks passwords.

## AI Insights layer

The AI Insights layer caches LLM-authored (or server-generated baseline) summaries of database objects so repeated investigations cost fewer tokens. It is **enabled by default** but **not auto-installed** — call **install_insights_layer** once per database.

### What gets installed

- **`AIInsights` schema** — `SchemaInsights`, `InsightHistory`, `DdlChangeWatermark`, and related objects (embedded SQL in `InsightsLayer/SqlScripts/`).
- **`dbo.DDL_AuditLog`** — captures DDL events via a database-level trigger.
- **`DDL_Audit` trigger** — runs `WITH EXECUTE AS 'DDL_Audit_Writer'`, a loginless user the install creates with only `INSERT, SELECT ON dbo.DDL_AuditLog`, so installing it requires `db_owner` (or `ALTER ANY USER`, grant rights on the table, `IMPERSONATE` on the new user and `ALTER ANY DATABASE DDL TRIGGER`).
  - The trigger is **database-wide**: it fires for DDL from every application, not just this server. It records `ORIGINAL_LOGIN()`, callers need no permission on `dbo.DDL_AuditLog`, and a failed audit insert does not roll back the caller's DDL. See [DDL history (extension only)](#ddl-history-extension-only) for the details and the limits. `install_insights_layer` is still marked destructive, because it adds a database-wide trigger and a user. Remove them with `DROP TRIGGER [DDL_Audit] ON DATABASE; DROP USER [DDL_Audit_Writer];`.
  - An existing `DDL_Audit` trigger is never replaced, so databases installed by an earlier version keep their original trigger definition (which runs as the caller: a login without `INSERT` on `dbo.DDL_AuditLog` has its DDL rolled back). To move such a database to the hardened trigger, drop the old trigger and install again. Legacy `AIInsights` tables from older schema versions are dropped only when empty.

Install is idempotent; re-running is safe.

### How it works

```mermaid
flowchart LR
    subgraph introspect [Introspection]
        DT[describe_table / describe_view / get_object]
    end
    subgraph cache [AIInsights]
        SI[SchemaInsights]
        IH[InsightHistory]
    end
    subgraph writes [Write tools]
        W[create_table / drop_table / insert_data / update_data / execute_sql]
    end
    DT -->|attach insight| SI
    W -->|queue DDL processing| SI
    SI -->|stale / DDL change| IH
```

1. **Read path** — `describe_table`, `describe_view`, and `get_object` attach `insight`, `insightFreshness`, and optionally `enrichmentSuggested` / `insightEnrichment` to their responses (best-effort; never fails the parent tool).
2. **Freshness** — On read, the service compares the cached row’s schema fingerprint and `modify_date` to the live object. Mismatches archive the row to `InsightHistory` and return `insightFreshness: StaleArchived`.
3. **Auto-population** — When `INSIGHTS_AUTOPOPULATE` is enabled (default), absent or stale insights trigger a mechanical baseline (`LlmModel = "auto-mechanical"`, `Confidence = 0.30`) built from `sys.*` metadata.
4. **Enrichment contract** — Baseline rows set `enrichmentSuggested: true` and include an `insightEnrichment` block with a pre-filled `upsert_insight` payload. MCP-aware agents should upgrade these rows before answering the user (protocol **MCP-Insight-Enrichment-v1**). See [.cursor/skills/mssql-insights-ops/SKILL.md](.cursor/skills/mssql-insights-ops/SKILL.md) for the full agent workflow.
5. **Write path** — After successful writes, a background `InsightDdlProcessingQueue` drains DDL audit rows (or falls back to fingerprint scans) and archives affected insights. When auto-population is on, baselines are rebuilt for archived objects.

### Recommended first-time workflow

```
1. get_server_info
2. insights_check
3. install_insights_layer          (if schema or DDL trigger missing)
4. list_objects(objectType=Table)
5. describe_table(name=…)        (inspect insight / enrichmentSuggested)
6. upsert_insight(…)              (when enrichmentSuggested is true)
```

### `insightFreshness` values

| Value | Meaning |
|-------|---------|
| `Fresh` | Cached insight matches live object definition. |
| `Absent` | No row in `SchemaInsights` (baseline may be created on next introspection if auto-pop is on). |
| `StaleArchived` | Row was archived due to DDL or fingerprint drift. On a read-only connection the row is reported stale but not archived. |
| `LayerDisabled` | `USE_INSIGHTS_LAYER=false`. |
| `AccessDenied` | Could not read live definition (permissions). |
| `DefinitionUnavailable` | Object exists but definition could not be resolved. |

## Response shape

All tools return `DbOperationResult`:

```json
{
  "success": true,
  "error": null,
  "rowsAffected": 0,
  "data": { }
}
```

- `success` — whether the operation completed without error.
- `error` — message when `success` is false.
- `rowsAffected` — for DML tools (`insert_data`, `update_data`, `execute_sql`, …).
- `data` — tool-specific payload (object metadata, row arrays, insight status, …).

Introspection tools may add top-level keys inside `data` for insights (`insight`, `insightFreshness`, `enrichmentSuggested`, `insightEnrichment`, `_agentDirective`, `pendingEnrichments`).

## Build, publish, and test

### Build

```powershell
dotnet build MssqlMcp.sln
dotnet build MssqlMcp.sln -c Release
```

### Test

```powershell
dotnet test MssqlMcp.sln -c Release
```

| Test project area | Coverage |
|-------------------|----------|
| `SqlStatementClassifierTests` | Read vs execute SQL routing |
| `TriggerQualifiedNameTests` | `schema.table.trigger` name parsing |
| `InsightsLayerEnvironmentTests` | Env var opt-out semantics |
| `InsightsLayerNoOpTests` | Disabled-layer behavior |
| `SqlBatchSplitterTests` | Embedded SQL script splitting |
| `GetServerInfoTests` | Server info response shape |
| `UnitTests` | General helpers |
| `InsightsLayerDbSmokeTests` | Live DB integration (**skipped** unless `RUN_INSIGHTS_DB_TEST=1`) |

For integration tests, set `CONNECTION_STRING` to a real database. The test project falls back to `(localdb)\MSSQLLocalDB` when unset.

```powershell
$env:RUN_INSIGHTS_DB_TEST = "1"
$env:CONNECTION_STRING = "Server=.;Database=TestDb;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test MssqlMcp.sln -c Release
```

### Publish single-file executable

```powershell
.\publish-release.ps1
# Optional: .\publish-release.ps1 --verbosity normal
```

Output path is configured in `MssqlMcp/MssqlMcp.csproj` and `Properties/PublishProfiles/ReleaseSingleFile.pubxml` (default: `C:\Development\MCPs\MS-SQL-Release\MssqlMcp.exe`).

## Project layout

```
MS-SQL/
├── MssqlMcp/                    # MCP server (.NET 10 exe)
│   ├── Program.cs               # Startup, logging, DI, stdio transport
│   ├── SqlConnectionFactory.cs  # current connection profile → SqlConnection
│   ├── Connections/             # Registry, config loader, routing filter, masker
│   ├── SqlStatementClassifier.cs
│   ├── TriggerQualifiedName.cs
│   ├── DbOperationResult.cs
│   ├── LanguageService/         # language_service: SqlParser IntelliSense, metadata cache
│   ├── InsightsLayer/           # AI Insights service + embedded SQL
│   │   ├── InsightsLayerService.cs
│   │   ├── InsightDdlProcessingQueue.cs
│   │   └── SqlScripts/
│   └── Tools/                   # MCP tools (partial class Tools)
├── MssqlMcp.Tests/              # xUnit tests
├── sample_mcp.json              # Example MCP config
├── publish-release.ps1
└── .cursor/skills/mssql-insights-ops/SKILL.md   # Agent playbook for insights
```

## Security notes

- **Connection strings** live in MCP client config / environment variables — never commit real credentials to the repo.
- **execute_sql**, **drop_table**, and **update_data** are marked destructive in MCP metadata; agents should confirm intent with the user.
- **read_data** and **execute_sql** do not support parameterized queries — literals are embedded in SQL. Do not pass untrusted user input through these tools without sanitization.
- **DDL audit trigger** installation requires elevated database permissions; use a dedicated database role in production.
- Startup logs mask password fields in connection strings; other secrets in the connection string are still logged — prefer Windows / Entra authentication where possible.

## Troubleshooting

### `MCP error -32000: Connection closed`

The server exited before or during MCP handshake. Check the log file:

| Config | Location |
|--------|----------|
| `LOG_FILE_PATH` set to file | That file |
| `LOG_FILE_PATH` set to directory | New `mssql-mcp-*.log` inside it |
| Default (Windows) | `%LOCALAPPDATA%\MssqlMcp\Logs\mssql-mcp-*.log` |
| Default (Linux/macOS) | `~/.local/share/MssqlMcp/Logs/mssql-mcp-*.log` |

Logs include process info, masked connection string, SQL connection test results, and stack traces.

### Common failures

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Immediate exit code 1 | No connection configured, invalid `MSSQL_CONNECTIONS` JSON, or (legacy mode) unreachable database | Set `CONNECTION_STRING` or `MSSQL_CONNECTIONS`; check the log file |
| Error that `connection` is required | More than one connection is registered | Call `list_connections` and pass one of the names as `connection` |
| Ad-hoc connections are disabled | `MSSQL_ALLOW_ADHOC_CONNECTIONS` is not `true` | Set it in the MCP `env`, or add the connection to `MSSQL_CONNECTIONS` |
| Connection test failed | Wrong server/database/auth | Verify string outside MCP (`sqlcmd`, SSMS) |
| install_insights_layer / ddl_history install permission error | The install creates the user `DDL_Audit_Writer`, grants it on `dbo.DDL_AuditLog` and creates a database trigger that runs as it | Install as `db_owner` |
| execute_sql rejects SELECT | By design | Use **read_data** for all queries that return rows |
| Insight tools return "layer is disabled" | `USE_INSIGHTS_LAYER=false` | Remove or set to `true`; restart server |
| Hardware fields null in get_server_info | Missing `VIEW SERVER STATE` | Expected on restricted accounts; see `hardware.warning` |

### Missing .NET runtime

Install [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on the machine running `MssqlMcp.exe` (not required for self-contained publish output).

## License

Copyright (c) Microsoft Corporation. Licensed under the [MIT license](LICENSE).
