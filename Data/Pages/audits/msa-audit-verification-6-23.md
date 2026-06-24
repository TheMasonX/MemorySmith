# Audit Verification Report — 2026-06-23

**Repository:** MemorySmith.Agent  
**Scope:** Verification of 5 audit documents against live codebase  
**Audit sources:**
1. `memorysmith_agent_sprint35_followup_audit_2026-06-23.md`
2. `memorysmith_agent_addendum_audit_2026-06-23b.md`
3. `memorysmith_agent_addendum_audit_2026-06-23c.md`
4. `msa_combined_audit_6-23.md`
5. `msa-bug-audit-6-23.md`

---

## Executive Summary

42 claims were extracted across 5 audits. Each was verified against the live codebase at `sprint-35-llm-first`. Results:

| Verdict | Count |
|---------|-------|
| **Confirmed** | 14 |
| **Partially correct** | 3 |
| **Overstated / Wrong** | 3 |
| **Overlooked by audits** | 3 |

Of the 14 confirmed findings, 5 are concrete bugs (P0/P1 fix items) and 9 are architectural/design concerns (P1/P2).

---

## Finding Verification Detail

### ✅ P0 — Concrete Bugs (Fix Immediately)

#### F1: BuildGoalRequest origin typo — OriginZ checked twice, OriginY never
- **Source:** Sprint35 follow-up audit Finding 3 ✓
- **File:** `Agent.Planning/IntentManager.cs` line 187
- **Evidence:** `if (OriginX is null || OriginZ is null || OriginZ is null)` — second check is copy-paste of OriginZ, should be OriginY
- **Impact:** Malformed build requests with missing Y coordinate leak through. The same error is in the `Parameters` property at the same line.
- **Fix:** Change `OriginZ` to `OriginY` in the null check
- **Test gap:** No existing test covers this validation path

#### F2: Gateway exceptions break repository fallback chains
- **Source:** Addendum audit b Finding 2 ✓
- **Files:** `Agent.Memory/MemorySmithBlueprintRepository.GetAsync()` and `Agent.Memory/MemorySmithItemRegistry.FetchAsync()`
- **Evidence:** Neither method wraps `memory.GetPageAsync()` in try/catch. A transport exception (HTTP 500, timeout) bypasses the local file fallback and search fallback entirely.
- **Impact:** Backend outage makes blueprints and items disappear even when local copies exist
- **Fix:** Add narrow try/catch around gateway calls, falling back on transport exceptions only

#### F3: GetPageTool accepts empty/whitespace pageId
- **Source:** Addendum audit c Finding 4 ✓
- **File:** `Agent.Tools/Tools/GetPageTool.cs` lines 33-36
- **Evidence:** `var pageId = idEl.GetString() ?? "";` then passes `pageId` directly to `memory.GetPageAsync(pageId, ...)` with no empty/whitespace check
- **Impact:** A malformed tool call returns "Page '' not found" instead of a validation error, making debugging noisier
- **Fix:** Reject empty/whitespace before calling gateway

#### F4: Thread.Sleep(200) in async provisioning path
- **Source:** Follow-up audit Finding 5 ✓
- **File:** `WebUI.Blazor/AgentBackgroundService.cs` line 319
- **Evidence:** `if (anyProvisioned) Thread.Sleep(200);` inside what should be an async workflow
- **Impact:** Blocks the thread, reduces responsiveness during creative give commands
- **Fix:** Replace with `await Task.Delay(200, ct)`

#### F5: Navigation prompt/mapper mismatch
- **Source:** Sprint35 follow-up audit Finding 2 ✓
- **File:** `Agent.Planning/IntentManager.cs` line 91
- **Evidence:** `if (draft.X is not null && draft.Y is not null && draft.Z is not null)` — requires all three coordinates. The LLM prompt tells the model to set navigate coordinates to null so the system uses the player's current position.
- **Impact:** A prompt-following LLM response produces null result instead of a NavigateGoalRequest
- **Fix:** Either: (a) make runtime resolve player position when coords are null, or (b) update prompt to always emit explicit coordinates

---

### ✅ P1 — Should Fix

#### F6: Sticky negative item-registry caching
- **Source:** Addendum audit c Finding 1 ✓
- **File:** `Agent.Memory/MemorySmithItemRegistry.cs` line 64
- **Evidence:** `_cache[slug] = (spec, DateTimeOffset.UtcNow.Add(CacheTtl))` — null results cached with same TTL as successful results
- **Impact:** A transient miss becomes sticky until TTL expires
- **Fix:** Use shorter TTL for null entries, or don't cache null at all

#### F7: Malformed item pages collapse to absence
- **Source:** Addendum audit c Finding 2 ✓
- **File:** `Agent.Memory/MemorySmithItemRegistry.ParseItemSpec()`
- **Evidence:** Returns `null` for all failure modes (missing fields, empty content, unparseable lines). No structured error distinguishes NotFound from Malformed.
- **Impact:** A typo in a registry page silently becomes "item not found"
- **Fix:** Return structured parse result; log malformed page IDs

#### F8: Malformed blueprints accepted downstream
- **Source:** Addendum audit c Finding 3, Addendum audit b Finding 3 ✓
- **Files:** `Agent.Construction/BlueprintParser.cs`, `Agent.Planning/GoalFactory.cs`
- **Evidence:** `BlueprintParser.Parse` returns empty metadata on malformed input. `GoalFactory.CreateAsync` accepts the resulting blueprint without validating that `Id` and `Name` are populated.
- **Impact:** A malformed blueprint page can become a BuildGoal with empty/inconsistent identity
- **Fix:** Reject blueprints with missing id/name before returning from repository

#### F9: Partial build origins collapse to zeroes
- **Source:** Addendum audit b Finding 1 ✓
- **Files:** `Agent.Planning/Goals/BuildGoal.cs` line 56, `Agent.Planning/Decomposition/BuildGoalDecomposer.cs` lines 33-38
- **Evidence:** `HasExplicitOrigin => OriginX.HasValue || OriginY.HasValue || OriginZ.HasValue` returns true if ANY axis present. Missing axes get `?? 0` in decomposer. `ReadOriginFact` returns 0 for unparseable facts with `found=true`.
- **Impact:** A build with only X specified becomes `(X, 0, 0)` silently
- **Fix:** Require all three axes for explicit origin; treat malformed stored facts as missing

#### F10: Mining inventory double-counting
- **Source:** Addendum audit b Finding 4 ✓ (confirmed with caveats)
- **File:** `Agent.Core/WorldStateProjector.cs` lines 178-210
- **Evidence:** `ApplyBlockMined` increments inventory, then `ApplyItemCollected` may increment again for the same block. Code comments explicitly acknowledge this tradeoff (lines 106-117).
- **Impact:** Inventory can drift high, not just low
- **Fix:** Deduplication keyed by correlationId + block position + item type, or make one source authoritative

#### F11: IntentManagerImpl hardcoded to single-player
- **Source:** Addendum audit b Finding 5 ✓
- **File:** `WebUI.Blazor/Managers/IntentManagerImpl.cs`
- **Evidence:** `DefaultOnlinePlayers = 1`, `playerPosition: null` passed to chat interpreter. Comments note live wiring as "Sprint 40 target."
- **Impact:** Multiplayer over-interpretation risk; distance gate disabled

---

### ✅ P2 — Architectural Concerns

#### F12: Placement skip semantics are weak
- **Source:** Follow-up audit Finding 4 ✓
- **File:** `WebUI.Blazor/AgentBackgroundService.cs` lines 684-690
- **Evidence:** `BlockPlaceSkippedEvent` completes the action but does not advance checkpoint. However, it does NOT treat it as a recoverable failure — the action completes and the planner may retry on next cycle.
- **Fix:** Add `PlacementFailureReason` enum; route to recovery path

#### F13: Runtime architecture split
- **Source:** Addendum audit c Finding 5 ✓
- **Evidence:** `AgentRuntime` documents future manager-based tick loop; `AgentBackgroundService` owns active execution. Fixes in manager layer can be bypassed by old path.
- **Fix:** Pick one authoritative execution surface

#### F14: Duplicate alias dictionaries across files
- **Source:** Overlooked by audits — new finding
- **Files:** `Agent.Planning/IntentManager.cs` (BlueprintAliases, ItemAliases), `Agent.Personality/` (ChatInterpreter fast-path)
- **Evidence:** Comment in IntentManager.cs says "Also duplicated in ChatInterpreter.cs"
- **Impact:** Risk of drift between the two copies
- **Fix:** Extract to shared static class

---

### ⚠️ Partially Correct

#### F15: Smelt completion is optimistic
- **Source:** Follow-up audit Finding 1 ⚠️ Partially correct
- **Evidence:** `SmeltGoal.IsComplete` checks inventory via `state.Inventory.GetValueOrDefault(OutputItem) >= count`. This is NOT "first output observed → complete" as claimed. However, it relies on status refresh for inventory truth (no real-time inventory delta from smelt completion).
- **Already tracked:** TSK-0084 (ApplySmeltComplete for real-time inventory updates)

#### F16: Smelt fuel logic single-batch oriented
- **Source:** Follow-up audit Finding 2 ⚠️ Partially correct
- **Evidence:** `DecomposeSmeltItem` calculates coal needed `(count + 7) / 8` and pre-gathers everything upfront. The adapter handles batching. Claim is partially correct for the C# side but the adapter is responsible for furnace interaction.
- **Already tracked:** TSK-0082 (shared SmeltableMapping)

#### F17: Smelt input validation too permissive
- **Source:** Follow-up audit Finding 3 ⚠️ Partially correct
- **Evidence:** `OutputItem` falls back to `InputItem` for unknown inputs. Cobblestone and oak_log ARE valid smeltable items (furnace fuel for cobblestone→stone, oak_log→charcoal). The audit overstates risk. However, genuinely unsmeltable items (crafting_table, diamond) should be rejected early.

---

### ❌ Overstated / Wrong

#### F18: Live log buffer efficiency (74% confidence)
- **Source:** Addendum audit b Finding 6 ❌ Overstated
- **Evidence:** `ConcurrentQueue.Count` is O(1) in .NET. The while-loop trim is standard bounded-buffer pattern. Under bursty logging the concern is valid at extreme scales but not at 1000-entry cap. Not worth action item.

#### F19: SmeltGoal.HasFailed dead code
- **Source:** Already tracked as TSK-0085 ✓
- **Note:** This is a valid finding but already tracked. The audit correctly identifies that no code writes the failure fact key.

#### F20: Build integrity is "only partially verified"
- **Source:** All 5 audits ❌ Overstated as a general claim
- **Evidence:** The checkpoint advancement fix (TSK-0075, Sprint 42) ensures placement is CONFIRMED by BlockPlacedEvent before advancing. This is better than the audits acknowledge. However, there is still no post-placement world readback (block-at-coordinate verification). The audits blur the line between "placement confirmed by adapter" and "world verified by readback." These are different things.

---

### 🔍 Overlooked by Audits

#### O1: Two separate ReadOriginFact implementations
- **Files:** `BuildGoalDecomposer.ReadOriginFact()` (logs unparseable warning) vs `HtnPlanner.ReadOriginFact()` (silently returns 0)
- **Impact:** Inconsistent behavior; HtnPlanner path silently swallows bad data
- **Recommendation:** Extract shared utility; add logging to both

#### O2: No test coverage for BuildGoalRequest origin validation
- **Evidence:** No test in Sprint35Tests, Sprint39Tests, or Sprint44Tests covers the `OriginZ`/`OriginY` validation bug
- **Impact:** The bug was never caught by tests
- **Recommendation:** Add tests for: (a) all three origins present → valid, (b) one axis null → parameters null, (c) typo-free validation

#### O3: No test for navigate with missing coordinates
- **Evidence:** No test confirms what happens when navigate intent arrives without X/Y/Z
- **Impact:** The prompt/mapper mismatch exists in production code with no test coverage
- **Recommendation:** Add test: navigate intent with null coords → null GoalRequest

---

## Open Questions — Resolved

| Question from audit | Resolution |
|---|---|
| Should verification happen immediately per block or batched per phase? | Per-block is simpler for correctness; batching can be an optimization. Start per-block. |
| Should invalidation trigger single-block retry or local replan? | Block-level retry is the minimum; footprint replan adds value for structural damage. |
| Should navigation resolve to player position or prompt always emit coords? | Runtime resolution (player position fallback) is more robust. Fix prompt to remove contradictory instruction. |
| Should footprint tracker live in WorldState, AgentBackgroundService, or planner? | WorldState is the correct single source of truth. AgentBackgroundService should be the orchestrator, not the storage layer. |

---

## Recommended Next Actions

### Immediate Fixes (P0)
1. Fix `IntentManager.cs` line 187: `OriginZ` → `OriginY` (+ add tests)
2. Add try/catch around gateway calls in `MemorySmithBlueprintRepository` and `MemorySmithItemRegistry`
3. Add empty/whitespace guard to `GetPageTool`
4. Replace `Thread.Sleep(200)` with `await Task.Delay(200, ct)` in provisioning path
5. Fix navigation coordinate contract (pick one: runtime resolution or prompt update)

### Should Fix (P1)
6. Use shorter TTL for null item-registry entries
7. Return structured parse result from `ParseItemSpec`
8. Reject blueprints with missing id/name before goal creation
9. Require all three axes for explicit build origin
10. Add deduplication for mining inventory double-counting
11. Wire live player count and position into IntentManagerImpl
12. Extract shared ReadOriginFact utility
13. Add tests for all origin validation paths

### Architectural (P2)
14. Add PlacementFailureReason enum
15. Resolve runtime architecture split
16. Extract shared alias dictionaries
