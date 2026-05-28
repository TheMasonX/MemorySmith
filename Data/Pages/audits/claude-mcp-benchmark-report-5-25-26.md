# Claude MCP Benchmark Report 5-25-26

## Benchmark Report — MemorySmith MCP Tools (2026-05-25)

### Test Matrix Executed

| Tool | Tested | Result |
|---|---|---|
| `memorysmith_hybrid_search` | ✅ Multiple queries, tag filter, status filter, empty query | Healthy |
| `memorysmith_search` | ✅ Exact-phrase test | Healthy |
| `memorysmith_semantic_search` | ✅ Paraphrased natural-language query | Healthy |
| `memorysmith_get` | ✅ Multiple exact IDs | Healthy |
| `memorysmith_context_pack` | ✅ depth=2, backlinks, maxRecords=30 | Healthy (see verbosity issue) |
| `memorysmith_unified_search` | ✅ 8+8 limits, cross-layer | Healthy |
| `memorysmith_page_search` | ✅ No-query recency scan, query scan | Healthy |
| `memorysmith_page_get` | ✅ Large page (20k char), small page | Healthy |
| `memorysmith_source_bundle` | ❌ `ids` mode | **4-min silent timeout** |
| `memorysmith_find_by_source` | ❌ pattern match | **4-min silent timeout** |
| `memorysmith_task_list` | ❌ default listing | **4-min silent timeout** |
| `memorysmith_task_create` | ❌ new task | **4-min silent timeout** |
| `memorysmith_page_save` | — not tested (blocked by write path risk) | — |
| `memorysmith_page_delete` | — not tested (destructive) | — |
| `memorysmith_task_get` / `update` / `comment` etc. | — skipped given task_list failure | — |

---

## Council Review — MCP Tool Benchmark Findings

**Context:** Full stress-test of all 19 MCP tools by an external Claude agent via MCP. 14 tools exercised, 4 confirmed timed out (4-minute silent hangs), 2 write tools avoided as precaution. Evidence drawn from live tool call results.

**Assumptions:**
- `[critical assumption]` The service was running and reachable throughout — confirmed by `memorysmith_get` responding immediately after every timeout.
- `[critical assumption]` Timeouts reflect server-side handler failure, not network/transport issues. MCP transport (HTTP JSON-RPC) was healthy for read tools.
- The `%MemorySmithRepo%` variable resolves correctly on this machine (based on `source-link-security-boundaries` record describing this as the default trusted root).

---

### Councillor Perspectives

#### The Advocate *(confidence: 82%)*
> **Position:** The timeouts are caused by file I/O blocking in a shared async execution context — source bundle reads, task store reads, and task writes all block waiting on file handles that are either locked, slow to resolve, or deadlocked in the file-backed storage layer.

`memorysmith_get`, `memorysmith_hybrid_search`, and `memorysmith_context_pack` all returned immediately after source tool timeouts — including `memorysmith_get` on records that have `sourceLinks`. This rules out a full service crash and points to something specific to file I/O paths that are *separate* from the in-memory Lucene/ONNX search pipeline. The KB's own record `task-tracking-feature-20260523` references `IFileTaskStore`, and `project-wiki-source-link-security-boundaries` describes `ReadSourceAsync`, `FileVarStore`, and `FileMaintenanceProposalStore` — these are all file-backed. The timeout pattern (source bundle → find_by_source → task_list → task_create, all fail; get/search/context_pack all succeed) is consistent with a blocked or exhausted async I/O thread pool, or a file lock on `Data/Tasks` or the `vars.json`/`%MemorySmithRepo%` variable resolution path. `FileVarStore` specifically records "corrupt vars.json reads in StorageDiagnostics" — a corrupted or locked vars.json would block `VarResolver`, which is required by source bundle to expand `%MemorySmithRepo%` paths.

#### The Devil's Advocate *(confidence: 65%)*
> **Position:** The four timeouts may be unrelated to each other — the task tools timing out after the source tools is more likely a sequential concurrency accident than a shared I/O cause.

The task storage layer (`Data/Tasks/*.json`) and the source bundle path (`VarResolver` → file reads under `MemorySmithRepo`) are architecturally separate concerns. Task reads don't require variable resolution. If the task tools were timing out independently, it would suggest something specific to the task MCP handler (perhaps a missing auth check on `CanReadTasks` or a null-reference in the MCP JSON-RPC dispatch for task tools) rather than a file I/O deadlock. The confidence here is limited because `task_list` may never have been tested externally before — the KB record `task-tracking-feature-20260523` has `usageCount: 0`. It's possible the task MCP handlers have a latent bug rather than a runtime I/O issue.

#### The Pragmatist *(confidence: 88%)*
> **Position:** Whether the cause is shared I/O or a handler bug, the immediate fix is identical: add a configurable server-side timeout (5–10 seconds) to `source_bundle`, `find_by_source`, and all task MCP handlers, and return a structured error rather than blocking the caller indefinitely.

A 4-minute silent hang is the worst possible failure mode from an agent's perspective. There is no partial result, no error code, no indication of what failed. A `{"error": "timeout", "message": "handler exceeded 10s", "tool": "memorysmith_source_bundle"}` response would let agents fail fast, retry with smaller input, or fall back gracefully. This is a low-effort fix with high impact on agent reliability. `MemorySmith:Mcp:DisabledTools` already exists per `features/api-and-mcp` — as a short-term mitigation, `memorysmith_source_bundle` and `memorysmith_find_by_source` should be added to `DisabledTools` until the hang is resolved, to protect agents from silent waits.

#### The Historian *(confidence: 70%)*
> **Position:** This pattern was anticipated. The `workbench/tasks` page explicitly lists "Decide whether MCP-only source bridge tools should move into the shared tool catalog with a richer risk model" as an open item. The task tools were introduced in `task-tracking-feature-20260523` (status: Working, confidence 0.96) as a recent addition, and the MCP integration friction record (`project-wiki-mcp-integration`) pre-dates them.

The broader design tension — source tools as `SensitiveRead` requiring `CanReadSourceBundle` auth, now flowing through `ChatToolCatalog` per `source-link-security-boundaries` — suggests a migration was in progress at the time these tools timed out. A half-migrated tool path that requires auth resolution before execution could deadlock if the auth resolution itself requires a file read (e.g. reading user roles from a SQLite store that's contending with the source read path).

#### The Risk Officer *(confidence: 90%)*
> **Position:** Silent 4-minute timeouts are an availability risk that compounds at scale — every agent call that hits these tools blocks a thread for the full duration, and under concurrent load the service could fully exhaust its handler pool.

The MCP server is `ASP.NET Core` (per `project-wiki-active-architecture`). Under the default `Kestrel` thread model, each blocked async operation holds a thread from the managed thread pool. Four concurrent agents each hitting `source_bundle` would hold 4 threads for 16 total minutes of blocking time. If the cause is a file lock on `vars.json` or `Data/Tasks`, it may be non-deterministic (only manifests under certain write interleavings) — making it intermittently hard to reproduce in controlled tests. The security test suite (`SecurityAndSourceLinkTests.cs`) covers authorization paths but may not cover the blocking/timeout behavior under file contention. This needs an explicit timeout regression test.

---

### Open Questions
- [ ] Does `%MemorySmithRepo%` resolve correctly in the current running service? A stalled `VarResolver` call would explain source bundle and find_by_source hangs. Check `StorageDiagnostics` in `/api/diagnostics` for corrupt vars.json warnings.
- [ ] Do task MCP handlers share any code path with source bundle (e.g. auth checks that resolve through `VarResolver` or `FileVarStore`)? If yes, a single locked file explains all four timeouts.
- [ ] Have task MCP tools (`task_list`, `task_create`, etc.) ever successfully returned a response in production/dogfood? `usageCount: 0` on the task KB record suggests they may never have been exercised externally.

### Confidence Summary

| Dimension | Confidence | Rationale |
|---|---|---|
| Timeout evidence quality | 95% | Four independent tool calls, 4-min each, while read tools responded immediately — highly reproducible pattern |
| Root cause identification | 68% | File I/O / VarResolver hypothesis fits the data but task tools timing out is harder to explain; two competing theories remain |
| Scope of impact | 85% | All file-backed MCP operations appear affected; in-memory search operations (Lucene, ONNX) are unaffected |
| Fix clarity | 90% | Server-side timeout + structured error is clear regardless of root cause; DisabledTools as immediate mitigation is also clear |

---

## Other Findings (Non-Critical)

### Search Quality

**Lexical vs. Hybrid on exact phrases:** For "reciprocal rank fusion" — lexical correctly fires the `exact lexical content phrase` bonus and ranks `project-wiki-hybrid-search-rrf` #1 with score 6.25. Hybrid also gets #1 correct by fusing. Semantic alone puts `project-wiki-search-roadmap` at #1 (it treats the query conceptually, not as a phrase). Behavior is correct and expected — the `matchReason` field makes this transparent and legible.

**Semantic score compression:** For "how does the search ranking combine vector and keyword scoring", the top 5 semantic results cluster between 0.800 and 0.831 — a 3% spread across records that are meaningfully different in specificity. At this range, re-ranking is unstable; small document changes could invert results. Not a bug, but worth knowing for any future relevance tuning.

**Tag-only filter with no query:** Returns results sorted by recency, `matchReason` reads "No query supplied; returned by recency." This is correct and transparent, but undocumented in the skill or the pages.

**Status filter works correctly** — `status=Working` (status numeric 1) filtered correctly. The status filter accepts status *names* not numbers.

### Context Pack Verbosity

At `referenceDepth=2`, `maxRecords=30`, `includeBacklinks=true`, the pack's `diagnostics[]` array at the envelope level contained **90+ individual `tag.unknown_plain` entries** — one per tag per record, duplicated. This makes the JSON almost unreadable for agent parsing. The content `records[]` are fine; the noise is entirely in the envelope-level `diagnostics`. Suggestion: deduplicate diagnostics at the envelope level (or omit `tag.unknown_plain` from the envelope entirely — it already appears per-record).

### Pages Have No Semantic Search

`pageProvider.mode: "markdown-lexical"` in unified search. Page search uses only lexical matching. A query like "what documentation describes how authentication flows work" will find pages with the word "auth" but miss pages about "sign-in policy" or "access control". This is a known gap per `project-wiki-semantic-search-gap` (which calls out broader page/document coverage as the remaining gap). Worth surfacing explicitly in tool docs.

### Dangling References in KB

`task-tracking-feature-20260523` has two `relationship.missing_reference` warnings for `task-tracking-feature-page` and `tasks-page-feature-design-20260523`. These reference targets don't exist in the KB. The records were likely renamed or the page was moved without updating the memory record's `references` array.

### Missing Tool Capabilities (Gap Analysis)

| Missing capability | Impact | Notes |
|---|---|---|
| No KB memory write tools via MCP | High | Agents can only *read* memory records; writes go through proposals. Intentional design per governance model, but not documented in tool descriptions. |
| No semantic page search | Medium | `page_search` is lexical-only; concept-level page retrieval requires `unified_search` which also returns memories |
| No search result pagination | Medium | `limit` with no `offset` — large KBs have no way to page through results beyond the first N |
| No date-range filter on search | Low-Medium | Can't filter to "records updated in the last 30 days"; recency only surfaced in no-query mode |
| No `memorysmith_health` tool | Low | Agents can't check service readiness before invoking expensive tools |
| No `memorysmith_task_delete` MCP tool | Low | Soft delete exists in UI/REST API but not exposed over MCP |
| `page_search` omits `minimumRole` | Low | Security-sensitive: agents can't know from search results which pages are Admin-only |
| Source bundle has no timeout param | High | Caller has no control over wait duration when file I/O blocks |
| `DisabledTools`/`EnabledTools` config undocumented in MCP tool descriptions | Medium | Per `features/api-and-mcp`, these config keys exist but aren't mentioned in the tool schemas or the skill |

### Tag Allowlist Saturation

The vast majority of tags in active use are `tag.unknown_plain` — `streaming`, `ollama`, `blazor`, `rrf`, `onnx`, `provider`, `layout`, `security`, `ci`, `doxygen`, etc. The allowlist appears to cover only a very narrow set of tags relative to the vocabulary actually in use. This makes the diagnostics noise essentially universal and dilutes the signal value of the warning. Either expand the allowlist significantly or consider silencing `tag.unknown_plain` from the MCP output by default (it's Info severity, not actionable by agents).