---
name: mssql-insights-ops
description: >-
  Operate the MSSQL MCP server (`MssqlMcp.exe`) effectively: introspect schema,
  query data safely, and use the AI Insights layer (cached object summaries +
  DDL audit) to investigate a database with low-token, repeatable workflows.
  Use when the user asks to "investigate", "explore", "describe", "audit", or
  "document" a SQL Server database; references specific table/related tables;
  works with `AIInsights` schema, `DDL_AuditLog`, or `USE_INSIGHTS_LAYER`;
  or wants to populate / refresh / list cached insights.
---

# MSSQL Insights Operations

This skill standardizes how an agent uses the MSSQL MCP server in this repo. It covers safe introspection, the AI Insights layer, the DDL audit log, and how to call tools whose generated MCP schemas list nullable arguments as `required`.

MCP wire tool names are **snake_case** (ModelContextProtocol SDK 2.x default). Use those names when calling tools.

## Quick orientation

- Server: .NET 10 stdio MCP server (`MssqlMcp.exe`)
- Connection: `CONNECTION_STRING` env var (single connection) or `MSSQL_CONNECTIONS` / `MSSQL_CONNECTIONS_FILE` (several named connections)
- **Connections first:** call `list_connections` at the start of every session. If `connectionRequired` is `true` (more than one connection is registered, open or closed), pass `connection="<name>"` on EVERY other tool call; there is no default connection. If it is `false`, `connection` may be omitted.
- Connections are independent databases: never assume an object or insight from one exists in another. Read-only connections refuse `execute_sql`, `insert_data`, `update_data`, `create_table`, `drop_table`, `upsert_insight`, `install_insights_layer`, `refresh_insights` and `rebuild_baseline_insights`.
- Insights layer: enabled by default, use mcp.json param `USE_INSIGHTS_LAYER=false` to disable. When enabled:
  - Introspection tools attach `insight` + `insightFreshness` to responses.
  - Auto-population is enabled by default (`INSIGHTS_AUTOPOPULATE=true`) and controls the full auto flow: baseline creation, baseline refresh during scans, and enrichment directives.
  - Write tools (`create_table`, `drop_table`, `insert_data`, `update_data`, `execute_sql`) queue a background DDL/fingerprint reconciliation.

## When to call `upsert_insight`

After `describe_table`, `describe_view` or `get_object`, look at one key:

- **`insightEnrichment` present** → call `upsert_insight` with `insightEnrichment.nextAction.args` before your final answer. `insightEnrichment.trigger` says why:
  - `InitialBaselineOnly` — only an auto-generated baseline exists. Fill the `<fill in: ...>` placeholders from the response you just got.
  - `StructureChanged` — the structure changed since the last authored insight. `previousInsight` (already copied into `nextAction.args`) and `structuralEvents` show what was there and what changed. Edit what the change made wrong; keep the rest. Do not re-investigate from scratch.
  - `DataPopulated` — the insight was written while the object held fewer than 100 rows and it now holds `rowsNow` or more. Re-check `dataPatterns` / `usageGuidelines` against the real data (a small `read_data` sample is enough) and edit them.
- **`enrichmentSuggested: false`** (no `insightEnrichment`) → the cached insight is current. Use `insight` as-is and do **not** call `upsert_insight`.

Always replace `llmModel` and `analyzedBy` with your own values. Never pass `llmModel="auto-mechanical"` or `confidence <= 0.30`.

Do not introspect other objects only to enrich them. Related objects are listed in `insight.relatedObjects`; describe one only when the user's task needs it, and handle its own `insightEnrichment` then.

## Tool taxonomy (use this to pick the right tool)

### Read-only inspection
- `list_objects` (by `objectType`: Table, View, StoredProcedure, TableFunction, ScalarFunction, Function, TableTrigger, SysObject)
- `describe_table`, `describe_view`, `get_object` (StoredProcedure / Function / Trigger)
- `read_data` — **all** read-only `SELECT` queries (including `sys.*`, `INFORMATION_SCHEMA`, DMVs). `execute_sql` rejects SELECT.

### Connection management
- `list_connections` — name, open/closed, read-only, server, database, plus `connectionRequired`. Call first.
- `open_connection` — reopen a configured connection, or register an ad-hoc one from a connection string (only if the operator enabled `MSSQL_ALLOW_ADHOC_CONNECTIONS`; ad-hoc is read-only by default).
- `close_connection` — close a connection; the last open one cannot be closed.
- These three take no `connection` argument. After `open_connection` / `close_connection`, re-check `connectionRequired`.

### Server metadata
- `get_server_info` — version, edition, hardware, DB counts. Some fields may be `null` with a `hardware.warning` string when permissions/version restrict DMVs.

### Writes / DDL
- `create_table`, `drop_table`, `insert_data`, `update_data`
- `execute_sql` — DDL/DML only (no SELECT). Destructive — confirm first.

### AI Insights layer
- `insights_check` — status of `AIInsights` schema, `DDL_AuditLog` table, `DDL_Audit` trigger, watermark.
- `install_insights_layer` — idempotent install (schema, tables, audit log + DB-level DDL trigger). Needs DDL trigger permission.
- `get_insight` — read one cached insight; returns `{ insight, insightFreshness }`.
- `upsert_insight` — write/update an insight (UPDATE-then-INSERT semantics).
- `list_insights` — recent rows from `AIInsights.SchemaInsights`.
- `get_insight_history` — archived rows from `AIInsights.InsightHistory`.
- `refresh_insights` — process DDL backlog / fingerprint drift; return recent summaries.

## Standard workflows

### Workflow A — first time using a new database

Copy this checklist and track progress:

```
- [ ] 0. list_connections (note connectionRequired; if true, pass `connection` on every call below)
- [ ] 1. get_server_info (note version)
- [ ] 2. insights_check (verify layer state)
- [ ] 3. install_insights_layer (only if missing or trigger disabled; refused on read-only connections)
- [ ] 4. list_objects(objectType = "Table") (broad orientation)
- [ ] 5. (Optional) read_data with sys.foreign_keys for relationship graph
```

### Workflow B — investigate a specific table

Example: user asks "investigate `SomeTable` and related tables".

```
- [ ] 1. describe_table(name = "SomeTable")
       Read: columns, indexes, constraints, foreignKeys, triggers, insight, insightFreshness
- [ ] 2. read_data: discover inbound + outbound FKs in one query (template below)
- [ ] 3. For each related table → describe_table(name = "<schema>.<name>")
- [ ] 4. read_data: row counts + lifecycle/quality probes
- [ ] 5. For each describe response that carried `insightEnrichment`, upsert_insight with its `nextAction.args` before answering
```

Bidirectional FK discovery template:

```sql
SELECT fk.name AS FKName,
       sch1.name AS FromSchema, t1.name AS FromTable, c1.name AS FromColumn,
       sch2.name AS ToSchema,   t2.name AS ToTable,   c2.name AS ToColumn
FROM sys.foreign_key_columns fkc
JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables   t1   ON t1.object_id   = fkc.parent_object_id
JOIN sys.schemas  sch1 ON sch1.schema_id = t1.schema_id
JOIN sys.columns  c1   ON c1.object_id   = t1.object_id AND c1.column_id = fkc.parent_column_id
JOIN sys.tables   t2   ON t2.object_id   = fkc.referenced_object_id
JOIN sys.schemas  sch2 ON sch2.schema_id = t2.schema_id
JOIN sys.columns  c2   ON c2.object_id   = t2.object_id AND c2.column_id = fkc.referenced_column_id
WHERE t1.name = @target OR t2.name = @target
ORDER BY FromTable, ToTable;
```

### Workflow C — refresh state after schema changes

```
- [ ] 1. refresh_insights (advances watermark, archives stale insights)
- [ ] 2. insights_check (verify lastProcessedAuditId close to MAX(DDL_AuditLog.ID))
- [ ] 3. get_insight_history(...) if the user wants to see what was archived
```

## Calling conventions (IMPORTANT)

The C# server's MCP schema generator lists every annotated parameter — including nullable optionals — under `"required"`. This is by design, but it confuses many clients. To call insight tools correctly:

- For unused optional filters, pass `null` (JSON `null`) explicitly. Do not omit the key.
- For `take`, always pass an integer (1..2000); the doc default (100) is informational only.
- For `objectType` defaults, pass the literal `"Table"` even though it's defaulted.

### `upsert_insight` template

```json
{
  "objectType": "Table",
  "schemaName": "dbo",
  "objectName": "TableProblems",
  "description": "<one short sentence>",
  "businessPurpose": "<why this object exists>",
  "dataPatterns": "<volume/keys/typical filters>",
  "usageGuidelines": "<how analysts should join/filter>",
  "relatedObjects": "[\"Buildings\",\"Suppliers\"]",
  "llmModel": "<your-model-tag>",
  "confidence": 0.85,
  "analyzedBy": "<agent-name>",
  "columnName": null
}
```

Rules:
- `columnName` = null → table/object-level insight; non-null → column-level insight.
- `relatedObjects` is a JSON string (the column type is text). Use compact JSON like `"[\"A\",\"B\"]"`.
- Re-running `upsert_insight` on the same `(objectType, schema, name, column)` updates and bumps `Version`.

### `get_insight` / `list_insights` shape

`get_insight` returns:

```json
{ "insight": { ... } | null, "insightFreshness": "<state>" }
```

`insightFreshness` values:

| Value | Meaning |
|---|---|
| `LayerDisabled` | `USE_INSIGHTS_LAYER` was set to `false`/`0`/`off`/`disabled` — do not rely on insights. |
| `Absent` | No cached insight yet. Consider `upsert_insight` after investigation. |
| `Fresh` | Insight present and fingerprint matches the live schema. Use it. |
| `StaleArchived` | Live object missing or its structure changed; row was archived. Describe the object: it returns a restored insight or an `insightEnrichment` with the previous text to update. |
| `AccessDenied` | Cannot verify staleness (permissions). Insight still returned; mark as advisory. |
| `DefinitionUnavailable` | Live `OBJECT_DEFINITION` returned null; insight still returned; advisory. |

## Safety rules

- Treat `execute_sql` and `drop_table` as destructive. Confirm intent before running.
- With more than one connection, confirm WHICH connection a write targets before running it. A missing or unknown `connection` returns an error listing the valid names; retry with one of them, do not guess.
- Use `read_data` for **every** `SELECT` (including `sys.*`). `execute_sql` rejects SELECT at validation time.
- Never embed user-provided values directly into `read_data` SQL. Build the literal yourself; do not echo unsanitized inputs.
- When `insightEnrichment.trigger` is `StructureChanged`, check `previousInsight` against the new structure before relying on it.

## Anti-patterns

- Omitting `connection` when `list_connections` said `connectionRequired: true`, or reusing an insight/object assumption from a different connection.
- Looping `read_data` per column to mimic `describe_table` — use `describe_table` once.
- Calling `refresh_insights` after every `upsert_insight`. Run it once per investigation session or after known DDL.
- Asking the user for `objectType` when the context already implies it (e.g. you just called `describe_table` → `objectType = "Table"`).
- Passing `take` larger than what you'll actually inspect — keep responses small.
- Answering without calling `upsert_insight` when the response carried `insightEnrichment`.
- Calling `upsert_insight` when `enrichmentSuggested` is `false`: the insight is current, and the call only burns tokens.
- Re-investigating an object from scratch when `insightEnrichment.previousInsight` is given: edit it instead.
- Describing related objects only to enrich their insights.
- Calling `upsert_insight` with `llmModel="auto-mechanical"` or `confidence<=0.30`.

## Verification snippet

After populating insights, verify with:

```
insights_check()
list_insights({ "schemaName": "dbo", "objectType": "Table", "take": 50 })
```

Expected: `schemaInsightsCount` ≥ number of upserts; rows appear in `list_insights` sorted by `LastAnalyzed DESC`.

## Additional references

- Server features and config: see `\\DEV5\C\Development\MCPs\MS-SQL\README.md` at the repo root.
- Insights layer design: see `\\DEV5\C\Development\MCPs\MS-SQL\documentation\ai_insights_guide.md`.
- SQL install scripts (canonical): `\\DEV5\C\Development\MCPs\MS-SQL\InsightsLayer\SqlScripts\*.sql` (embedded resources).
