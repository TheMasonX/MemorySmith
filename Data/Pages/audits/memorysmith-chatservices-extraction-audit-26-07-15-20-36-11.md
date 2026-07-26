# MemorySmith Audit — Delta Report: ChatServices.cs Extraction Verification
**Repo:** `TheMasonX/MemorySmith` · **Fixed point:** `master` · **Reviewed:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-15
**Method:** continued the `git diff master...dev/sprint-1` review from the prior Standards/Spec report, this time on `ChatServices.cs`'s large net-negative diff (-177 lines). Large deletions accompanied by "moved to X" comments are exactly where a refactor can silently drop or half-migrate something, so each of the two extracted blocks was traced to its new home and its new call sites verified, not assumed correct from the comment alone.

---

## Executive Summary

| # | Finding | Confidence | Severity | Notes |
|---|---|---|---|---|
| — | `SplitThinking`, `FormatRecordAsync`/`Read*Query`/`Format*Results` (→ `ChatToolCatalog.cs`), and `ShouldPreloadContext`+6 regexes (→ `ChatContextPlanner.cs`) were all traced end-to-end: genuinely moved/superseded, correctly wired, no orphaned references to the old removed methods | 95% | N/A (positive verification) | Worth stating explicitly — this was a nontrivial refactor (one extraction became a real feature expansion, not a pure move) and it holds up |
| F41 | `ChatToolCatalog.ReadSemanticQuery` / `FormatSemanticResults` are dead code — carried over during the move with zero call sites, leftover from the `memorysmith_semantic_search` tool's removal under TSK-0271 | 95% | Low (cleanup) | **New** |
| F42 | `ChatContextPlanner.Plan`'s code-intent early-return doesn't check `wantsContextPack` first, creating an inconsistent priority order relative to the rest of the method, where context-pack is treated as highest priority everywhere else | 70% | Low (tool-recommendation quality only, not correctness-critical — the LLM still chooses which tool to actually call) | **New** |

---

## Verified clean: the `ChatServices.cs` → `ChatContextPlanner.cs` extraction is a genuine (good) upgrade, not just a move

The diff's comment (`// ShouldPreloadContext + 6 regex fields — moved to ChatContextPlanner.cs (dead in ChatServices)`) undersells what actually happened. Traced it fully:
- The old `ShouldPreloadContext` returned a plain `bool`. The new `ChatContextPlanner.Plan` returns a `ChatContextPlan` record (`ShouldPreload`, `MemoryLimit`, `PageLimit`, `Reason`, `RecommendedToolName`) — a genuine feature expansion, not a lift-and-shift.
- Confirmed the original boolean gate logic is preserved exactly: the old method's `LocalKnowledgeRegex().IsMatch(...) || (Agent && AgentContextRegex().IsMatch(...))` reappears at `ChatContextPlanner.cs:48-53` as `!localKnowledge && !agentEvidence → skip`, logically equivalent, same regexes, same short-circuit ordering for the earlier exclusion checks (`ExactReplyRegex`/`SimpleNoContextRegex`/`AgentWriteCommandRegex`).
- Confirmed the caller side: `ChatServices.cs:2753-2754` (`BuildContextPlan`) calls `ChatContextPlanner.Plan(...)`, and `BuildContextAsync` (2756-2813) genuinely consumes the new `MemoryLimit`/`PageLimit`/`Reason`/`RecommendedToolName` fields (including in a structured debug-log call, `ChatLogEvents.ContextPreloadSkipped`), not just the old boolean — the richer return type is actually used, not dead weight.
- Confirmed no orphaned reference to the deleted `ShouldPreloadContext` remains anywhere in `ChatServices.cs`.

This is worth recording as a positive result for the same reason `BootstrapGate.cs`'s clean bill of health was worth recording in the prior report — an engagement this deep into finding problems should also say plainly when something holds up under the same scrutiny.

**Similarly verified for the `ChatToolCatalog.cs` extraction:** `FormatRecordAsync`, `ReadLexicalQuery`, `ReadHybridQuery`, `ReadContextPackQuery`, `FormatLexicalResults`, `FormatHybridResults`, `FormatContextPack` all exist at their new location and are called from the corresponding tool handlers (`memorysmith_search`, `memorysmith_hybrid_search`, `memorysmith_context_pack`) — correctly wired. This is where F41 below was found, as the one loose end in an otherwise clean move.

---

## F41 — Dead code left behind by the `memorysmith_semantic_search` tool removal (Low, 95%)

**File:** `MemorySmith.App/Services/ChatToolCatalog.cs`, lines 1297-1302 (`ReadSemanticQuery`) and 1524-1530 (`FormatSemanticResults`).

Both were carried over from `ChatServices.cs` during the extraction verified above, but neither has any call site anywhere in the file (confirmed via grep — only their own declarations match). This lines up exactly with a comment found in the same commit's `ChatContextPlanner.cs` (line 78-82): *"`memorysmith_unified_search` and `memorysmith_semantic_search` were deliberately removed from the tool catalog (TSK-0271 search-tool consolidation: both scored worst in spotcheck evals and overlap `hybrid_search`/`page_search`)."* The tool itself is correctly gone (confirmed in the earlier `ChatToolCatalog.cs` tool-name/risk extraction from a prior report — no `memorysmith_semantic_search` entry exists in the 21 registered tools), but its two supporting formatter/query-builder methods weren't deleted along with it — they moved files during this sprint's refactor instead of being cleaned up.

**Recommendation:** delete both methods. Zero risk — confirmed zero callers — and it's a small, concrete instance of exactly the "lift cleanly out of legacy, don't leave residue" principle this engagement has been asked to apply throughout. **Effort:** 10 minutes.

---

## F42 — `ChatContextPlanner`'s code-intent check bypasses the context-pack priority order (Low, 70%)

**File:** `MemorySmith.App/Services/ChatContextPlanner.cs`, lines 59-65:
```csharp
var wantsContextPack = ContextPackIntentRegex().IsMatch(message);
var wantsCode = CodeIntentRegex().IsMatch(message);

if (wantsCode && !wantsMemories && !wantsPages)
{
    return None("Detected codebase/source investigation intent.", "memorysmith_code_search");
}
...
var recommendedTool = wantsContextPack
    ? "memorysmith_context_pack"
    : memoryLimit == 0 && pageLimit > 0
        ? "memorysmith_page_search"
        : "memorysmith_hybrid_search";
```
`wantsContextPack` is computed before the code-intent early return but never consulted by it — a message matching both `CodeIntentRegex` and `ContextPackIntentRegex` (a plausible real phrasing: *"put together a context pack on how the code search feature works"*) with no memory/page intent match would hit the early return at line 62-65 and get routed to `memorysmith_code_search`, even though everywhere else in this method context-pack intent is treated as the **highest**-priority recommendation (it's the first branch checked in the final ternary at line 83). This is an inconsistent precedence order between the early-exit path and the main path, not a data-integrity or security issue — worst case, the planner's *suggestion* to the LLM is suboptimal for one narrow phrasing overlap, and the LLM is free to call whichever tool it judges correct regardless of the recommendation.

**Recommendation:** move the `wantsContextPack` check ahead of the code-intent early return, or fold it into the same condition (`if (wantsCode && !wantsContextPack && !wantsMemories && !wantsPages)`), so the method's priority order is consistent end-to-end. **Effort:** 15 minutes including a test case for a message matching both regexes.
**Confidence (70%, not higher):** this depends on how much real overlap exists between `CodeIntentRegex` and `ContextPackIntentRegex` in practice — I did not exhaustively test both regex patterns against a corpus of realistic phrasings to confirm how often this overlap actually triggers; the inconsistency is real and visible from the code, but its practical frequency is an assumption, not a measured fact.

---

## Assumptions

- Continued from the same fixed point (`master`, merge-base with `dev/sprint-1`) established in the prior report.
- F42's severity assessment assumes the planner's recommendation is advisory-only and the LLM tool-selection layer isn't rigidly bound to follow it — confirmed this is the design intent from the `ChatContextPlan.Summary` property's phrasing ("prefer X if more evidence is needed") but did not trace every consumer of `RecommendedToolName` to confirm none of them treat it as a hard directive rather than a suggestion.
- This report does not yet cover the remainder of `ChatServices.cs`'s diff beyond the two extracted blocks and the `SplitThinking`/GitHub-Copilot-retry-logging hunks already covered in the prior two reports — the file's diff has a few smaller hunks not yet individually verified, available for a further pass if wanted.
