# Changelog

## 1.0.0

- Named SQL Server connection profiles (Windows, SQL and Entra auth); SQL passwords are kept in VS Code SecretStorage only.
- Object explorer tree per connection with a read-only data view and DDL (`mssql-ddl:` documents) for tables, views, procedures, functions, triggers, types and security objects; object name filter.
- Explorer profiles are forced read-only, so the explorer can never modify data.
- Bundled MSSQL-MCP server (.NET 10, single file) exposed natively to VS Code agents via an MCP server definition provider.
- One-click registration with Cursor, Claude Desktop and Claude Code; existing config files are merged after a timestamped backup, and invalid JSON is never overwritten. Plain-text passwords for external clients require an explicit confirmation; `${env:NAME}` placeholders avoid them.
- Connection settings inherited from the editor's environment (`CONNECTION_STRING`, `MSSQL_CONNECTIONS_FILE`, `MSSQL_CONNECTIONS`) never add connections to the agent server, the explorer or registered clients.
- The agent server's definition version changes with every non-secret connection or setting change, so VS Code prompts to refresh a running server.
- Automatic `connections.json` refresh always applies close / remove / read-only; a new connection that needs a new `${env:}` variable is left out until re-registration (warning with Re-register), and the file is deleted when no connection is left.
- Commands are registered even when the editor has no MCP server definition API.
- Connection fields reject `${env:`; trace logging records only the row count of `read_data` results.
- Cursor: the agent server is registered with Cursor's own `cursor.mcp.registerServer` API (Cursor ignores VS Code's MCP server definition provider), so it appears in Cursor without any manual registration. A one-time warning flags an existing `ms-sql` entry in `~/.cursor/mcp.json`; the "Register with Cursor / Claude..." Cursor option is then only needed for the cursor-agent CLI.
- The extension-only `MSSQL_SCRIPT_RUNNER` switch is never inherited by the agent server or by servers registered with external clients.
- Add / Edit Connection is a single form (replacing the quick-pick wizard): all settings at once, inline per-field errors, **Test connection** and **List databases** on the unsaved values, and an existing saved password is kept when its field is left empty.
- Query windows: **New Query** on an open connection opens a SQL editor bound to it; a status bar item shows the bound connection (with a lock when read-only) and **Change Connection** rebinds it. Scripts run in a third private server process that keeps each connection's own read-only flag and is the only one with the extension-only `run_script` tool. Trace logging records only counts for `run_script` results.
- Query windows run with F5 or the editor title's Run button (the selection, or the whole document) and can be cancelled. Results and messages show in a bottom **MSSQL-MCP Results** panel; message lines link to the editor. Object documents on a read-only connection show a disabled Run button that explains why. New setting `msSqlMcp.query.maxRows` (default 1000). A connection change never kills a running query; `mssql-ddl:` views are never bound to a connection.
- Editable DDL: Show DDL on a view, stored procedure, table-valued function or scalar function opens an editable SQL file bound to its connection (`CREATE OR ALTER`, or `ALTER` on servers before 2016 SP1), saved under the extension's storage folder. Run / F5 applies it on a read/write connection; a read-only connection shows the disabled Run button with the reason. Unsaved edits are kept when the object is opened again. Other object types stay read-only.
- README: Features list, Query window, Editing views, procedures and functions, and a note that agents never get `run_script`.
- Windows x64 only.
