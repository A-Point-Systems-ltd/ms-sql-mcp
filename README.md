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
| `USE_INSIGHTS_LAYER` | No | enabled | Opt-**out** switch. Set to `false`, `0`, `no`, `off`, or `disabled` to disable the AI Insights layer. Any other value (including unset) leaves it enabled. |
| `INSIGHTS_AUTOPOPULATE` | No | enabled | Opt-out. When enabled (and insights layer is on), introspection auto-creates mechanical baseline insights and attaches enrichment directives. Set to a falsey value to disable auto-population only. |
| `LOG_FILE_PATH` | No | `%LOCALAPPDATA%\MssqlMcp\Logs\` (Windows) or `~/.local/share/MssqlMcp/Logs/` (Linux/macOS) | Full file path, or a directory (timestamped log files are created inside it). |

When both `USE_INSIGHTS_LAYER` and `INSIGHTS_AUTOPOPULATE` are enabled, the server also enables baseline row-count probing, baseline refresh during DDL scans, and `insightEnrichment` response directives (all derived from those two flags — there are no separate env vars for them).

## MCP tools reference

The server exposes **23 tools** through a single partial `Tools` class. MCP wire names are **snake_case** (pinned explicitly in `MssqlMcp/ToolNames.cs`). Legacy per-type list/get helpers (`ListTables`, `GetStoredProc`, etc.) remain as internal C# methods; clients should use the unified tools below.

> **Breaking change (.NET 10 / MCP SDK 2.x upgrade):** tool names changed from PascalCase (`ReadData`, `ExecuteSQL`, …) to snake_case (`read_data`, `execute_sql`, …). Update client tool allow-lists, auto-approve rules and saved prompts that reference the old names.

### Read-only inspection

| Tool | Purpose |
|------|---------|
| **list_objects** | List objects by `objectType`: `Table`, `View`, `StoredProcedure`, `TableFunction`, `ScalarFunction`, `Function` (scalar + table), `TableTrigger`, `SysObject`, `DatabaseTrigger`, `Type` (user-defined alias/table types), `Login`, `ServerRole`, `DatabaseUser`, `DatabaseRole`. **Privacy:** the security types return principal names (logins and users often identify people) and role membership to the calling agent and its LLM provider; passwords, hashes and SIDs are never returned. Optional `partialName` does a `LIKE` filter on name and `schema.name`. For `SysObject`, optional `sysObjectType` filters by `sys.objects.type` (`U`, `V`, `P`, `FN`, …). |
| **describe_table** | Full table metadata: columns (type, nullability, descriptions), indexes, constraints, foreign keys, triggers. Preferred over ad-hoc `sys.*` queries for one table. |
| **describe_view** | View metadata, column list, indexes (`name`, `type`, `isUnique`, `keys`), and full T-SQL definition. |
| **script_object** | Ready-to-run T-SQL DDL for one object: `Table`, `View`, `Index` (with `parent`), `ForeignKey`, `TableTrigger`, `StoredProcedure`, `TableFunction`, `ScalarFunction`, `DatabaseTrigger`, `Type`, `Login`, `ServerRole`, `DatabaseUser`, `DatabaseRole`. Returns `{ objectType, schema, name, form, ddl, warnings }`; `form` is `Create`, `CreateOrAlter` (SQL Server 2016 SP1+) or `Alter` for programmable objects. Passwords, hashes and SIDs are never scripted (placeholders instead); unsupported features (partitioning, compression, XML/columnstore indexes, encrypted modules, ...) are listed in `warnings`. Principal names are returned (see privacy note above). Read-only. |
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
| **open_connection** | write | Reopens a configured connection, or (only with `MSSQL_ALLOW_ADHOC_CONNECTIONS=true`) registers an ad-hoc one from `connectionString`. Ad-hoc connections are read-only unless `readOnly=false`. The connection is tested (5 s cap) before it is registered. |
| **close_connection** | write | Closes a connection. Ad-hoc connections are forgotten; configured ones stay listed as closed and can be reopened. The last open connection cannot be closed. |

These three tools take no `connection` argument. Every other tool accepts an optional `connection` argument; see [Multiple connections](#multiple-connections).

### Read vs execute routing

`SqlStatementClassifier` parses every statement with the T-SQL parser (`Microsoft.SqlServer.TransactSql.ScriptDom`) and enforces a strict split. Every tool accepts **exactly one statement**; T-SQL needs no `;` between statements, so text such as `SELECT 1 WAITFOR DELAY '…'` counts as two statements and is rejected.

- **read_data** — a single `SELECT` / `WITH … SELECT`. `SELECT … INTO`, `OPENQUERY` / `OPENROWSET` / `OPENDATASOURCE` and linked-server (4-part) names are rejected. The query runs inside a transaction that is always rolled back. At most `maxRows` rows are returned (default 500, max 10000). `data` is always the row array; when more rows exist the response also carries top-level `truncated: true` and `maxRows`.
- **insert_data / update_data / create_table / drop_table** — only the matching statement type (`INSERT`, `UPDATE`, `CREATE TABLE`, `DROP TABLE`) is accepted, so `insert_data("DROP TABLE x")` is refused.
- **execute_sql** — single DDL/DML statements (including `SELECT … INTO`; a `CREATE PROCEDURE` body counts as one statement). Any plain `SELECT` is rejected with a message pointing to `read_data`. `SET`, `DECLARE`, `USE`, `WAITFOR` and `SHUTDOWN` are rejected as unsupported.

This keeps destructive operations behind an explicitly flagged tool and prevents accidental full-table reads through the write path.

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

**Quoting.** A placeholder can sit unquoted or inside double quotes:

```text
Password=${env:CRM_PASSWORD}      (unquoted: value substituted raw)
Password="${env:CRM_PASSWORD}"    (quoted: any " in the value is doubled to "")
```

Use the quoted form for passwords. An unquoted value is inserted as is, so a password containing `;`, `=`, `"` or leading/trailing spaces breaks the connection string. When the placeholder is enclosed in double quotes, the server doubles every `"` in the substituted value, which is the ADO.NET escape rule, so the value is read back exactly as stored.

Inside a JSON string the quotes must be escaped: `"connectionString": "Server=sql01;Database=Crm;User Id=mcp_reader;Password=\"${env:CRM_PASSWORD}\""`.

> **Unreleased:** quoted placeholders (`Password="${env:X}"`) are escaped as described above; unquoted placeholders behave as before.

### 4. The rule: when is `connection` required?

- **Exactly one registered connection:** `connection` is optional.
- **More than one registered connection:** `connection` is **mandatory** on every data tool. There is **no default connection**.

"Registered" counts every connection: configured, legacy and ad-hoc, **open or closed**, so the rule does not flip when a connection is closed. Closing an ad-hoc connection forgets it and lowers the count; closing a configured one does not.

The JSON schema keeps `connection` optional so single-connection clients see no change; the rule is enforced when the call is made. A missing or unknown name returns an error that lists every connection (name, server, database, read-only, open), so an agent can retry in one step. Agents are told the rule by the server instructions, each tool's `connection` description, and `connectionRequired` in `list_connections`.

Startup in multi-connection mode: all targets are probed in parallel with a 5 s timeout, and unreachable targets are logged but do **not** stop the server (unlike legacy mode).

### 5. `MSSQL_ALLOW_ADHOC_CONNECTIONS`

Off by default. When set to `true`, `open_connection` accepts a `name` plus a raw `connectionString` and registers it at runtime. Because this lets an agent point the server at arbitrary hosts (an SSRF-like capability), ad-hoc connections are **read-only by default** (`readOnly=false` must be passed explicitly), have the Insights layer disabled, and are probed with a 5 s timeout before registration. A configured connection cannot be redefined ad hoc. Adding an ad-hoc connection to a single-connection server makes `connection` mandatory from then on.

### 6. Read-only profiles

A connection with `"readOnly": true` refuses these tools with an error:

`execute_sql`, `insert_data`, `update_data`, `create_table`, `drop_table`, `upsert_insight`, `install_insights_layer`, `refresh_insights`, `rebuild_baseline_insights`.

Inspection tools, `read_data`, `get_insight`, `list_insights`, `get_insight_history` and `insights_check` still work. Read-only is enforced by this server, not by SQL Server: also use a least-privilege database login for connections that must never write.

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
- **`DDL_Audit` trigger** — requires `ALTER ANY DATABASE DDL TRIGGER` (or `ddl_admin` / `sysadmin`).
  - The trigger is **database-wide**: it fires for DDL from every application, not just this server, and runs **as the caller**. A login that may run DDL but lacks `INSERT` on `dbo.DDL_AuditLog` has its DDL **rolled back** — grant `INSERT ON dbo.DDL_AuditLog` to such logins before installing on a shared database. `install_insights_layer` is marked destructive for this reason.
  - An existing `DDL_Audit` trigger is never replaced, so databases installed by an earlier version keep their original trigger definition. Legacy `AIInsights` tables from older schema versions are dropped only when empty.

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
| `StaleArchived` | Row was archived due to DDL or fingerprint drift. |
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
| install_insights_layer permission error | Missing DDL trigger rights | Grant `ALTER ANY DATABASE DDL TRIGGER` or use `ddl_admin` |
| execute_sql rejects SELECT | By design | Use **read_data** for all queries that return rows |
| Insight tools return "layer is disabled" | `USE_INSIGHTS_LAYER=false` | Remove or set to `true`; restart server |
| Hardware fields null in get_server_info | Missing `VIEW SERVER STATE` | Expected on restricted accounts; see `hardware.warning` |

### Missing .NET runtime

Install [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on the machine running `MssqlMcp.exe` (not required for self-contained publish output).

## License

Copyright (c) Microsoft Corporation. Licensed under the [MIT license](LICENSE).
