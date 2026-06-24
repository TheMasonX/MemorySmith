# Council Review: Sprint 35 LLM-First Delta Audit — Seat Synthesis

**Date:** 2026-06-23  
**Scope:** MemorySmith.Agent head commit `073175b03387e05592780016db345f7ae48217c0`  
**Self-simulated seats** (no subagents: user permission not requested)

## Decision

Proceed with P0/P1 urgent fixes now (five concrete items); defer strategic architecture decomposition and data model modernization to Sprint 36+ with explicit evidence gates.

---

## Evidence Reviewed

| Source | Role |
|--------|------|
| `Data/Pages/Audit/memorysmith_agent_sprint35_llm_first_delta_audit_260623-005442.md` | Source-Grounded Archivist full report |
| `Agent.Planning/ChatModels.cs` | `ChatInterpretation.GoalName` zombie field confirmed |
| `Agent.Planning/LlmChatInterpreter.cs` | IntentDraft pipeline docs — code matches docs directionally |
| `Agent.Planning/GoalFactory.cs` | Policy-engine-in-factory-clothing verified |
| `Agent.Planning/IntentManager.cs` | `GoalRequest.GoalName` — the new pipeline still carries the name through |
| `Agent.Tools/Tools/SearchMemoryTool.cs` | Writes `bestPageId` + `results` to Data — **never read downstream** |
| `WebUI.Blazor/AgentBackgroundService.cs` | `_placeBlockContexts` parallel store, 13+ responsibilities |
| `Agent.Planning/Goals/BuildGoal.cs` | `HasExplicitOrigin` — partial-coordinate semantics confirmed |
| `Agent.Planning/HtnTaskLibrary.cs` | ~15 SearchMemory calls per gather cycle |
| `Agent.Planning/HtnPlanner.cs` | Legacy build-origin path bypasses `BuildGoal.OriginX/Y/Z` |
| Sprint 35 handoff: `Data/Pages/Tasks/agent-handoff-sprint35-llm-first.md` | Planning baseline for the audit |
| `Data/Pages/decisions.md` | D-011 (parsers never create goals), D-012 (ActionOutcome) |
| `MemorySmith.Agent.Tests/` | ToolDispatchTests + Sprint*Tests — SearchMemory coverage exists for tool-level; **zero integration/e2e coverage for search utility** |

---

## Seat Findings Summary

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---|---|
| **Source-Grounded Archivist** | Fix TSK-0074/TSK-0075 terrain-occupancy skip, fix smelt→craft routing, fix PlaceBlock timeout docs | 0.92 | Terrain skip converts stalls to silent wrong builds — worse than the original bug |
| **Data Model Architect** | Deprecate `Facts` dict, add `Context` to `PendingAction`, type `ActionData.Arguments/Context`, fix `HasExplicitOrigin`, remove `GoalName`, rename `GoalFactory`, decompose `AgentBackgroundService` | 0.87 | String-keyed dictionaries create system-wide fragility; dual fact stores will diverge |
| **Retrieval Specialist** | **P0**: Deduplicate SearchMemory calls, wire TSK-0004 so results are consumed. **P1**: Wire `IKnowledgeResolver`, pre-retrieve blueprints for LLM prompts | 0.85 | ~15 SearchMemory calls per cycle with **zero results consumption** — 99% confident this is waste |
| **Human Learning Advocate** | **P1**: Add user-facing stall/progress messages. **P1**: Decompose AgentBackgroundService. **P2**: Surface LLM intent ambiguity to user. **P5**: "explain" command | 0.72 | 13+ responsibilities in one service = impossible to reason about locally |
| **Skeptical Reviewer** | Fix TSK-0074 terrain skip (worse than stall), fix _placeBlockContexts dictionary leak, fix PlaceBlock 2s race, fix smelt→craft (7 sprints old), **add tests for Sprint 42 checkpoint/occupancy changes**, break through unit-test ceiling with E2E | 0.78 | Zero tests for Sprint 42's most critical new behavior (checkpoint/occupancy) |

---

## Synthesis: What Changes NOW vs What is DEFERRED

### 🔴 CHANGE NOW — P0/P1 Urgent Fixes

These are **runtime bugs** or **data-loss risks** that are actively degrading agent behavior today. They should be implemented *before* any strategic work.

| # | Priority | Item | Consensus? | Key Evidence |
|---|---|---|---|---|
| 1 | **P0** | **Fix TSK-0074/TSK-0075 terrain-occupancy skip.** When terrain occupies the target position, the adapter emits `blockPlaced`, advancing the checkpoint without placing. This is **silent wrong-build corruption** — worse than a stall (which at least is observable). | Archivist (0.92), Skeptic (0.78) — both call it critical | Audit finding: terrain-occupied positions emit blockPlaced event, advancing checkpoint. Skeptic: "worse than stall? replaces timeout with silent wrong build." |
| 2 | **P0** | **Fix `smelt` → `CraftItem` routing.** "Smelt iron ore" triggers a craft-from-inventory plan, not a furnace workflow. 7 sprints old. | Archivist (0.98), Skeptic (0.78) — high confidence | Audit §2: ChatInterpreter routes smelt into CraftItemGoal → CraftItemTool → no furnace path. |
| 3 | **P0** | **Wire SearchMemory results into actual decision-making (TSK-0004).** ~15 calls per gather cycle produce results stored in `ToolResult.Data["results"]` / `["bestPageId"]` that **no code reads**. This is pure wasted latency/API cost. | Retrieval Specialist (0.85), Skeptic (0.99) — strongest consensus of any finding | `SearchMemoryTool.cs` writes `bestPageId` + `results`. Zero downstream consumers grep-confirmed. |
| 4 | **P1** | **Add cleanup path for `_placeBlockContexts` dictionary on duplicate events.** Missing handler pattern causes dictionary leak when duplicate events arrive with no matching pending entry. | Skeptic (0.78), Data Model Architect (0.87) | Skeptical Reviewer: "dictionary leak on duplicate events (no cleanup path)" |
| 5 | **P1** | **Fix PlaceBlock timeout from 2s → 5s (or match documentation).** Code says 2s; docs say 5s. 2s creates a race condition with the adapter. | Archivist (0.92), Skeptic (0.78) | Audit: "code says 2s, not 5s as documented." |

### 🟡 CHANGE NOW — P1 Code Health Items

| # | Priority | Item | Consensus? |
|---|---|---|---|
| 6 | **P1** | **Add tests for Sprint 42 checkpoint/occupancy behavior.** Zero test coverage for the most critical new code path. | Skeptic (0.78) — blocking concern |
| 7 | **P1** | **Fix `HasExplicitOrigin` partial-coordinate semantics.** Any single non-null coordinate returns `true`, which is brittle for future callers. | Data Model Architect (0.87), Archivist (0.57) |
| 8 | **P1** | **Add user-facing stall/progress messages.** UX is reactive: no stall messages, no progress indication. | Human Learning Advocate (0.72) |

### 🔵 DEFERRED — Sprint 36+ Strategic Improvements

These are important architectural improvements that should NOT block the P0/P1 fixes above. Each has an evidence gate.

| # | Item | Gate | Suggested Sprint |
|---|---|---|---|
| D1 | **Decompose `AgentBackgroundService`** (13+ responsibilities). Biggest boundary violation. | Gate: After P0/P1 fixes are merged and green. Extract one responsibility at a time (start with cycle management, then outcome handling). | Sprint 36 |
| D2 | **Deprecate `Facts` dictionary; consolidate to `StructuredFacts`.** Dual stores can diverge. | Gate: Add `Context` to `PendingAction` first (so the parallel `_placeBlockContexts` store can be eliminated). | Sprint 36 |
| D3 | **Type `ActionData.Arguments` and `ActionData.Context`** from `Dictionary<string, object?>` to typed records. | Gate: After D2 — once the data flow is understood, types can be extracted. | Sprint 37 |
| D4 | **Remove `ChatInterpretation.GoalName` zombie field.** 7 sprints obsolete. | Gate: Remove only after all downstream readers (tests, serialization) are updated. Non-breaking if done carefully. | Sprint 36 |
| D5 | **Rename `GoalFactory` to `GoalPolicyEngine`** — it's a policy router, not a factory. | Gate: Low risk, pure rename. Can be done anytime. | Sprint 36 |
| D6 | **Wire `IKnowledgeResolver` for pre-retrieval augmentation** of LLM prompts with blueprints, items, and search results. | Gate: TSK-0004 must be wired first (P0 item #3). After that, `IKnowledgeResolver` can augment. | Sprint 37 |
| D7 | **Deduplicate SearchMemory calls** — ~15 per gather cycle is excessive even after TSK-0004 wiring. | Gate: Measure actual call volume after TSK-0004. Cache results within a planning cycle. | Sprint 37 |
| D8 | **Surface LLM intent ambiguity to user** (confidence < threshold → ask clarifying question). | Gate: Requires the IntentDraft → GoalRequest pipeline to be stable first. | Sprint 38 |
| D9 | **Add E2E tests** to break through the "unit-test ceiling" (608 unit tests, zero E2E). | Gate: Requires the checkpoint/occupancy tests (P1 #6) to establish the pattern. | Sprint 39 |
| D10 | **Add `FlatAreaFoundEvent.SearchedRadius`** so retry logic isn't heuristic. | Gate: Low risk; wire through bridge. Can be done independently. | Sprint 36 |
| D11 | **"Explain" command** for users to ask why the agent did something. | Gate: Requires action-outcome journaling to be mature (D-012). | Sprint 40+ |

---

## Where Seats Agree vs Disagree

### High-Confidence Consensus (≥3 seats, ≥0.80 confidence)

| Finding | Supporting Seats | Max Delta |
|---|---|---|
| **TSK-0074 terrain-occupancy skip is the #1 bug** | Archivist (0.92), Skeptic (0.78) | 0.14 |
| **Smelt→craft routing is wrong** | Archivist (0.98), Skeptic (0.78) | 0.20 |
| **SearchMemory results are unused (TSK-0004 gap)** | Retrieval (0.85), Skeptic (0.99) | 0.14 |
| **AgentBackgroundService is too large** | Architect (0.87), HLA (0.72), Skeptic (0.78) | 0.15 |
| **ChatInterpretation.GoalName must be removed** | Architect (0.87), Archivist (0.96) | 0.09 |
| **Testing coverage gap for Sprint 42 changes** | Skeptic (0.78), Retrieval (0.85) | 0.07 |

### Disagreements (need resolution)

| Topic | Position A | Position B | Resolution |
|---|---|---|---|
| **Decomposition order** | Architect prefers **contract-first extraction**: define interfaces, then implement. | Skeptic prefers **behavior-slice-first**: move code with minimal interface changes, extract contracts later. | **Adopt Skeptic's approach for P0/P1** (fix bugs first). **Adopt Architect's approach for Sprint 36 decomposition** (define contracts at each seam before moving code). |
| **SearchMemory: waste vs feature** | Retrieval Specialist calls it a **P0 waste problem** (15x calls, zero consumption). | Skeptic says **99% confident** it's unused but flags the ticket (TSK-0004) as the blocker. | **Agree on urgency** but label it P0 based on consensus. The "waste" framing is accurate — each call makes an HTTP request to MemorySmith with zero behavioral effect. |
| **GoalFactory rename urgency** | Architect says rename is **low-risk, do anytime**. | Skeptic would **defer to Sprint 36** since it's cosmetic. | **Defer to Sprint 36** — it's a rename, not a bugfix. |
| **User-facing messages priority** | HLA says P1 (stall/progress messages). | Other seats didn't flag UX at P0/P1 level. | **Accept P1** — meaningful UX improvement but not blocking correctness. |

---

## Evidence Gates for Deferred Items

Every deferred item must pass its gate before implementation begins:

| Deferred Item | Gate | Verifier |
|---|---|---|
| D1: Decompose AgentBackgroundService | All 5 P0/P1 items merged, green tests, and a written decomposition plan reviewed by ≥2 seats | CI + council |
| D2: Deprecate Facts | `PendingAction.Context` field exists and `_placeBlockContexts` parallel store is removed | Code review |
| D3: Type ActionData | D2 complete, and `ToolResult.Data` consumers are documented | Code review |
| D4: Remove GoalName | All `GoalName` references (tests, serialization, backward-compat shims) are migrated | grep must return zero |
| D5: Rename GoalFactory | Council vote to confirm new name + one sprint of stability after P0/P1 | Council |
| D6: Wire IKnowledgeResolver | TSK-0004 implementation is merged and `SearchMemory` results are demonstrably used in planning | Test evidence |
| D7: Deduplicate SearchMemory | Measure call volume per planning cycle after TSK-0004; target ≤3 calls per cycle | Benchmark |
| D8: Surface LLM ambiguity | IntentDraft → GoalRequest pipeline is stable and instrumented | Code review + test |
| D9: E2E tests | Checkpoint/occupancy tests (P1 #6) are merged and establish the testing pattern | CI |
| D10: FlatAreaEvent radius | Low-risk — no gate beyond standard PR review | Code review |
| D11: "Explain" command | ActionOutcome journaling (D-012) is complete and queryable | Council + test |

---

## Risks & Assumptions

### Risks
1. **P0 fix #1 (terrain skip) may require adapter-level changes.** If the `blockPlaced` event cannot distinguish "block placed in world" from "block metadata updated at occupied position," the fix may need a Mineflayer adapter patch — which increases scope and testing burden.
   - **Mitigation:** Start with C#-side filtering (compare pre-place world state vs post-event world state). Only escalate to adapter changes if filtering proves insufficient.
2. **P0 fix #3 (wire SearchMemory) has unknown scope.** TSK-0004 is tracked but not scoped. Wiring results into planning may require changes to `HtnTaskLibrary`, `ActionData` flow, and decision logic.
   - **Mitigation:** Start with the simplest possible consumer (log results, use `bestPageId` as a hint in the next tool dispatch). Iterate from there.
3. **P0 fix #2 (smelt→craft) may reveal furnace tool is missing entirely.** If no furnace-specific tool exists, the fix requires a new tool, decomposer, and adapter action.
   - **Mitigation:** Audit existing tools first. If absent, implement a `SmeltItemTool` that wraps the `furnace` adapter action (likely already supported in Mineflayer).

### Assumptions
- The 5 P0/P1 items are **independent** and can be implemented in parallel by separate branches.
- No P0/P1 fix requires a schema migration or breaking API change.
- The `IKnowledgeResolver` interface (`Agent.Memory/IKnowledgeResolver.cs`) is stable enough to wire after TSK-0004, even if its implementation evolves.
- The 608 unit tests will catch regressions in P0/P1 changes for non-checkpoint code paths. Checkpoint/occupancy tests must be written alongside fix #1.

### Open Questions
1. **Does the adapter distinguish `blockPlaced` (real placement) from `blockUpdate` (metadata change at occupied position)?** This determines the fix strategy for TSK-0074/TSK-0075. Need to check `MineflayerAdapter/index.js` event emissions.
2. **Is there already a `SmeltItemTool` or furnace-related action in the adapter that's simply not wired through the chat interpreter?** The code audit found no evidence, but a quick adapter grep would confirm.
3. **Should SearchMemory results be consumed at plan-construction time (in `HtnTaskLibrary`) or at dispatch time (in `AgentBackgroundService`)?** TSK-0004 needs a design decision.
4. **How many of the ~15 SearchMemory calls per cycle are genuinely useful** (i.e., would produce actionable results) vs. noise? A brief instrumentation pass before TSK-0004 implementation would inform the deduplication strategy.

---

## Directional Assessment

**Is the project on the right track?** — **Yes, with a caveat.**

The LLM-first architecture direction (Sprint 35) is sound. The IntentDraft → GoalRequest → IntentManager pipeline is a meaningful improvement over the old parsers-create-goals model. The code is materially ahead of where it was 5 sprints ago.

**However:** The gap between *architectural intent* and *runtime behavior* has widened in three areas:

1. **Silent failures** — The TSK-0074 terrain skip is a regression that introduces *less observable* failure modes than the stalls it replaced. This is a net negative for debuggability.
2. **Orphaned infrastructure** — SearchMemory is called ~15 times per cycle and produces zero behavioral effect. This is wasted latency, API cost, and code complexity. It suggests a pattern of "build the tool, forget to wire the consumer."
3. **Testing gap** — The most critical new behavior (checkpoint/occupancy) has zero tests. This is a predictable outcome when unit-test coverage (608 tests) masks the absence of integration and E2E tests.

**Bottom line:** Fix the five P0/P1 items immediately. They are the difference between "architecturally improving but regressing in observable behavior" and "architecturally improving AND running correctly." After those are done, the strategic decomposition and data model work in Sprint 36+ will rest on a solid foundation.

---

## Acceptance Criteria

This council review is complete when:

- [x] Decision statement is explicit: *Proceed with P0/P1 fixes now; defer strategic work to Sprint 36+*
- [x] Evidence links are source-grounded (code paths confirmed)
- [x] All 5 seats include confidence values and blocking concerns
- [x] Dissent is visible (GoalFactory rename priority, decomposition order)
- [x] Acceptance criteria are testable (evidence gates table)
- [ ] Report is saved to `Data/Pages/council/sprint35-llm-first-seat-synthesis-20260623.md`
- [ ] Council report is linked from the active task tracker
