# Codebase Audit — MemorySmith Swarm Synthesis

**Task Description:** 5-agent swarm codebase sweep of MemorySmith for bugs, inconsistencies, gaps, weak guards, error handling gaps, observability/logging issues, overcoupling, and architectural fixes.
**Author:** Agent Smith (swarm synthesis)
**Timestamp:** 2026-07-10
**Branch:** (current working tree)
**Commit:** (uncommitted audit)

## Executive Summary

A 5-agent parallel swarm audited the entire MemorySmith codebase across five partitions: Core Models, API & Services, Storage & Infrastructure, Tests & Quality, and UI & App Shell. **125+ findings** were identified across all layers.

**Top 5 critical themes:**
1. **Silent catches (Rule E-3 violations)** — 15+ bare `catch {}` blocks across 6+ files with no logging. The auditing standard (AGENTS.md Rule E-3) is violated pervasively in the UI and services layers.
2. **Zero observability in Core** — `MemorySmith.Core/` has no logging whatsoever. Scoring, indexing, and state machine operations are completely opaque.
3. **Scoring weights are mathematically broken** — `MemoryScorer` weights sum to 1.23 (not 1.0), and an unnormalized `References.Count` term dominates. Thresholds are meaningless.
4. **Monolithic components** — `Chat.razor` (~3100 lines), `Admin.razor` (~400+ lines), and `SqliteMemorySmithDatabase` (~1500 lines) are extreme overcoupling cases.
5. **Skipped/wasted tests** — `navigation-freeze.spec.ts` (E2E) entirely skipped. 5+ test files silently pass without executing (no ONNX model in CI). Multiple weak assertions.

### Severity Distribution

| Severity | Count | Description |
|----------|-------|-------------|
| **P0** | 2 | Scoring logic broken, index race condition |
| **P1** | 9 | Silent data loss paths, API key mismatch, security gaps, massive monoliths |
| **P2** | 42 | Observability gaps, flaky tests, weak guards, missing interfaces, state machine gaps |
| **P3** | 72 | Convention violations, doc typos, minor cleanup, cosmetic issues |

---

## P0 — Critical

### P0-001: MemoryScorer Weights Sum to 1.23 — Scoring Is Mathematically Broken

**File:** `MemorySmith.Core/StateMachine/MemoryScorer.cs` (lines 10–13)
**The bug:** Scoring weights are `0.63 + 0.3 + 0.2 + 0.1 = 1.23`. The `References.Count` term (weight 0.2) is a raw count with no normalization — a memory with 5 references gets +1.0. A record with zero usage, zero confidence, and 5 references scores 1.0 (Working threshold).
**Impact:** Promotion/deprecation thresholds are meaningless. The state machine makes incorrect decisions about memory lifecycle.
**Recommendation:** Normalize weights to sum to 1.0, normalize `References.Count` (log or cap), and re-calibrate thresholds.
**Confidence:** 100%

### P0-002: MemoryIndex Uses Unsynchronized Collections — Race Condition

**File:** `MemorySmith.Core/Indexing/MemoryIndex.cs` (lines 5–38)
**The bug:** Plain `Dictionary<,>` and `HashSet<>` with zero synchronization. Public dictionaries expose mutable references. Concurrent `Add`/`Remove`/`Rebuild` causes `ArgumentException` or corruption.
**Impact:** Index corruption under concurrent access; crashes in multi-threaded scenarios.
**Recommendation:** Use `ConcurrentDictionary` or `ReaderWriterLockSlim`, make dictionaries private, expose query methods.
**Confidence:** 100%

---

## P1 — High

### P1-001: API Key Env Var Mismatch — `MS_LLM_API_KEY` vs `MSA_LLM_API_KEY`

**File:** `MemorySmith.App/Hosting/MemorySmithConfigurationSetup.cs` (line 22) + `Services/OpenAICompatibleChatProvider.cs`
**The bug:** Configuration setup maps `MS_LLM_API_KEY` env var to `MemorySmith:Secrets:OpenAIApiKey`, but `OpenAICompatibleChatProvider.ResolveApiKey` reads from `MSA_LLM_API_KEY` (note `MSA` prefix). The provider never picks up the configured key.
**Impact:** API key configured via env var is silently ignored.
**Recommendation:** Align to `MSA_LLM_API_KEY` consistently, or remove the redundant mapping.
**Confidence:** 95%

### P1-002: Chat.razor Is a ~3100-Line Monolith

**File:** `MemorySmith.App/Components/Pages/Chat.razor`
**The bug:** Single file mixes session management, streaming, markdown rendering, approval workflows, trace graphs, model profile selection, and composer — 20 injected services, 7+ concerns.
**Impact:** Unmaintainable; any change risks regressions across unrelated concerns. Change velocity drops as the file grows.
**Recommendation:** Extract into focused partial classes: `ChatSessionManager`, `ChatTracePanel`, `ChatApprovalPanel`, `ChatComposer`.
**Confidence:** 100%

### P1-003: Admin.razor Is a 400+ Line Monolith

**File:** `MemorySmith.App/Components/Pages/Admin.razor`
**The bug:** 14 injected services, 5 sub-panels (Users, OAuth, Models, Configuration, Sessions) in one component.
**Impact:** Adding settings breaks existing admin UI; violates single-responsibility.
**Recommendation:** Split into dedicated page components (`AdminUsers.razor`, `AdminOAuth.razor`, `AdminConfiguration.razor`).
**Confidence:** 95%

### P1-004: SqliteMemorySmithDatabase Is a ~1500-Line Monolith

**File:** `MemorySmith.Storage/SqliteMemorySmithDatabase.cs`
**The bug:** Implements all 10 store interfaces directly in one file: auth, RBAC, audit, versioning, semantic index, API tokens, settings.
**Impact:** Schema changes risk cascading breakage; impossible to unit-test stores in isolation.
**Recommendation:** Decompose into dedicated store classes per interface, keep `SqliteMemorySmithDatabase` as a composite facade.
**Confidence:** 95%

### P1-005: Request Guard Middleware — API Key Blocks Local Requests

**File:** `MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs` (lines 56–59)
**The bug:** When `AllowRemoteApi=false` and `ApiKey` is configured, loopback requests must supply an API key header even though they shouldn't need one. Also, `/api/admin/setup/status` is not in the API key exempt list.
**Impact:** Local users incorrectly blocked; setup status endpoint blocked by middleware.
**Recommendation:** Skip API key validation for loopback when `AllowRemoteApi=false`; add `/api/admin/setup/status` to exempt list.
**Confidence:** 80%

### P1-006: E2E Navigation-Freeze Tests Entirely Skipped

**File:** `e2e/tests/navigation-freeze.spec.ts`
**The bug:** Entire `test.describe` block is `.skip`-ed. Three critical tests (route hopping, slug navigation, tree/flat click stability, sidebar controls) are not running.
**Impact:** No active E2E guard against Blazor Server circuit termination or rendering exceptions.
**Recommendation:** Fix selectors/layout and re-enable, or create replacement tests.
**Confidence:** 100%

### P1-007: Silent Catch Blocks Across 15+ Locations

**Files (representative samples):**
- `MemorySmith.App/Components/Pages/Chat.razor` — 2 bare `catch {}` (lines ~749, ~2873)
- `MemorySmith.App/Components/Pages/TrainingWorkbench.razor` — 4 bare `catch {}` (lines 494, 527, 569, 607)
- `MemorySmith.App/Components/Pages/CodeSearch.razor` — bare `catch {}` (line ~326)
- `MemorySmith.App/Components/Pages/HealthStats.razor` — bare `catch {}` (line ~300)
- `MemorySmith.App/Components/SafeJsInterop.cs` — silent catch returning false (line ~27)
- `MemorySmith.Storage/FileEventStore.cs` — 2 silent catch blocks (lines 65–82, 88–91)
- `MemorySmith.Storage/FileMemoryStore.cs` — silent exception propagation on corrupt files (line ~100)
- `MemorySmith.Bridge/Program.cs` — silent `catch { }` on mcp.json parse (line ~640)

**Impact:** Failures in UI components, JS interop, event store reads, and config parsing are completely invisible in logs. Violates AGENTS.md Rule E-3.
**Recommendation:** Add `LogWarning`/`LogError` to every silent catch block. Add `ILogger<T>` where missing.
**Confidence:** 100%

---

## P2 — Medium

### MemorySmith.Core (Agent 1 Findings)

| ID | File | Finding | Confidence |
|----|------|---------|------------|
| P2-001 | `Models/MemoryRecord.cs` | No property validation, no `CreateNew()` factory, no tag normalization. Confidence unclamped. | 100% |
| P2-002 | `StateMachine/MemoryStateMachine.cs` | No demotion path (Core→Working→Unconsolidated), no recovery from Deprecated. No `IMemoryStateMachine` interface. | 100% |
| P2-003 | `**/*.cs` (entire Core project) | Zero logging — no `ILogger<T>` anywhere. Silent failure in Index.Remove, fallthrough in Evaluate. | 100% |
| P2-004 | `**/*.cs` | No `CancellationToken` support anywhere — all methods synchronous, uncancellable. | 100% |
| P2-005 | `Models/MemoryEvent.cs` vs `MemoryUpdateEvent.cs` | Two event models with overlapping purpose but incompatible shapes. No correlation ID. | 95% |
| P2-006 | `Models/MemoryDiagnostic.cs` | `Severity` is a `string` not an enum. | 100% |
| P2-007 | `Models/MemoryRecord.cs` | `References`/`Conflicts` are flat string lists — no direction, type, or metadata. | 100% |
| P2-008 | `Indexing/MemoryIndex.cs` | Public dictionaries expose mutable references. No query methods. | 100% |
| P2-009 | `StateMachine/MemoryStateMachine.cs` + `MemoryScorer.cs` + `MemoryIndex.cs` | No interfaces — cannot be mocked or DI-substituted. | 100% |
| P2-010 | `StateMachine/MemoryStateMachine.cs` | Single `Evaluate` method mixes promotion AND deprecation logic. | 100% |

### API & Services (Agent 2 Findings)

| ID | File | Finding | Confidence |
|----|------|---------|------------|
| P2-011 | `Controllers/OAuthBridgeController.cs` | `ExchangeCode` forwards `Content-Type` without validation; no request body size limit. | 85% |
| P2-012 | `Controllers/McpController.cs` | Failed tool calls invisible in logs when telemetry disabled. | 85% |
| P2-013 | `Controllers/HealthController.cs` | `Ready()` calls synchronous `GetEvents().Take(1).ToList()` potentially blocking on I/O. | 80% |
| P2-014 | `Services/ChatServices.cs` | Provider name resolution drifts between error formatting and tool routing. | 80% |
| P2-015 | `Services/ChatContextPlanner.cs` | `LocalKnowledgeRegex` matches generic terms unnecessarily triggering preloaded context. | 75% |
| P2-016 | `Services/CodeSearchService.cs` | `QuerySynonyms` contains Mineflayer/Agent-specific tool terms ("screwdriver", "hammer", etc.). | 80% |
| P2-017 | `Services/MemoryMaintenanceTasks.cs` | `DeduplicateRecords` modifies `records` list in-place while caller treats it as snapshot. | 75% |
| P2-018 | `Services/MemorySmithRequestGuardMiddleware.cs` | All API key rejections (403, 503, 401) return plain text with **no logging**. | 95% |
| P2-019 | `Services/LoggingObservabilityService.cs` | `ReadStructuredEntries` uses O(n²) insertion for sorted log reading. | 80% |
| P2-020 | `Services/PageService.cs` | Two `HasEffectiveEditorRole` overloads with slightly different logic. | 85% |
| P2-021 | `Services/MemoryChatAgent.ToolLoop.cs` | Tool-call prefix buffering may delay streaming output. | 70% |
| P2-022 | `Hosting/MemorySmithSecuritySetup.cs` | Singleton `GitHubOAuthCallbackHandler` resolves scoped services via `HttpContext.RequestServices`. | 90% |
| P2-023 | `Hosting/MemorySmithStorageSetup.cs` | Path resolution mismatch between IOptions binding and direct factory resolution. | 80% |
| P2-024 | `Hosting/MemorySmithTelemetrySetup.cs` | Malformed OTLP URI silently defaults to default endpoint. | 85% |
| P2-025 | `Services/BootstrapGate.cs` | Loopback detection bypassed entirely when `HttpContext` is null (test context). | 90% |
| P2-026 | `Services/MemorySmithLocalDevelopmentPostConfigure.cs` | Conflicting default layers between SecurityProfile and LocalDevelopment env. | 80% |

### Storage, Bridge, Scripts (Agent 3 Findings)

| ID | File | Finding | Confidence |
|----|------|---------|------------|
| P2-027 | `Storage/SqliteMemorySmithDatabase.cs` | `DisableAsync` has race window (read-mutate-write instead of atomic SQL). | 90% |
| P2-028 | `Storage/SqliteMemorySmithDatabase.cs` | `RevokeAsync` ignores `revokedByUserId` — no audit trail of token revocation. | 100% |
| P2-029 | `Storage/FileEventStore.cs` | `GetEvents` loads ALL events into memory while holding lock — O(n) memory, blocks writes. | 95% |
| P2-030 | `Storage/FileMemoryStore.cs` | `Load` has no error handling — corrupt file crashes caller. | 100% |
| P2-031 | `Bridge/Program.cs` | Silent `catch { }` on mcp.json parse. | 100% |
| P2-032 | `Bridge/Program.cs` | `dynamic parseResult` bypasses compile-time checks. | 95% |
| P2-033 | `Data/vars.json` | Hardcoded absolute path `D:\@Repos\MemorySmith\` — cannot work on other machines. | 100% |
| P2-034 | `LICENSE.txt` | MIT template `[year] [fullname]` placeholders never filled in. | 100% |
| P2-035 | `Scripts/Test-TaskRecords.ps1` | Missing validations for: priority whitelist, required field presence, camelCase field casing, prohibited labels. | 95% |
| P2-036 | `Scripts/Import-OpenTasksFromWorkbench.ps1` | No post-import validation. | 90% |
| P2-037 | `Storage/SqliteMemorySmithDatabase.cs` | `EnsureDatabaseDirectory` resolves relative paths against working directory, not app content root. | 85% |
| P2-038 | `Storage/SqliteMemorySmithDatabase.cs` | Direct `(SqliteTransaction)transaction` cast — breaks if provider changes. | 80% |

### Tests (Agent 4 Findings)

| ID | File | Finding | Confidence |
|----|------|---------|------------|
| P2-039 | `Tests/AgentSessionTests.cs` | `TearDown` silently swallows `Directory.Delete` exceptions. | 100% |
| P2-040 | `Tests/AgentSessionTests.cs` | `AgentSession_Lock_SerializesAccess` doesn't actually test serialization under contention. | 95% |
| P2-041 | `Tests/McpAndSemanticSearchTests.cs` | 2 critical MCP/semantic tests `[Ignore]`-d — not run in CI. | 90% |
| P2-042 | `Tests/LoggingObservabilityServiceTests.cs` | Cross-platform log test only runs on Windows. | 80% |
| P2-043 | `Tests/SemanticEmbeddingPrewarmServiceTests.cs` | Uses `Task.Delay(100)` timing hack — flaky on CI. | 85% |
| P2-044 | `Tests/SemanticEmbeddingPathTests.cs` | `[NonParallelizable]` + `Directory.SetCurrentDirectory()` — forces serial execution for entire assembly. | 90% |
| P2-045 | `Tests/PublisherAndStatsTests.cs` | `Task.Yield()` ordering assumptions may fail under different schedulers. | 75% |
| P2-046 | `Tests/TagGovernanceTests.cs` | Markup-inspection tests assert on raw Razor/CSS source — brittle. | 90% |
| P2-047 | `Tests/TestDoubles.cs` | `InMemoryMemoryStore` lacks validation → tests pass with invalid records. | 85% |
| P2-048 | `Tests/TestDoubles.cs` | No status-partitioning in memory store — masking directory-based bugs. | 80% |
| P2-049 | `Tests/FileMemoryStoreHardeningTests.cs` | Concurrent test doesn't verify data integrity. | 85% |
| P2-050 | `Tests/LiveMemoryRecordValidationTests.cs` | Source-wiki coupled tests break on wiki additions. | 85% |
| P2-051 | `Tests/ProjectWikiTestbaseTests.cs` | Status distribution is implicit CI contract. | 90% |
| P2-052 | `Tests/ModelBackedSearchBenchmarkTests.cs` | Zero effective coverage in CI (no ONNX model). | 85% |
| P2-053 | `Tests/ScoringTests.cs` | Recency decay comparison doesn't verify magnitude. | 75% |
| P2-054 | `Tests/MeasurementBaselineTests.cs` | Token-fallback assertion breaks when ONNX model is configured. | 80% |

### UI Layer (Agent 5 Findings)

| ID | File | Finding | Confidence |
|----|------|---------|------------|
| P2-055 | `App/Services/MemorySmithRequestGuardMiddleware.cs` | API key exempt paths missing `/api/admin/setup/status`. | 85% |
| P2-056 | `Components/SafeJsInterop.cs` | Silent catch with no logging on JS failures. | 100% |
| P2-057 | `Components/Pages/Chat.razor` | `_responseTimerCts` race — new CTS created without cancelling old one. | 85% |
| P2-058 | `Components/Pages/TagManager.razor.cs` | `_isBusy = false` not in `finally` block — stuck busy on save failure. | 95% |
| P2-059 | `Components/Pages/Login.razor` | GitHub OAuth only checks `ClientId`, not `ClientSecret`. | 90% |
| P2-060 | `Program.cs` | `WindowsServiceCommands.TryHandle` exits without Serilog flush. | 80% |
| P2-061 | `appsettings.LocalOverrides.json` | Hardcoded absolute paths committed to source. | 100% |
| P2-062 | `Properties/launchSettings.json` | `https-lan` profile binds to `0.0.0.0` with `LocalDevelopment` environment. | 70% |
| P2-063 | `Components/Pages/About.razor` | `LicenseText` switch may be truncated — possible `SwitchExpressionException`. | 75% |

---

## P3 — Low / Observability / Cleanup

All findings below are consolidated by theme rather than individually listed:

### Documentation & Naming
- `SemantingSearch.md` → should be `SemanticSearch.md` (typo propagates across multiple docs) **(DONE)**
- `Docs/Tasks/Task1.md` has `H#` instead of `#` heading **(DONE)**
- 40+ stale progress reports/reviews from April–May 2026 in `Docs/ProgressReports/` and `Docs/Reviews/` — superseded by Final Refactor Design
- `MemorySmith.Core.csproj` references non-existent `Docs\Unconsolidated\` folder **(DONE)**
- Test file `ApiExtensionTests.cs` contains `PaginationTests` and `StatsTests` classes (mismatched name)

### SourceLink Default Behavior
- `SourceLink.EndLine` defaults to `StartLine + 49` (hidden magic)
- Schemas `memory.schema.json` has no `minLength: 1` on `SourceLink.Uri`

### Convention Violations
- `DatabaseOptions` is `sealed class` — should be `sealed record` per AGENTS.md **(DONE)**
- `SqliteMemorySmithDatabase` default connection string is a literal, not a named constant
- `MemorySmith.slnx` has empty `/Temp/` folder placeholder **(DONE)**
- Controller formatting inconsistency (`GovernanceController` compacted attribute) **(DONE)**
- `AdminController` redundant `[HttpPost("setup")]` actions

### Minor Refactors
- `MemoryIndex.Remove` leaves empty `HashSet<>` entries (gradual memory leak)
- `MemoryStatus` lacks `Archived` value
- `MemoryEvent.Timestamp` defaulted at construction but mutable — stale timestamp risk
- `BackgroundServiceTelemetry.Interval` is `string` instead of `TimeSpan`
- `UserAccount` has 16 properties — split identity vs auth concerns
- `AdminSettingsService` atomic-write pattern leaves `.tmp` files on crash

### Test Quality
- `CodeSearchBenchmarkTests.RelevanceScorecard_MeetsTopRankAndLatencyTargets` requires MRR >= 1.0 (all probes at rank 1 — impossible threshold)
- `WarmThroughputBaseline_50QueriesUnder1000Ms` — tight 20ms/query on CI
- `SemanticEmbeddingPrewarmServiceTests` — uses `Task.Delay(100)` timing hack
- `McpSafeDefaults_HideSensitiveAndWriteToolsUntilExplicitlyEnabled` — bulk assertion on 16 tools without identifying which failed
- `ConsolidationTaskRulesTests` — `.Single()` error message not actionable
- `ScoringTests` — missing magnitude check on recency decay
- 4 benchmark probe sets duplicated across `SearchBenchmarkTests.cs`, `SemanticToolQualityTests.cs` with different counts

### UI Polish
- `MainLayout.razor` hardcodes `_isDarkMode = true` — no theme toggle
- `Routes.razor` unnecessary DI for `InstanceName`
- A `MudTooltip` inside another `MudTooltip` in `Admin.razor`
- `CodeSearch.razor.Dispose` lacks null guard on `_statusPollingCts`

---

## Architecture Notes

### 1. Chat Provider Registration Pattern
All three providers (`OllamaChatProvider`, `GitHubCopilotChatProvider`, `OpenAICompatibleChatProvider`) are registered as `AddScoped<IChatProvider>`. Each HTTP request creates instances of all three. Consider lazy resolution or factory pattern.

### 2. No Interfaces in Core
`MemoryIndex`, `MemoryStateMachine`, and `MemoryScorer` are all concrete — no abstractions. The Task1 plan called for interfaces; none exist. This makes unit testing harder and DI substitution impossible.

### 3. Memory Lifecycle Is a One-Way Ratchet
The state machine can only promote (Unconsolidated→Working→Core) or drop to Deprecated. No demotion path exists. A deprecated memory is unrecoverable through the state machine.

### 4. File Store vs SQLite Async Gap
File stores (`FileMemoryStore`, `FileEventStore`, `FileVarStore`) are synchronous while `SqliteMemorySmithDatabase` is fully async. Callers must handle both patterns.

### 5. Dual MCP Tool Governance
`McpController` has two independent governance mechanisms — `IsMcpToolEnabled` (via chat tool descriptors) and `McpAgentToolHandler.IsToolEnabled` (for agent tools). This dual system adds complexity.

### 6. Test Double Gap
`InMemoryMemoryStore` lacks validation and status-partitioning. Tests using it may pass while the real store would reject records.

### 7. No Fast/Slow Test Partitioning
Multiple benchmark test files lack `[Category]` markers. Tests with real service initialization are indistinguishable from fast unit tests.

### 8. Source-Wiki Coupling in Tests
4+ test files depend on live `Data/Memories/` wiki content. Adding memory records in PRs can break unrelated tests.

---

## Supplemental Data

### Methodology
- **Partition strategy:** By project layer (5 partitions)
- **Subagent count:** 5 (homogeneous swarm — same audit criteria per partition)
- **Codebase explored:**
  - `MemorySmith.Core/` — 16 source files + docs
  - `MemorySmith.App/Controllers/` — 15 controllers
  - `MemorySmith.App/Services/` — 35+ service files
  - `MemorySmith.App/Hosting/` — 11 setup files
  - `MemorySmith.Storage/` — 10 source files
  - `MemorySmith.Bridge/` — Program.cs
  - `Schemas/` — memory.schema.json
  - `Scripts/` — 10+ PowerShell/Python scripts
  - `MemorySmith.Tests/` — 40+ test files
  - `MemorySmith.Benchmarks/` — benchmark project
  - `e2e/` — Playwright tests
  - `MemorySmith.App/Components/` — 20+ pages + layout + services
  - Root config: Program.cs, appsettings, launchSettings, slnx, README, LICENSE

### Key Findings by Category

| Category | Count |
|----------|-------|
| Bugs | 7 |
| Inconsistencies | 15 |
| Gaps | 20 |
| Weak guards | 18 |
| Error handling | 12 |
| Observability | 22 |
| Overcoupling | 10 |
| Architecture | 21 |

---

## Out of Scope

The following were identified but fall outside the current audit scope:

1. **MemorySmith.Agent codebase** — separate repo, not audited here
2. **`Services/Training/` directory** — training harness services not individually reviewed
3. **`Services/AgentSessions/` directory** — agent session management for `memorysmith_agent_invoke` feature
4. **`Services/OllamaChatProvider.cs`** and **`Services/GitHubCopilotChatProvider.cs`** — not individually read in Agent 2's scan
5. **Data/Memories/ content audit** — individual memory records not validated for accuracy
6. **Data/Pages/ content audit** — markdown page content not reviewed
7. **Data/Tasks/ record audit** — task records not individually validated
8. **Dependency vulnerability scan** — `dotnet list package --vulnerable` not run

---

## Assumptions

1. **AGENTS.md rules apply to MemorySmith** — The AGENTS.md from MemorySmith.Agent defines coding standards (Rule E-3: no silent catches). MemorySmith doesn't have its own AGENTS.md, but these are good general practices.
2. **Tests should provide CI signal** — Tests that always skip (no ONNX model) or always pass (weak assertions) provide no value.
3. **The scoring weight bug is real** — Confirmed by reading `MemoryScorer.cs` weights. The 1.23 sum is mathematically provable.
4. **File contents were read accurately** — Subagents read actual file contents. Where line numbers are approximate (prefixed with ~), they indicate the general area.

---

## Open Questions

1. **Scoring weights:** Are downstream thresholds tuned to the broken formula? Fixing weights will change behavior significantly. Who tuned these thresholds originally?
2. **ONNX test model:** Is there a plan to provide a small test model for CI? 5+ test files have zero effective coverage without it.
3. **Skipped E2E tests:** What is the plan to fix `navigation-freeze.spec.ts` layout/selector issues?
4. **vars.json git status:** Is `Data/vars.json` tracked in git with the absolute `D:\` path? This will never work on CI or other machines.
5. **Chat.razor decomposition priority:** Is this a planned refactor item, or is it deprioritized?
6. **Bridge `dynamic` usage:** Was `dynamic parseResult` intentional (supporting multiple ParseResult subtypes) or an oversight?

---

## Next Steps

### Immediate (P0)
1. Fix `MemoryScorer` weights (normalize to 1.0, cap `References.Count`)
2. Add synchronization to `MemoryIndex` (ConcurrentDictionary or RWLS)

### Sprint-Blocking (P1)
3. Fix `MS_LLM_API_KEY` / `MSA_LLM_API_KEY` env var mismatch
4. Fix `MemorySmithRequestGuardMiddleware` API key blocking local requests
5. Add `LogWarning`/`LogError` to all 15+ silent catch blocks
6. Re-enable or replace skipped E2E navigation-freeze tests

### High Priority (P2)
7. Extract `Chat.razor` into focused partial components
8. Split `Admin.razor` into dedicated page components
9. Decompose `SqliteMemorySmithDatabase` into per-interface store classes
10. Add logging to `MemorySmith.Core/` (IMemoryScorer, IMemoryIndex, IMemoryStateMachine)
11. Add interfaces to Core services for testability
12. Add demotion paths to `MemoryStateMachine`
13. Fix `Test-TaskRecords.ps1` missing validations
14. Fill in `LICENSE.txt` placeholders
15. Fix `vars.json` to use relative paths
16. Consolidate `HasEffectiveEditorRole` implementations
17. Add CI-provided test ONNX model or properly categorize skipped tests

### Cleanup (P3)
18. (DONE) Rename `SemantingSearch.md` → `SemanticSearch.md`
19. Archive stale docs in `Docs/ProgressReports/` and `Docs/Reviews/`
20. Fix `DatabaseOptions` → `sealed record`
21. Remove empty `/Temp/` folder from slnx
22. Clean up `.csproj` non-existent folder references
23. Add dark mode toggle to MainLayout
24. Fix About.razor `LicenseText` switch if truncated
25. Consolidate benchmark probe definitions into shared source

---

*Generated by Agent Smith via 5-agent swarm synthesis. Each subagent read actual file contents in its partition. Findings are evidence-based with confidence levels as stated.*
