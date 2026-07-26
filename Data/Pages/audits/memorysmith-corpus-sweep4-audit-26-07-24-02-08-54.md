# MemorySmith Audit — Corpus Sweep, Round 4: Closing Out `council-codebase-audit-20260710.md`
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-24
**Method:** verified the four remaining threads flagged as open at the end of the prior report. This completes this document's full remediation-priority list (all 4 items originally left unchecked are now resolved-or-confirmed).

---

## Executive Summary

| Item | Verified current status | Confidence |
|---|---|---|
| P1-004/P1-005 (index staleness during consolidation) | **Resolved** — `MemoryMaintenanceTasks.cs` calls `_index.Rebuild(_store.LoadAll().ToList())` both before and after consolidation, exactly as this engagement's own very first report already documented | 95% |
| P2-039 (missing `JsonPropertyName` attributes, recalibrated to P1 by the council) | **Correctly tracked and still accurate.** `TSK-0388`, status **Ready**, priority **High**, description matches exactly: all 14 files in `MemorySmith.Core/Models/` still lack the attributes | 95% |
| P1-006/P1-012 — `McpController`'s "9-dependency god class" | **New finding — confirmed real, currently untracked.** 9 real constructor-injected service dependencies confirmed directly, spanning memory search, source-link resolution, config, authorization, chat tools, pages, tasks, code search, and agent-session orchestration | 90% |

---

## New finding: `McpController` is a 9-dependency dispatch hub with no tracked decomposition (Medium, 90%)

**File:** `MemorySmith.App/Controllers/McpController.cs`, 394 lines, constructor (lines 37-47):
```csharp
public McpController(
    MemoryApplicationService memories, VarResolver vars, IOptionsMonitor<MemorySmithOptions> options,
    IAuthorizationService authorization, ChatToolCatalog toolCatalog, IPageService pages,
    ITaskService tasks, CodeSearchService codeSearch, McpAgentToolHandler agentTools,
    ILogger<McpController>? logger = null)
```
Nine real, unrelated service dependencies (excluding the optional logger) — confirmed matching the council review's original citation exactly. This is the same category of finding as this engagement's existing decomposition-tracked classes (`SqliteMemorySmithDatabase`/F12, `ChatServices.cs`, `MaintenanceAgentServices.cs`, `MemoryApplicationService`) — a single class acting as the seam for many independent subsystems, making it hard to test any one MCP tool category in isolation without standing up the whole controller's dependency graph, and making the controller's own diff surface large for any change touching just one tool family.

**Why this is worth a task even though the controller "only" dispatches**: a controller that's purely thin pass-through (each action does one `await _dependency.DoThingAsync(...)`) is a reasonable, common pattern and wouldn't itself be a smell regardless of dependency count. The concern here is scale and cohesion: 9 *unrelated* dependencies (not 9 flavors of the same concern) means every future MCP tool category added to this system has a plausible reason to add a 10th, 11th, 12th dependency to the same class, with no natural pressure to split — exactly the growth pattern that turned `MaintenanceAgentServices.cs` into a 2,188-line file this engagement spent five separate reports auditing.
**Recommendation:** consider grouping related tool-handler logic behind a small number of facade services (e.g., a `McpMemoryToolHandler`, `McpPageTaskToolHandler`, alongside the already-existing `McpAgentToolHandler` pattern this controller already uses for agent-session tools) that `McpController` depends on instead of the underlying services directly — this mirrors the pattern this file has *already* adopted for agent-session tools (`McpAgentToolHandler` is itself one of the 9, suggesting the team already recognizes this pattern is worth using, just hasn't applied it consistently to the controller's other tool categories yet).
**Effort:** a genuinely larger refactor, better scoped as its own task rather than done ad hoc — recommend filing at Medium priority, consistent with how this engagement's other decomposition-candidate findings have been prioritized (real technical debt, not urgent risk).
**Confidence (90%):** the dependency count and file size are directly confirmed; the severity judgment (Medium, not higher) reflects that unlike this engagement's security/correctness findings, this is a maintainability concern with no live behavioral consequence today.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- This completes verification of every item raised in `council-codebase-audit-20260710.md`'s remediation priority list across this and the prior report. The document's own source (a 94-finding, 10-agent swarm report, `codebase-audit-20260710-agent10-swarm-v2.md`) has not itself been independently read in full — this council review synthesized and recalibrated it, but the underlying 94-finding document may contain additional items beyond what the council's summary surfaced.
