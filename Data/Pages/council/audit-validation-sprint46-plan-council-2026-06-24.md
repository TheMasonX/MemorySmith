# Council Review: Audit Findings Validation and Sprint 46 Plan

## Decision

Sprint 46 must shift from adding features to hardening observability, error-handling contracts, and runtime decomposition — the three audits collectively show that silent fallbacks and null-overloading are now the highest-leverage sources of brittleness, outweighing any feature gap.

## Evidence Reviewed

- **`Data/Pages/audits/sprint-45-audit-6-24.md`** — 21 findings across 6 scoring dimensions; runtime architecture scored 5/10, observability 4/10, error handling 4/10
- **`Data/Pages/audits/memorysmith_agent_deep_dive_audit_2026-06-24.md`** — 9 findings + 3 architectural opportunities; strongest on build-origin, null-overloading, and planner decomposition
- **`Data/Pages/audits/memorysmith_agent_deep_dive_audit_addendum_2026-06-24.md`** — 6 supplementary findings; partial-origin acceptance at 95% confidence, auth fail-open at 97%
- **`Data/Pages/council/sprint35-llm-first-seat-synthesis-20260623.md`** — Sprint 45 Wave A/B plan
- Source code: `BuildGoal.cs` (lines 64-68, `HasExplicitOrigin` OR logic), `BuildFactKeys.cs` (line 17, sentinel doc), `HtnPlanner.cs` (lines 169-196, catch→null + originalGoal unused), `RestMemoryGatewayOptions.cs` (lines 38-52, WorldKbUrl fallback), `LlmChatInterpreter.cs` (lines 330, 403, catch→null), `WebSocketBridge.cs` (line 378, catch→null), API provider files (Anthropic/Gemini/OpenAI catch→null), `Program.cs` (lines 392-400, stale version)
- Core memories: `agent-runtime-lifecycle`, `agent-planner-architecture`, `agent-goal-types-catalog`, `agent-architecture-bounded-contexts`, `agent-phase-status-current`
- Task files: TSK-0083 through TSK-0099
- Council skill: `.github/skills/council/SKILL.md`
- Agent guidelines: `AGENTS.md`

## Methods

This review was conducted as a 6-seat subagent council with explicit user permission. Each seat was given the full evidence pack and asked to produce independent findings, risks, recommendations, assumptions, open questions, and confidence percentages. The Synthesizer merged all reviews into a unified plan.

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---|---|
| **Source-Grounded Archivist** | 13 of 14 validated claims are accurate; 8 are worse than originally claimed. The most actionable new finding is the 3-LLM-provider catch→null cluster (M-1) and the WebSocketBridge fire-and-forget receive loop (M-2). Fix those first. | 0.92 | The audit missed: ChatServices.cs `20+` bare catch blocks in the MemorySmith base repo. That's outside scope for the Agent repo but must be surfaced as a cross-repo request. |
| **Data Model Architect** | The null-overloading problem (11+ meanings) is the single highest-leverage schema fix. Introduce `ReplanResult`, `ParseResult`, and a `BuildOrigin` value object. These three schema changes eliminate entire classes of bugs without changing any behavioral logic. | 0.88 | `BuildOrigin` value object changes `HasExplicitOrigin` semantics from OR to AND — this is a breaking change requiring updates to all 4 origin-resolution touch points. Must be gated behind tests first. |
| **Retrieval Specialist** | World KB fallback (Validated Claim 7) is the only retrieval-affecting finding. The alias-drift (Claim 10) is a semantic retrieval concern — different canonical IDs produce different search results. Neither is P0. | 0.85 | The alias-drift (TSK-0099) has LOW priority but the semantic impact is real: `"iron"→"raw_iron"` vs `"iron"→"iron_ore"` means search returns different results depending on path. |
| **Human Learning Advocate** | The silent-failure culture hurts developers most. 5 distinct silent-failure patterns (catch→null, catch→return false, catch{}, fire-and-forget, ReceiveLoopAsync) mean every bug investigation starts with "did it actually fail?" not "what went wrong?" | 0.82 | Without structured error results, every bug requires reading debug logs. The 3 LLM providers' catch→null means LLM integration bugs are invisible until a user reports "the bot ignored me." |
| **Skeptical Reviewer** | The audit claims are well-supported but over-prioritize new architectural constructs. `BuildOrigin` value object and `ReplanResult` are good designs but building them doesn't fix the core problem — silent failures. Fix the catch→null blocks with logging first (5-minute fixes), then design new types. | 0.78 | Claim 14 (3 origin layers) is architecturally messy but has caused zero known bugs. Replan losing goal context (Claim 8) IS a real bug pattern. Merge claims 1, 2, 3, 14 into one "BuildOrigin consolidation" task. |
| **Synthesizer** | Sprint 46 must address ALL catch→null and fire-and-forget paths as P0, then BuildOrigin consolidation as P1, then runtime decomposition as P2. The 3-LLM-provider catch→null plus ChatServices catch blocks are the most impactful 10-minute fix in the entire backlog. | 0.90 | The Sprint 45 plan (Wave A/B) was well-executed based on the previous council report. These audits surface a deeper layer of issues that Wave A/B didn't touch. Sprint 46 needs a different theme. |

## Synthesis

**SPRINT 46 THEME: "Observability First"** — fix all silent failure paths, then consolidate the build-origin model, then begin runtime decomposition planning.

### Sprint 46 Plan

| Priority | Area | Description | Task ref |
|---|---|---|---|
| **P0** | Observability | WebSocketBridge.ReceiveLoopAsync: wrap in try-catch with log + reconnect | TSK-0100 |
| **P0** | Observability | All 7 catch→null paths: add LogWarning before returning null | TSK-0101 |
| **P0** | Cross-repo | Document ChatServices.cs 20+ bare catch blocks as base-repo request | TSK-0102 |
| **P1** | BuildOrigin | Consolidate BuildOrigin model: merge TSK-0098 + TSK-0095 into single value object | TSK-0103 |
| **P1** | Planner | Introduce ReplanResult type + fix unused originalGoal parameter in ReplanAsync | TSK-0104 |
| **P2** | Docs | Fix documentation drift: ReplanGovernor, HtnPlanner, phase-status memory | TSK-0105 |
| **P2** | Aliases | Extract shared alias dictionaries (TSK-0099) — promote from P3 | TSK-0099 |
| **P2** | Tests | Add error-path tests covering all catch→null and fire-and-forget locations | TSK-0106 |
| **P3** | Tests | Complete remaining checkpoint tests (TSK-0083 remaining ~50%) | TSK-0083 |
| **P3** | Architecture | Runtime decomposition planning: scope 6 interfaces, create extraction plan | TSK-0107 |
| **P3** | Cleanup | State duplication (M-4): remove redundant _currentGoal field access | TSK-0108 |

### Updated Existing Tasks

- **TSK-0099 (alias extraction):** PROMOTE from P3 to P2. The semantic drift (`"iron"→"raw_iron"` vs `"iron"→"iron_ore"`) has higher retrieval impact than previously assessed. Still defer to Sprint 46 Wave B after P0 items.
- **TSK-0098 (ReadOriginFact consolidation):** ABSORB into TSK-0103 (BuildOrigin value object). Both implementations will be replaced by the shared resolver. Close TSK-0098 as "merged into TSK-0103."
- **TSK-0083 (checkpoint tests):** RETAIN at P3. The remaining ~50% of checkpoint tests are needed but not blocking anything. Move to Sprint 47.
- **TSK-0084 (ApplySmeltComplete):** RETAIN at P3. Defer until smelting functionality matures.
- **TSK-0085 (HasFailed dead code):** RETAIN at P3. Low priority — affects smelt and craft goals equally.
- **TSK-0086 (stale doc comments):** ABSORB by TSK-0105. Close as "merged into TSK-0105."
- **TSK-0096 (mining double-counting):** RETAIN at P1 (Wave B from Sprint 45). Not addressed by Sprint 46 theme. Move to Sprint 47.
- **TSK-0093 (structured parse result):** RETAIN at P1. The `ParseResult<T>` type from TSK-0104 may absorb this — reassign after TSK-0104 implementation.

## Dissent

**Disagreement on BuildOrigin sentinel severity.** The Source-Grounded Archivist and Data Model Architect rate the sentinel-overloading as HIGH because it violates the "no magic numbers" rule from `AGENTS.md`. The Skeptical Reviewer argues the collision probability is ~0.1% and the sentinel is documented. **Resolution:** Fix during BuildOrigin consolidation (P1) but do NOT prioritize separately.

**Disagreement on ReplanResult priority.** The Human Learning Advocate argues it should be P0 because "null overloading is the #1 debugging friction." The Skeptical Reviewer argues catch→log fixes are P0 while structural type changes are P1. **Resolution:** Two-phase approach. Catch→log is TSK-0101 (P0, 15 minutes). ReplanResult type is TSK-0104 (P1, 2 hours).

**Disagreement on runtime decomposition timeline.** The Data Model Architect wants decomposition to start immediately. The Skeptical Reviewer argues the God Object (~80KB) works and decomposing it prematurely could introduce bugs. **Resolution:** Planning (TSK-0107) starts Sprint 46. Extraction starts Sprint 47+.

## Acceptance Criteria

- [ ] TSK-0100 (WebSocketBridge): Inject exception into ReceiveLoopAsync → log clearly states crash + reconnect attempts. No silent-death path remains.
- [ ] TSK-0101 (catch→log): All 7 locations produce `LogWarning` output when exceptions occur. Mock logger verification in tests.
- [ ] TSK-0102 (cross-repo request): Document created at `Data/Pages/MS-Requests/` with specific file paths, line numbers, evidence, and fix recommendations.
- [ ] TSK-0103 (BuildOrigin): `BuildOrigin` sealed record with `Source` enum, nullable coordinates, and `IsExplicit`/`IsResolved` properties. `HasExplicitOrigin` requires all 3 axes. Sentinel `(0,0,0)` eliminated. 4 files changed. All existing tests pass.
- [ ] TSK-0104 (ReplanResult): `ReplanResult<IPlan>` replaces `IPlan?` return. `originalGoal` parameter is consumed. Typed goal data survives replanning.
- [ ] TSK-0105 (docs): ReplanGovernor, HtnPlanner, and phase-status memory all reflect current behavior.
- [ ] TSK-0106 (error-path tests): 8+ new NUnit tests covering all catch→null and fire-and-forget paths. `Mock<ILogger<T>>` verification.
- [ ] **Overall:** `dotnet test MemorySmith.Agent.Tests` passes with zero failures. `dotnet build` passes with zero warnings.
- [ ] **Overall:** No P0 items remain open at Sprint 46 end. P1 items are either complete or have documented progress with clear next steps.

## Open Questions

1. **WebSocketBridge ownership:** Is `WebSocketBridge` owned by `MinecraftAdapter` or `AgentBackgroundService`? Need to verify the lifecycle before restructuring.
2. **TSK-0102 handoff process:** Who in the MemorySmith base repo owns ChatServices.cs? Suggest `@TheMasonX` as initial reviewer.
3. **LLM provider abstraction:** The 3 LLM providers (Anthropic, Gemini, OpenAI) share the same catch→null pattern. Would a shared base class or middleware simplify all 3 fixes?
4. **Coverage collection:** Should Sprint 46 include coverage instrumentation? The error-path tests (TSK-0106) would benefit from knowing which catch blocks are actually covered.
5. **Event loop health check:** After fixing WebSocketBridge.ReceiveLoopAsync (TSK-0100), should we add a periodic health check ("PING" event every 30s, crash if no response in 60s)?

## General Themes & Cross-Cutting Patterns

The three audits and this council review surfaced four major cross-cutting themes:

### Theme 1: Silent Failure is the Dominant Risk Pattern (HIGHEST CONCENTRATION)
- 7 catch→null locations in 5 files
- 20+ bare catch blocks in ChatServices.cs (base repo)
- 11+ fire-and-forget calls in AgentBackgroundService
- WebSocketBridge entire receive loop fire-and-forget
- **Pattern:** The codebase is resilient-by-fallthrough. Every layer has a "don't crash" blanket catch, but the aggregate effect is opacity — when something fails, there's no breadcrumb trail.

### Theme 2: Null is Overloaded Everywhere (11+ meanings)
- Parse failure, planner failure, LLM timeout, JSON error, network error, item not found, blueprint not found, not addressed, too far away, low confidence, unavailable
- **Pattern:** The codebase uses nullable return types as a universal error channel. Every nullable method has its own private semantics that callers must infer from context.

### Theme 3: Architectural Migration Debt
- AgentBackgroundService is a documented God Object with 18+ responsibilities, 18 constructor parameters
- 3 separate BuildOrigin resolution paths in 3 layers
- HtnPlanner still has type-switch branches that docs claim were removed
- Dual state tracking (_currentGoal + facts, dual replan-throttle, dual ConcurrentDictionary)
- **Pattern:** The migration from deterministic-first to LLM-first architecture is incomplete. Old and new systems coexist with implicit priority rules.

### Theme 4: Documentation Drift is a Trust Problem
- `/api/about` reports Sprint 36 / v0.37.0 at Sprint 45+
- ReplanGovernor doc says "60s timeout" — actual is graduated delay [10,20,30,60]s
- HtnPlanner doc says "type-switch removed" — code still has them
- Phase status memory says "Sprint 41" — codebase is at Sprint 45+
- **Pattern:** Documentation is treated as a one-time artifact, not a living resource. Each sprint adds drift.

### Patterns That Were NOT Called Out in the Original Audits

This council discovered 8 missed patterns (M-1 through M-8):

| # | Pattern | Files | Severity | Found By |
|---|---|---|---|---|
| M-1 | **LLM provider catch→null bare catches** | Anthropic/Gemini/OpenAI providers | HIGH | Cross-reference LLM providers |
| M-2 | **WebSocketBridge ReceiveLoopAsync fire-and-forget** | WebSocketBridge.cs | CRITICAL | Trace event receive path |
| M-3 | **int.MinValue sentinel** | HtnPlanner + HtnTaskLibrary | MEDIUM | Search for sentinel values |
| M-4 | **State duplication** | AgentBackgroundService (multiple fields) | LOW-MEDIUM | Trace state ownership |
| M-5 | **ReplanGovernor XML doc stale** | ReplanGovernor.cs | LOW | Compare doc to code |
| M-6 | **HtnPlanner XML doc misleading** | HtnPlanner.cs | LOW | Compare doc to code |
| M-7 | **ChatServices.cs bare catches (base repo)** | MemorySmith.App/Services/ChatServices.cs | HIGH | Search for catch-all blocks |
| M-8 | **Test coverage gaps** | Tests project | MEDIUM | No tests for any catch→null path |

The common root cause across all missed patterns: **the audits focused on what they set out to find** (planner architecture, build origin, runtime design) and didn't systematically search for identical patterns in adjacent code (LLM providers, WebSocket bridge, base repo services).
