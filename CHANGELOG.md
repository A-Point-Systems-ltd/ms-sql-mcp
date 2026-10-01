# Changelog

## Unreleased

- New extension-only tool `run_script`, registered only when `MSSQL_SCRIPT_RUNNER=true` (not listed to agents; the agent tool count stays 23). It runs a multi-batch T-SQL script with SSMS-style `GO` / `GO n` separators on one session and returns every result set (row cap per set, total row count) and every message (PRINT, row counts, errors with script line numbers). Read-only connections run only single read-only `SELECT` batches, each inside a rolled-back transaction; a transaction left open on a read/write connection is rolled back with a warning. See README, "Script runner (extension only)".
- Connection-string `${env:NAME}` placeholders enclosed in double quotes (`Password="${env:X}"`) now have any `"` in the substituted value doubled, so passwords containing `;`, `=` or `"` work. Unquoted placeholders are substituted raw, as before. See README, "Multiple connections".
- Single-quoted placeholders (`Password='${env:X}'`) now have any `'` in the substituted value doubled, the same way.
- Read-only connections make no writes at all: the AI Insights layer no longer creates baselines, archives stale insights or runs DDL processing on a read-only connection (cached insights are still returned).
- Ad-hoc connections (`open_connection` with a connection string) are hardened: Integrated Security / Active Directory authentication needs `MSSQL_ADHOC_ALLOW_INTEGRATED_AUTH=true`, `readOnly=false` needs `MSSQL_ADHOC_ALLOW_WRITE=true`, `MSSQL_ADHOC_ALLOWED_HOSTS` can limit target hosts, `AttachDBFilename` and `User Instance` are always refused, and a failed connection test returns a generic message (details go to the log, masked). Replacing an ad-hoc entry clears its old connection pool.
- `script_object` fidelity: disabled triggers are scripted followed by `DISABLE TRIGGER` (with a warning); `IGNORE_DUP_KEY = ON`, `NOT FOR REPLICATION` on identity columns and CHECK constraints, and non-default filegroups (`ON [fg]`) are scripted; Azure SQL Database / Managed Instance (EngineEdition 5 / 8) use `CREATE OR ALTER` and read temporal / memory-optimized metadata.
- Ad-hoc connection strings with `Failover Partner` are always refused (the failover host would bypass `MSSQL_ADHOC_ALLOWED_HOSTS`).
- `script_object` emits `TEXTIMAGE_ON [fg]` when a table's LOB data lives on a filegroup other than the one it would use by default.
- `describe_view` / `describe_table` index key lists no longer XML-escape column names (`a&b` instead of `a&amp;b`).
- An unknown tool name now gets the MCP unknown-tool error instead of a "'connection' argument is required" error in multi-connection mode.
- Server `<Version>` is now set in `MssqlMcp.csproj` (1.0.0); assembly, file and informational versions derive from it and must match the VS Code extension version (enforced by CI).
