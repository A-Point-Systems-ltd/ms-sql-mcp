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

## Quick orientation

- Server: .NET 9 stdio MCP server (`MssqlMcp.exe`)
- Connection: `CONNECTION_STRING` env var (Windows auth or SQL auth)
- Insights layer: enabled by default, use mcp.json param `USE_INSIGHTS_LAYER=false` to disable. When enabled:
  - Introspection tools attach `insight` + `insightFreshness` to responses.
  - Write tools (`CreateTable`, `DropTable`, `InsertData`, `UpdateData`, `ExecuteSQL`) queue a background DDL/fingerprint reconciliation.

## Tool taxonomy (use this to pick the right tool)

### Read-only inspection
- `ListTables`, `ListViews`, `ListStoredProcedures`, `ListTableFunctions`, `ListScalarFunctions`, `ListTableTriggers`, `ListSysObjects`
- `DescribeTable`, `DescribeView`, `GetStoredProc`, `GetFunction`, `GetTrigger`
- `ReadData` — arbitrary `SELECT` only

### Server metadata
- `GetServerInfo` — version, edition, hardware, DB counts. Some fields may be `null` with a `hardware.warning` string when permissions/version restrict DMVs.

### Writes / DDL
- `CreateTable`, `DropTable`, `InsertData`, `UpdateData`
- `ExecuteSQL` — anything else (DDL/DML/batches). Destructive — confirm first.

### AI Insights layer
- `InsightsCheck` — status of `AIInsights` schema, `DDL_AuditLog` table, `DDL_Audit` trigger, watermark.
- `InstallInsightsLayer` — idempotent install (schema, tables, audit log + DB-level DDL trigger). Needs DDL trigger permission.
- `GetInsight` — read one cached insight; returns `{ insight, insightFreshness }`.
- `UpsertInsight` — write/update an insight (UPDATE-then-INSERT semantics).
- `ListInsights` — recent rows from `AIInsights.SchemaInsights`.
- `GetInsightHistory` — archived rows from `AIInsights.InsightHistory`.
- `RefreshInsights` — process DDL backlog / fingerprint drift; return recent summaries.

## Standard workflows

### Workflow A — first time using a new database

Copy this checklist and track progress:

```
- [ ] 1. GetServerInfo (note version)
- [ ] 2. InsightsCheck (verify layer state)
- [ ] 3. InstallInsightsLayer (only if missing or trigger disabled)
- [ ] 4. ListTables (broad orientation)
- [ ] 5. (Optional) ReadData with sys.foreign_keys for relationship graph
```

### Workflow B — investigate a specific table

Example: user asks "investigate `SomeTable` and related tables".

```
- [ ] 1. DescribeTable(name = "SomeTable")
       Read: columns, indexes, constraints, foreignKeys, triggers, insight, insightFreshness
- [ ] 2. ReadData: discover inbound + outbound FKs in one query (template below)
- [ ] 3. For each related table → DescribeTable(name = "<schema>.<name>")
- [ ] 4. ReadData: row counts + lifecycle/quality probes
- [ ] 5. UpsertInsight for the focal table + each related table
- [ ] 6. ListInsights(schemaName = "dbo", objectType = "Table") to verify population
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
- [ ] 1. RefreshInsights (advances watermark, archives stale insights)
- [ ] 2. InsightsCheck (verify lastProcessedAuditId close to MAX(DDL_AuditLog.ID))
- [ ] 3. GetInsightHistory(...) if the user wants to see what was archived
```

## Calling conventions (IMPORTANT)

The C# server's MCP schema generator lists every annotated parameter — including nullable optionals — under `"required"`. This is by design, but it confuses many clients. To call insight tools correctly:

- For unused optional filters, pass `null` (JSON `null`) explicitly. Do not omit the key.
- For `take`, always pass an integer (1..2000); the doc default (100) is informational only.
- For `objectType` defaults, pass the literal `"Table"` even though it's defaulted.

### `UpsertInsight` template

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
- Re-running `UpsertInsight` on the same `(objectType, schema, name, column)` updates and bumps `Version`.

### `GetInsight` / `ListInsights` shape

`GetInsight` returns:

```json
{ "insight": { ... } | null, "insightFreshness": "<state>" }
```

`insightFreshness` values:

| Value | Meaning |
|---|---|
| `LayerDisabled` | `USE_INSIGHTS_LAYER` was set to `false`/`0`/`off`/`disabled` — do not rely on insights. |
| `Absent` | No cached insight yet. Consider `UpsertInsight` after investigation. |
| `Fresh` | Insight present and fingerprint matches the live schema. Use it. |
| `StaleArchived` | Live object missing or fingerprint changed; row was archived. Re-investigate, then upsert. |
| `AccessDenied` | Cannot verify staleness (permissions). Insight still returned; mark as advisory. |
| `DefinitionUnavailable` | Live `OBJECT_DEFINITION` returned null; insight still returned; advisory. |

## Safety rules

- Treat `ExecuteSQL` and `DropTable` as destructive. Confirm intent before running.
- Default to `ReadData` for `SELECT`s — `ExecuteSQL` also works but advertises destructive intent.
- Never embed user-provided values directly into `ReadData` SQL. Build the literal yourself; do not echo unsanitized inputs.
- When `DescribeTable` returns `insightFreshness: "StaleArchived"`, do NOT trust the previous insight; re-investigate.

## Anti-patterns

- Looping `ReadData` per column to mimic `DescribeTable` — use `DescribeTable` once.
- Calling `RefreshInsights` after every `UpsertInsight`. Run it once per investigation session or after known DDL.
- Asking the user for `objectType` when the context already implies it (e.g. you just called `DescribeTable` → `objectType = "Table"`).
- Passing `take` larger than what you'll actually inspect — keep responses small.

## Verification snippet

After populating insights, verify with:

```
InsightsCheck()
ListInsights({ "schemaName": "dbo", "objectType": "Table", "take": 50 })
```

Expected: `schemaInsightsCount` ≥ number of upserts; rows appear in `ListInsights` sorted by `LastAnalyzed DESC`.

## Additional references

- Server features and config: see `\\DEV5\C\Development\MCPs\MS-SQL\README.md` at the repo root.
- Insights layer design: see `\\DEV5\C\Development\MCPs\MS-SQL\documentation\ai_insights_guide.md`.
- SQL install scripts (canonical): `\\DEV5\C\Development\MCPs\MS-SQL\InsightsLayer\SqlScripts\*.sql` (embedded resources).
