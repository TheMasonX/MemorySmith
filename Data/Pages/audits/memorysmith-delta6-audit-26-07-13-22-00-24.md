# MemorySmith Audit — Delta Report 6 (Continued Deep Dive)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` · **Commit:** `e8a3065` (unchanged)
**Report generated:** 2026-07-13
**Relationship to prior reports:** new finding only, continuing the `ChatToolCatalog.cs` tool-handler review flagged as open scope in Delta Report 5.

**This pass covered:** full read of the `memorysmith_search`, `memorysmith_hybrid_search`, `memorysmith_context_pack`, `memorysmith_get`, `memorysmith_source_bundle`, and `memorysmith_find_by_source` tool handler bodies; a scripted extraction of all ~21 tool-name-to-`ChatToolRisk`-classification pairs to check for misclassification (none found — see below); and a repo-wide trace confirming the `ChatToolRisk` enum is genuinely enforced at multiple call sites (`McpController.cs`, `ChatServices.cs`, `AgentSessionService.cs`), ruling out a "declared but unenforced" pattern here (unlike the F21/F22 config-property findings from Delta Report 4).

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F29 | `memorysmith_source_bundle`'s `limit` parameter is unclamped — unlike three sibling `limit` parameters elsewhere in the same file, all of which cap at 50-100 — and its `ids` parameter has no count cap at all; combined with a per-source-link `maxFileBytes` clamp up to 1MB, a single MCP tool call can be made to read and return an effectively unbounded amount of file content | 90% | Medium (resource exhaustion / cost-control gap on an MCP-exposed tool) | **New** — no existing task covers input-bounds auditing across tool handlers |

**Also checked and ruled out this pass:** all ~21 extracted tool-name→risk-classification pairs look correct on inspection (writes classified `Write`, reads classified `ReadOnly`/`SensitiveRead` sensibly); the `ChatToolRisk` enum is confirmed genuinely enforced in `McpController.cs` (gates `SensitiveRead`/`Write` behind permission checks), `ChatServices.cs` (filters tool availability by risk in multiple places), and `AgentSessionService.cs` (scopes sub-agent tool sets by risk) — this is a well-wired safety mechanism, not another instance of the declared-but-inert config pattern from Delta Report 4.

---

## F29 — Unbounded `limit`/`ids` in `memorysmith_source_bundle` (Medium, 90%)

**File:** `MemorySmith.App/Services/ChatToolCatalog.cs`, `memorysmith_source_bundle` handler, lines ~222-261 (query path) and ~230-243 (ids path).

**The inconsistency, shown directly:** grepped every `ReadInt(args, "limit", ...)` call site in the file:
```
line 261: var limit = ReadInt(args, "limit", 10);                              // memorysmith_source_bundle — NOT clamped
line 396: var limit = Math.Clamp(ReadInt(args, "limit", 10), 1, 50);            // memorysmith_find_by_source
line 527: var limit = Math.Clamp(ReadInt(args, "limit", 10), 1, 50);            // (a third search-adjacent tool)
line 608: var limit = Math.Clamp(ReadInt(args, "limit", 25), 1, 100);           // (a fourth search-adjacent tool)
```
Three of four sibling call sites correctly clamp the caller-supplied `limit` to a sane maximum (50 or 100). The fourth — `memorysmith_source_bundle`, at line 261 — does not. Confirmed `ReadInt` itself performs no bounds enforcement of its own (it's a bare parse-or-fallback helper, `ChatToolCatalog.cs` lines 1380-1385), so there's no safety net catching this at a lower layer; the clamp has to be applied by each call site individually, and this is the one place it was missed.

**Why this specific miss is more expensive than the other three:** an unclamped `limit` on a plain search tool means "return more search-result rows than intended" — annoying, but each row is cheap. Here, `limit` controls how many memory records get pulled via the `query` path, and **every source link attached to every one of those records** then gets read via `ctx.Vars.ReadSourceAsync(sl, maxFileBytes)`, where `maxFileBytes` is separately clamped to up to `1,048,576` bytes (1MB) per link (line ~226: `Math.Clamp(ReadInt(args, "maxFileBytes", 16384), 1, 1048576)`). A caller supplying a large `limit` (e.g., `10000`) multiplies against however many source links each matched record has, each up to 1MB — there is no per-call aggregate cap on total bytes read or total records processed.

**A second, independent unbounded path in the same handler:** the `ids` parameter (comma-separated record IDs, lines ~230-243) has no count limit either — `ids.Split(',', ...)` is iterated in full with no `.Take(...)`, so a caller supplying a very long comma list gets every one of those records' source links read in the same unbounded way, with no `limit`-style parameter to even reason about here (there's nothing bounding "how many ids can I list" at all).

**Severity context:** this tool is `ChatToolRisk.SensitiveRead` and `AvailableInMcp: true` with `EnabledByDefaultInMcp: false` — so it's gated behind an explicit opt-in (per the `CanReadSourceBundleAsync` check confirmed in `McpController.cs`) and isn't exposed to every caller by default. That meaningfully lowers the practical risk (this isn't reachable by an arbitrary unauthenticated request), but it doesn't remove the gap for whoever *does* have that permission — including any agent session or MCP client granted `SensitiveRead` access, which per `AgentSessionService.cs` line 586 (`ChatToolRisk.SensitiveRead => canReadSensitive`) is a real, reachable grant in this system's own permission model, not a hypothetical.

**Recommendation:**
```csharp
var limit = Math.Clamp(ReadInt(args, "limit", 10), 1, 50);   // match the sibling pattern used 3 other places in this file
```
for the query path, and add a count cap on the `ids` path:
```csharp
var idList = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(50);
```
Also worth considering (separate, slightly larger change): an aggregate byte budget for the whole tool call — e.g., stop accumulating `entries` once a running total exceeds some configured ceiling (mirroring the spirit of `SourceLinks.MaxReadBytes`, but applied per-call rather than per-file) — since even a capped `limit`×`maxFileBytes` combination (50 records × several source links each × 1MB) can still add up to a very large single response. That's a design decision for whoever owns this tool's cost/latency budget, not something to bolt on reflexively; the two clamp fixes above are the immediate, low-risk part.
**Effort estimate:** 15 minutes for the two clamp/cap fixes; a half-day if the aggregate-byte-budget idea is also pursued, including a test that asserts a call requesting an oversized `limit`/`ids` list is rejected or truncated rather than processed in full.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- F29's severity assessment accounts for the tool's `EnabledByDefaultInMcp: false` + permission-gated status; if that gating is ever loosened (e.g., a future change makes this tool enabled-by-default), the same unclamped inputs become a materially bigger concern — worth re-checking this finding's severity if that gating changes.
- Did not extend this pass to the remaining ~18 tool handlers in `ChatToolCatalog.cs` beyond the ones read in full plus the scripted name/risk extraction — the file still has open scope for a subsequent pass if a complete line-by-line review of every handler body is wanted (in particular, the `memorysmith_memory_create`/`_update`, `memorysmith_page_save`/`_delete`, and task-mutation handlers, all classified `Write`, haven't had their bodies read in this engagement yet).
