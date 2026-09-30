# Changelog

## 1.0.0

- Named SQL Server connection profiles (Windows, SQL and Entra auth); SQL passwords are kept in VS Code SecretStorage only.
- Object explorer tree per connection with a read-only data view and DDL (`mssql-ddl:` documents) for tables, views, procedures, functions, triggers, types and security objects; object name filter.
- Explorer profiles are forced read-only, so the explorer can never modify data.
- Bundled MSSQL-MCP server (.NET 10, single file) exposed natively to VS Code agents via an MCP server definition provider.
- One-click registration with Cursor, Claude Desktop and Claude Code; existing config files are merged after a timestamped backup, and invalid JSON is never overwritten. Plain-text passwords for external clients require an explicit confirmation; `${env:NAME}` placeholders avoid them.
- Windows x64 only.
