# Council Review: Audit Verification Task Prioritization & Refinement

## Decision

Prioritize the 5 verified P0 bugs as immediate sprint work, merge overlapping P1 tasks into consolidated implementation batches, and defer P2 architectural items until the Sprint 45 integrity theme is underway.

## Evidence Reviewed

- `Data/Pages/audits/msa-audit-verification-6-23.md` — full verification of 42 claims across 5 audit documents
- `Data/Memories/Core/agent-planner-architecture` — planner decomposition and routing architecture
- `Data/Memories/Core/agent-planner-task-library` — HTN planner task decomposition patterns
- `Data/Memories/Core/agent-sprint40-p0-implementation-status` — prior sprint completion evidence
- `Data/Memories/Core/agent-build-pipeline-state` — current build state
- `Agent.Planning/IntentManager.cs` line 187 — confirmed typo: `OriginZ || OriginZ` instead of `OriginZ || OriginY`
- `WebUI.Blazor/AgentBackgroundService.cs` line 319 — confirmed `Thread.Sleep(200)`
- `Agent.Tools/Tools/GetPageTool.cs` lines 33-36 — confirmed missing empty-pageId guard
- `Agent.Memory/MemorySmithBlueprintRepository.cs` — confirmed no try/catch around gateway calls
- `Agent.Memory/MemorySmithItemRegistry.cs` — confirmed null caching and malformed→null collapse
- Task list: TSK-0087 through TSK-0099 (new), TSK-0083 through TSK-0086 (existing backlog)

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---|---|
| **Source-Grounded Archivist** | Tasks TSK-0087, TSK-0090, TSK-0091 are single-line fixes with direct evidence — implement immediately as Sprint 45 cold-open. TSK-0088 (gateway fallback) and TSK-0089 (nav mismatch) need design decisions before coding. | 0.92 | The gateway try/catch has a design question: which exception types trigger fallback vs rethrow? Need to decide before implementation. |
| **Data Model Architect** | Merge TSK-0095 (all-axes origin) into TSK-0087 (origin typo) — they touch the same `BuildGoalRequest` record. Merge TSK-0097 (origin tests) into that batch too. Keep TSK-0098 (ReadOriginFact consolidation) separate since it's a different concern. | 0.88 | The `Parameters` property on `BuildGoalRequest` returns `null` when any axis is missing. This changes the contract: callers must handle null Parameters. Currently only `GoalFactory.CreateAsync` reads it, so risk is contained, but verify. |
| **Retrieval Specialist** | TSK-0088 (gateway fallback) and TSK-0092 (negative cache TTL) directly affect retrieval reliability. These are higher priority than the audit's "P1" label suggests because they address silent data loss during transient outages. Promote TSK-0088 to P0 alongside the other bugs. | 0.85 | The current fallback chain is advertised as resilient but isn't; fixing this changes the system's actual reliability characteristics significantly. |
| **Human Learning Advocate** | The task descriptions are good. Three improvements: (1) add "estimated effort" markers (minutes/hours/days) so a human can plan sprints; (2) merge the three test-coverage tasks (TSK-0083, TSK-0097, O3-navigate) into a single "Add origin/navigation validation tests" task; (3) TSK-0096 (mining double-counting) needs a decision about which approach to take before implementation. | 0.82 | Without effort estimates, a human reading the backlog can't tell if this is 2 hours or 2 days of work. Merge or tag clearly. |
| **Skeptical Reviewer** | Three concerns: (1) TSK-0088 scope is larger than described — both `MemorySmithBlueprintRepository` and `MemorySmithItemRegistry` need changes, and the try/catch boundary must distinguish transport errors from application errors. (2) TSK-0089 (nav mismatch) has two possible fixes with different scope — the task should pick one. (3) TSK-0093 (structured parse result) changes a public API (`ParseItemSpec` is `public static`) and may break tests. Verify downstream callers. | 0.78 | The six new P0/P1 tasks plus three existing backlog items (TSK-0083/84/85) together represent a substantial Sprint 45. Consider splitting into two batches rather than one. |
| **Synthesizer** | Sprint 45 should have two waves: **Wave A** (cold-open): TSK-0087 + TSK-0095 + TSK-0097 (origin fix + test), TSK-0090 (GetPageTool), TSK-0091 (Thread.Sleep), TSK-0088 (gateway fallback, P0-promoted). **Wave B**: TSK-0089 (nav decision + fix), TSK-0092 (negative cache), TSK-0094 (blueprint validation), TSK-0093 (parse result), TSK-0096 (double-counting). Defer to Sprint 45+: TSK-0098 (ReadOriginFact), TSK-0099 (alias extraction), TSK-0083 (checkpoint tests — partially done), TSK-0084 (ApplySmeltComplete), TSK-0085 (HasFailed). | 0.90 | The `Thread.Sleep` fix (TSK-0091) must verify the enclosing method is `async Task` — if it's `void` or synchronous, the fix requires a method signature change. Verify before coding. |

## Synthesis

### Changes now (Wave A — Sprint 45 cold-open)

1. **TSK-0087 + TSK-0095 merged**: Fix `OriginZ` typo + require all three axes for explicit origin + add validation tests (absorbs TSK-0097).
   - Fix: Change `OriginZ` to `OriginY` on line 187
   - Fix: `HasExplicitOrigin` requires `OriginX.HasValue && OriginY.HasValue && OriginZ.HasValue`
   - Tests: all-three-present, one-missing, all-null
   - Risk: The `Parameters` property becomes `null` when any axis is missing — verify `GoalFactory.CreateAsync` handles null Parameters

2. **TSK-0090**: Add empty/whitespace guard to `GetPageTool.ExecuteAsync`
   - Fix: `if (string.IsNullOrWhiteSpace(pageId)) return new ToolResult(false, "pageId must be a non-empty string.");`
   - Tests: `""` and `"   "` cases

3. **TSK-0091**: Replace `Thread.Sleep(200)` with `await Task.Delay(200, ct)`
   - Pre-flight: Verify the enclosing method signature is `async Task` (not `void` or sync)
   - Risk: Low — the surrounding creative provisioning block is already inside an async method

4. **TSK-0088 (promoted to P0)**: Add try/catch around gateway calls
   - Scope: `MemorySmithBlueprintRepository.GetAsync()` and `MemorySmithItemRegistry.FetchAsync()`
   - Catch: `HttpRequestException` and `TaskCanceledException` (timeout)
   - Log: exception type + page/item ID
   - Continue to local fallback on transport failure only
   - Do NOT catch on "not found" (empty content)

### Changes next (Wave B — Sprint 45 remainder)

5. **TSK-0089**: Resolve navigation coordinate contract
   - Pick option (a): runtime resolves player position when coords are null
   - Update prompt to match: remove contradictory "leave coords null" instruction
   - Tests: navigate with null coords → null GoalRequest (currently missing)

6. **TSK-0092**: Use shorter TTL for null cache entries (5s vs 60s) or don't cache null at all

7. **TSK-0094**: Reject blueprints with missing id/name in repository before returning

8. **TSK-0093**: Return structured parse result from `ParseItemSpec`
   - Check: `ParseItemSpec` is `public static` — verify test callers before changing return type

9. **TSK-0096**: Add deduplication for mining inventory double-counting
   - Needs decision: Make one source authoritative (preferred) vs correlation-based dedup

### Deferred (Sprint 45+)

- TSK-0098 (ReadOriginFact consolidation) — low impact, can batch with other BuildGoal refactoring
- TSK-0099 (alias extraction) — P2, batch with prompt cleanup
- TSK-0083 (checkpoint tests) — partially done in Sprint44Tests; complete remaining ~50%
- TSK-0084 (ApplySmeltComplete) — needs council's Wave B completion first
- TSK-0085 (HasFailed dead code) — P2, impacts smelt and craft goals equally

## Dissent

**Disagreement on merge strategy.** The Retrieval Specialist argues TSK-0088 should be Wave A because it affects production reliability. The Skeptical Reviewer agrees on priority but notes the scope is larger than a single-line fix and requires careful design of exception-type filtering. **Resolution:** Promote to P0 but place at end of Wave A to allow design time.

**Disagreement on approach for TSK-0089.** The Source-Grounded Archivist prefers updating the prompt only (less code risk). The Data Model Architect prefers runtime player-position resolution (more robust). **Resolution:** Defer to Wave B with an explicit sub-task to pick approach via a mini-council or ADR.

## Acceptance Criteria

- [ ] All Wave A tasks have passing NUnit tests that cover the fix and regression cases
- [ ] `dotnet test MemorySmith.Agent.Tests` passes with zero failures
- [ ] `dotnet build` passes with zero warnings (TreatWarningsAsErrors is enabled)
- [ ] TSK-0088 distinguishes transport exceptions from application-level "not found"
- [ ] TSK-0091 verifies the enclosing method is `async Task` before changing Thread.Sleep
- [ ] Wave A tasks are completed before Wave B begins (no partial Wave A)

## Open Questions

1. **TSK-0091 pre-flight**: Is `ProvisionCreativeBuild` (or whatever method contains line 319) declared as `async Task`? If not, changing `Thread.Sleep` to `await Task.Delay` requires a signature change plus all callers.
2. **TSK-0088 catch surface**: Should we catch `HttpRequestException` only, or also `TimeoutException` and `OperationCanceledException`? The cancellation token is already passed — `OperationCanceledException` should propagate, not be swallowed.
3. **TSK-0096 approach decision**: Make `ItemCollectedEvent` the sole authoritative inventory source for mined blocks, or add correlation-based dedup? The former is simpler but risks stuck-at-zero for non-collected drops. The latter is more complex but preserves the safety net.
