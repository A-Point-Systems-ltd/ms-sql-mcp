---
name: Auto-populate insights as-you-go
overview: "Make the AI Insights layer self-managing: every introspection of an object produces a usable cached insight (mechanical baseline if no LLM-authored row exists), DDL changes auto-rebuild that baseline, and the response advertises when richer analysis is still pending. Pure MCP — no outbound LLM client. Single env var to disable."
todos:
  - id: options_env
    content: Add AutoPopulationOptions + InsightsLayerEnvironment.IsAutoPopulationEnabled (opt-out env var, mirrors USE_INSIGHTS_LAYER semantics).
    status: pending
  - id: iface
    content: Extend IInsightsLayerService with EnsureBaselineForObjectAsync; implement in InsightsLayerService and NoOpInsightsLayerService.
    status: pending
  - id: populator
    content: "Implement IInsightAutoPopulator + InsightAutoPopulator (mechanical baseline builder: columns, FKs, indexes, triggers, dependencies, modify_date, optional fast row-count, MS_Description)."
    status: pending
  - id: ddl_rebuild
    content: Modify InsightsLayerService.ProcessDdlChangesAsync to call EnsureBaselineForObjectAsync for every object whose row it archives (when auto-pop is enabled).
    status: pending
  - id: tools_attach
    content: Update Tools.TryAttachInsightAsync to trigger baseline-build + re-fetch on Absent/StaleArchived; expose enrichmentSuggested in ProjectInsightForResponse.
    status: pending
  - id: bulk_tool
    content: Add RebuildBaselineInsights MCP tool for bulk seeding (schemaName?, objectType?, take).
    status: pending
  - id: di
    content: Register IInsightAutoPopulator in Program.cs alongside existing insights services; keep no-op wiring when layer is disabled.
    status: pending
  - id: docs
    content: "Update README.md and .cursor/skills/mssql-insights-ops/SKILL.md: env var, new tool, cooperation contract (enrichmentSuggested -> call UpsertInsight)."
    status: pending
  - id: compliance_response
    content: "Build insightEnrichment block (required/reason/nextAction with pre-filled UpsertInsight args + placeholders) and surface it from TryAttachInsightAsync; gate via INSIGHTS_ENRICHMENT_DIRECTIVE env var."
    status: pending
  - id: compliance_descriptions
    content: "Harden Description attributes for DescribeTable, DescribeView, GetStoredProc, GetFunction, GetTrigger, UpsertInsight, RebuildBaselineInsights with the enrichment contract wording."
    status: pending
  - id: compliance_skill
    content: "Add 'Mandatory enrichment loop' section + worked example + anti-pattern + updated freshness table to .cursor/skills/mssql-insights-ops/SKILL.md."
    status: pending
  - id: tests
    content: Add AutoPopulationEnvironmentTests and InsightAutoPopulatorTests; extend InsightsLayerDbSmokeTests with auto-baseline + LLM-upsert-replaces + DDL-rebuild assertions.
    status: pending
  - id: verify
    content: dotnet build + dotnet test -c Release pass; manual TestDB sanity for first-touch baseline and LLM upgrade flow.
    status: pending
isProject: false
---

# Auto-Populate Insights "As You Go"

## Goal

When a developer (and the LLM helping them) starts working on a new area of the DB, the MCP server should:

1. Auto-cache a useful insight on first inspection of any object.
2. Auto-reflect schema/data-shape changes done during the session.
3. Stay disable-able without turning off the whole insights layer.

## Approach: hybrid, no outbound LLM client

- Server-built mechanical baseline guarantees every inspected object ends up with a cached `SchemaInsight` row. Marked `LlmModel = "auto-mechanical"`, `Confidence = 0.3`, version starts at 1.
- A new projected field `enrichmentSuggested` on every insight response is `true` while `LlmModel == "auto-mechanical"` — this is the cooperation point that gets the calling LLM to call `UpsertInsight` with a richer narrative, naturally upgrading the row to `Version = 2`, `LlmModel = <real model id>`, higher confidence.
- DDL/fingerprint reconciliation already archives stale rows; it'll now also rebuild a baseline for previously-known objects so the cache is never empty after schema work.
- New env var `INSIGHTS_AUTOPOPULATE` (opt-out, defaults to enabled). `USE_INSIGHTS_LAYER` keeps its current meaning.

Rejected alternatives:

- Server-side LLM client: adds API keys, model picking, costs, async waits. Out of scope.
- Directive-only (no baseline): no guarantee the calling LLM cooperates; would leave the cache empty in the common case.

## File list

New:

- [MssqlMcp/InsightsLayer/InsightAutoPopulator.cs](MssqlMcp/InsightsLayer/InsightAutoPopulator.cs) — `IInsightAutoPopulator` + impl that builds mechanical baselines.
- [MssqlMcp/InsightsLayer/AutoPopulationOptions.cs](MssqlMcp/InsightsLayer/AutoPopulationOptions.cs) — strongly-typed options (sourced from env vars).
- [MssqlMcp/Tools/RebuildBaselineInsights.cs](MssqlMcp/Tools/RebuildBaselineInsights.cs) — new MCP tool for bulk seeding / re-seeding (LLM-callable).
- [MssqlMcp.Tests/InsightAutoPopulatorTests.cs](MssqlMcp.Tests/InsightAutoPopulatorTests.cs)
- [MssqlMcp.Tests/AutoPopulationEnvironmentTests.cs](MssqlMcp.Tests/AutoPopulationEnvironmentTests.cs)

Modified:

- [MssqlMcp/InsightsLayer/InsightsLayerService.cs](MssqlMcp/InsightsLayer/InsightsLayerService.cs) — `InsightsLayerEnvironment.IsAutoPopulationEnabled`; new `EnsureBaselineForObjectAsync`. `GetInsightForObjectAsync` unchanged — orchestration stays in `TryAttachInsightAsync` so the simple `GetInsight` tool semantics don't shift.
- [MssqlMcp/InsightsLayer/IInsightsLayerService.cs](MssqlMcp/InsightsLayer/IInsightsLayerService.cs) — add `EnsureBaselineForObjectAsync`.
- [MssqlMcp/InsightsLayer/NoOpInsightsLayerService.cs](MssqlMcp/InsightsLayer/NoOpInsightsLayerService.cs) — no-op.
- [MssqlMcp/InsightsLayer/InsightDdlProcessingQueue.cs](MssqlMcp/InsightsLayer/InsightDdlProcessingQueue.cs) — after archiving a stale row, request a baseline rebuild for that object.
- [MssqlMcp/Tools/Tools.cs](MssqlMcp/Tools/Tools.cs) — augment `TryAttachInsightAsync`: when freshness is `Absent` or `StaleArchived` and auto-pop on, build baseline + re-fetch. Update `ProjectInsightForResponse` to expose `enrichmentSuggested`.
- [MssqlMcp/Program.cs](MssqlMcp/Program.cs) — register `IInsightAutoPopulator`; wire into queue.
- [README.md](README.md), [.cursor/skills/mssql-insights-ops/SKILL.md](.cursor/skills/mssql-insights-ops/SKILL.md) — document the new flow + `INSIGHTS_AUTOPOPULATE` toggle + cooperation contract ("when `enrichmentSuggested` is true, call `UpsertInsight` after analyzing").

## Mechanical baseline content

Captured purely from `sys.*` and DMVs already used elsewhere:

- **Tables**: column count + name/type list, PK, FK out-refs, top FK in-refs, index list, trigger list (incl. disabled), `modify_date`, `create_date`, est. row count from `sys.dm_db_partition_stats` (no table scan), MS_Description presence.
- **Views / procedures / functions / triggers**: `sys.sql_expression_dependencies` referenced object list, parameter signature (where applicable), `modify_date`, first ~200 chars of `OBJECT_DEFINITION` as a one-line summary.

Stored as:

- `Description` — generated one-liner ("Auto-baseline for dbo.Foo: 12 cols, 3 FKs, ~1.2M rows").
- `DataPatterns` — structural JSON.
- `RelatedObjects` — JSON array of FK / dependency targets.
- `LlmModel = "auto-mechanical"`, `AnalyzedBy = "MssqlMcp"`, `Confidence = 0.3`.

When the calling LLM later calls `UpsertInsight`, the existing UPDATE-then-INSERT path naturally replaces the row and bumps `Version`.

## Auto-rebuild on DDL

[`InsightDdlProcessingQueue.ExecuteAsync`](MssqlMcp/InsightsLayer/InsightDdlProcessingQueue.cs) already drains signals and calls `ProcessDdlChangesAsync`. We extend [`InsightsLayerService.ProcessDdlChangesAsync`](MssqlMcp/InsightsLayer/InsightsLayerService.cs) so that — after the `ArchiveInsightAsync` step for each affected row — it remembers the `(objectType, schema, name)` of every archived row and, if auto-pop is enabled, calls `EnsureBaselineForObjectAsync` for each. This means after `ALTER TABLE` the cache is non-empty within the next queue tick.

The cooperation hint stays attached on the next read because the rebuilt baseline is again `LlmModel = "auto-mechanical"`.

## Configuration

- `INSIGHTS_AUTOPOPULATE` — opt-out (matches `USE_INSIGHTS_LAYER` style). `false`/`0`/`off`/`disabled` turn it off. Missing/empty/any other value = enabled.
- `INSIGHTS_AUTOPOPULATE_ROWCOUNTS` — opt-in (default off). Controls whether the baseline includes the fast `dm_db_partition_stats` row count. Off by default to avoid surprises on locked-down accounts; on for richer baselines.
- `INSIGHTS_ENRICHMENT_DIRECTIVE` — opt-out (default on when `INSIGHTS_AUTOPOPULATE` is on). Controls whether the `insightEnrichment` block (the response-body directive nudging the calling LLM to call `UpsertInsight`) is attached to introspection responses. Disable for token-sensitive or read-only setups.

## New tool

- `RebuildBaselineInsights(schemaName?, objectType?, take=200)` — bulk baseline pass for an LLM that wants to "warm" insights for a whole area. Walks `sys.objects` filtered by parameters, ensures each object has a non-archived row. Same auto-mechanical semantics as the per-touch path. Returns `{ scanned, created, skipped, errors }`.

## Driving calling-LLM compliance

Honest framing: the MCP **cannot literally force** the calling LLM. Skill files are only honored by clients that read `.cursor/skills/` (Cursor does; many MCP clients don't). What we can do is push compliance from "maybe" to "almost always" with three layered pressure points, each of which falls back gracefully:

### 1. Move the directive into the response body (highest-leverage)

Tool descriptions are seen once at tool-selection time. Response data sits in the LLM's working context for the *next* turn. We add an `insightEnrichment` block to every `Describe*` / `Get*` response when `enrichmentSuggested == true`:

```json
"insightEnrichment": {
  "required": true,
  "reason": "Auto-mechanical baseline only. Richer narrative not yet provided by an LLM.",
  "contract": "Before answering the user about this object, call UpsertInsight using the nextAction payload below, replacing each '<fill in>' placeholder with concise text derived from the schema you just received.",
  "nextAction": {
    "tool": "UpsertInsight",
    "args": {
      "objectType": "Table",
      "schemaName": "dbo",
      "objectName": "Foo",
      "columnName": null,
      "description": "<fill in: one short sentence>",
      "businessPurpose": "<fill in: why this object exists>",
      "dataPatterns": "<fill in: volume / keys / typical filters>",
      "usageGuidelines": "<fill in: join/filter advice, gotchas>",
      "relatedObjects": "[\"<fill in object names from foreignKeys array above>\"]",
      "llmModel": "<your model id>",
      "analyzedBy": "<your agent name>",
      "confidence": 0.85
    }
  }
}
```

Pre-filling everything the server already knows (`objectType`, `schemaName`, `objectName`) makes the follow-up call cheap — the LLM only has to write the narrative parts.

Token cost: ~300–500 tokens per introspection response. Bounded by the fact that once the LLM enriches the row (`Version > 1`, real `LlmModel`), the block disappears.

Implementation lives in [MssqlMcp/Tools/Tools.cs](MssqlMcp/Tools/Tools.cs) inside `TryAttachInsightAsync` (or a new helper `BuildEnrichmentDirective`) and is gated by a new env var `INSIGHTS_ENRICHMENT_DIRECTIVE` (opt-out, defaults to enabled; turning it off saves tokens for setups that don't need the nudge).

### 2. Harden the `[Description(...)]` attributes

Every tool that surfaces or fulfills `insightEnrichment` gets a directive in its description. Examples (final wording in code, sketched here):

- `DescribeTable`, `DescribeView`, `GetStoredProc`, `GetFunction`, `GetTrigger`:
  > "When the response includes `insightEnrichment.required = true`, you MUST call `UpsertInsight` using its `nextAction.args` before answering the user. The pre-filled JSON only needs the `<fill in>` narrative fields completed from the schema you just received. Skipping this leaves the cache mechanical-only and degrades the next session."

- `UpsertInsight`:
  > "Fulfills the enrichment contract returned by `Describe*` and `Get*` tools. Re-running on the same key updates the row and bumps `Version`; the cache then stops asking for enrichment."

- `RebuildBaselineInsights`:
  > "Bulk-creates mechanical baselines for an area. Every produced row still asks for enrichment via `insightEnrichment` on subsequent introspection — this tool warms the cache, it does not finalize it."

### 3. Skill update

Add to [.cursor/skills/mssql-insights-ops/SKILL.md](.cursor/skills/mssql-insights-ops/SKILL.md):

- New section **"Mandatory enrichment loop"**, placed right after "Quick orientation":
  > After every `DescribeTable` / `DescribeView` / `GetStoredProc` / `GetFunction` / `GetTrigger` call, inspect `insightEnrichment`. If `required == true`, call `UpsertInsight` with the `nextAction.args` payload — fill the `<fill in>` fields from the schema you just received — **before** producing your answer to the user. Treat this as part of the tool's contract, not optional.

- Worked example: a `DescribeTable` response with `insightEnrichment.required = true`, then the exact `UpsertInsight` call the agent should make.

- Update the freshness table to add the new `Fresh + enrichmentSuggested` state and what to do in it.

- New anti-pattern: "Ignoring `insightEnrichment.required = true` and answering the user without first calling `UpsertInsight`."

### Limits we are not pretending to solve

- MCP clients that don't read project skills (Claude Desktop without skill support, custom clients) only get pressure points 1 and 2. The response-body directive remains the only universal channel.
- Strong "MUST" wording can be overridden by user/system prompts in the calling client. We accept this; the value is in raising the default compliance rate.
- For setups where this is noise (e.g. read-only audits with no plan to author insights), set `INSIGHTS_ENRICHMENT_DIRECTIVE=false` to suppress the response block while keeping mechanical baselines and DDL reconciliation intact.

## Tests

- `AutoPopulationEnvironmentTests` — mirrors `InsightsLayerEnvironmentTests`, pins the opt-out semantics of `INSIGHTS_AUTOPOPULATE` and `INSIGHTS_ENRICHMENT_DIRECTIVE`.
- `InsightAutoPopulatorTests` — unit test the projection (description string, JSON shape), with a fake `ISqlConnectionFactory` for the no-DB path; live DB shape covered under `RUN_INSIGHTS_DB_TEST=1`.
- `EnrichmentDirectiveTests` — unit test that `BuildEnrichmentDirective` produces a payload whose `nextAction.args` round-trips through the existing `UpsertInsight` tool when placeholders are replaced; verify the block is omitted when `INSIGHTS_ENRICHMENT_DIRECTIVE=false` or when the cached row already has `LlmModel != "auto-mechanical"`.
- Extend [`InsightsLayerDbSmokeTests`](MssqlMcp.Tests/InsightsLayerDbSmokeTests.cs):
  - `DescribeTable` on a fresh table after install → response has `insight != null`, `LlmModel == "auto-mechanical"`, `enrichmentSuggested == true`, `insightEnrichment.required == true` with pre-filled `objectType/schema/name`.
  - `UpsertInsight` using the returned `nextAction.args` (placeholders replaced) → next `DescribeTable` shows `enrichmentSuggested == false`, no `insightEnrichment` block, `Version == 2`.
  - `ALTER TABLE` (add column) → after `RefreshInsights`, row is rebuilt as auto-mechanical again and the directive is re-attached.

## Verification

- `dotnet build -c Release` clean.
- `dotnet test -c Release` — all green; new unit tests pass without DB; smoke tests skipped unless `RUN_INSIGHTS_DB_TEST=1`.
- Manual: against `TestDB`, after deploy: `DescribeTable` on an object with no insight returns `insight` with `enrichmentSuggested=true`; calling `UpsertInsight` with narrative content flips it to `false`.

## Out of scope

- Outbound LLM client in MssqlMcp.exe.
- Cost / rate-limit handling on the calling LLM (not the MCP's concern).
- Auto-seeding the whole DB at server start. Bulk seeding goes through the new `RebuildBaselineInsights` tool when the LLM wants it.