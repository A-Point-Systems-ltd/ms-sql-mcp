# Changelog

## 1.0.0

- Named SQL Server connection profiles (Windows, SQL and Entra auth); SQL passwords are kept in VS Code SecretStorage only.
- Object explorer tree per connection with a read-only data view and DDL (`mssql-ddl:` documents) for tables, views, procedures, functions, triggers, types and security objects; object name filter.
- Explorer profiles are forced read-only, so the explorer can never modify data.
- Bundled MSSQL-MCP server (.NET 10, single file) exposed natively to VS Code agents via an MCP server definition provider.
- One-click registration with Cursor, Claude Desktop and Claude Code; existing config files are merged after a timestamped backup, and invalid JSON is never overwritten. Plain-text passwords for external clients require an explicit confirmation; `${env:NAME}` placeholders avoid them.
- Connection settings inherited from the editor's environment (`CONNECTION_STRING`, `MSSQL_CONNECTIONS_FILE`, `MSSQL_CONNECTIONS`) never add connections to the agent server, the explorer or registered clients.
- The agent server's definition version changes with every non-secret connection or setting change, so VS Code prompts to refresh a running server.
- An automatic `connections.json` refresh that would need a new environment variable or leave no connections is not written silently; a warning offers Re-register.
- Commands are registered even when the editor has no MCP server definition API.
- Connection fields reject `${env:`; trace logging records only the row count of `read_data` results.
- Windows x64 only.
