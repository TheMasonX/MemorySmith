# Codebase Audit — MemorySmith 10-Agent Swarm Synthesis

**Task Description:** 10-agent parallel swarm codebase sweep of MemorySmith for bugs, inconsistencies, gaps, weak guards, error handling gaps, observability/logging issues, overcoupling, and architectural fixes.
**Author:** Agent Smith (swarm synthesis)
**Date:** 2026-07-10
**Methodology:** 10 homogeneous subagents, each covering an independent partition of the codebase, with structured output format for mechanical merge.

## Partition Strategy

| Agent | Partition | Files/Directories | Findings |
|-------|-----------|-------------------|----------|
| 1 | Core Models | `MemorySmith.Core/Models/`, `.csproj`, `copilot-instructions.md` | 16 |
| 2 | State Machine & Indexing | `MemorySmith.Core/StateMachine/`, `Indexing/` | 16 |
| 3 | Docs & Prompts | `MemorySmith.Core/Docs/` (Plans, Prompts, Reviews, ProgressReports) | 32 |
| 4 | API Controllers | `MemorySmith.App/Controllers/` (15 controllers) | 19 |
| 5 | Chat/Agent/Search Services | `MemorySmith.App/Services/` — Chat, Agent, MCP, Search files | 30 |
| 6 | Admin/Security/Infra Services | `MemorySmith.App/Services/` — Admin, Security, Storage, Maintenance | 20 |
| 7 | Blazor UI & App Shell | `MemorySmith.App/Components/`, `Program.cs`, `appsettings*.json`, `wwwroot/` | 25 |
| 8 | Storage & Hosting | `MemorySmith.Storage/`, `MemorySmith.App/Hosting/`, `Schemas/`, `Scripts/` | 29 |
| 9 | Unit Tests | `MemorySmith.Tests/` (39 test files) | 25 |
| 10 | E2E/Benchmarks/Training | `e2e/`, `MemorySmith.Benchmarks/`, `Bridge/`, `Training/`, `docs/` | 24 |
| | **Total** | | **~236** |

## Executive Summary

A 10-agent parallel swarm conducted the most comprehensive single-pass audit of the MemorySmith codebase to date. **~236 findings** were identified across all layers, with 3 critical (P0), 14 high-severity (P1), and ~115 medium-severity (P2) issues.

**Top 5 cross-cutting themes:**

1. **Silent catch blocks (Rule E-3 violations)** — 25+ bare `catch {}` blocks across 12+ files with zero logging. Violates AGENTS.md Rule E-3 pervasively in UI, services, storage, and test layers.

2. **Monolithic god classes** — `Chat.razor` (3232 lines), `Admin.razor` (2328 lines), `SqliteMemorySmithDatabase.cs` (~1500 lines), `ChatServices.cs` (~3279 lines), `MemoryChatAgent` (~3279 lines across two partials), and `MemorySmith.Bridge/Program.cs` (~700 lines) are extreme overcoupling cases blocking maintainability.

3. **MemoryScorer is mathematically broken** — Weights sum to 1.23 (not 1.0), `References.Count` is unnormalized, and **every new record is instantly deprecated** on first triage cycle (score 0.1 < DeprecationThreshold 0.2).

4. **Zero observability in Core** — `MemorySmith.Core/` (StateMachine, Indexing, Models) has no `ILogger` dependency anywhere. Scoring, indexing, and state machine operations are completely opaque.

5. **Stale documentation debt** — `MemorySmith.Core/Docs/` contains 45+ historical files (Reviews, ProgressReports, outdated Plans) from the pre-May-2026 Worker+Dashboard era. 10:1 noise-to-signal ratio for current architecture understanding.

### Severity Distribution

| Severity | Count | % |
|----------|------:|---:|
| **P0 — Critical** | 3 | 1.3% |
| **P1 — High** | 14 | 5.9% |
| **P2 — Medium** | ~115 | 48.7% |
| **P3 — Low / Cleanup** | ~104 | 44.1% |

---

## P0 — Critical

### P0-001: MemoryScorer Instantly Deprecates New Records

**Files:** `MemorySmith.Core/StateMachine/MemoryScorer.cs` (lines 10–13), `MemoryStateMachine.cs` (Evaluate)
**The bug:** New Unconsolidated records (UsageCount=0, Confidence=0, References=[], LastUpdated=now) score **0.1**, which is **below `DeprecationThreshold` (0.2)**. When `allowDeprecation=true` (the default), `MemoryStateMachine.Evaluate` immediately transitions them to `Deprecated` on the first triage cycle.
**Root cause:** Scoring weights sum to 1.23 (0.63+0.3+0.2+0.1), and the recency term for fresh records is weighted at only 0.63 with `Math.Pow(0.995, 0)` = 1.0 — giving 0.63 from recency, but the negative `deprecationPenalty` (0.4) and zero from other terms yield 0.23 - 0.13 = 0.1.
**Impact:** Every new memory created via the API is instantly deprecated and becomes invisible/unusable.
**Confidence:** 100%
**Recommendation:** Add a guard in `Evaluate`: skip deprecation when `original == MemoryStatus.Unconsolidated`. Also normalize weights to sum to 1.0, normalize `References.Count` (log or cap), and re-calibrate thresholds.

---

### P0-002: Chat.razor Is a ~3232-Line Monolith

**File:** `MemorySmith.App/Components/Pages/Chat.razor`
**The bug:** Single file mixes Razor markup, 20+ service injections, inline JSON serialization helpers, private nested model classes, stream rendering, timer logic, session management, file attachment processing, approval workflows, trace filtering, and keyboard shortcuts. 101 `CancellationToken.None` usages across all `.razor` files.
**Impact:** Blocks any single-responsibility refactor; 100% of chat UI bugs route to one file. Every chat feature change touches this file, creating merge conflicts and regression risk across unrelated concerns.
**Confidence:** 100%
**Recommendation:** Decompose into child components: `ChatTranscript.razor`, `ChatComposer.razor`, `ChatSidebar.razor`, `ChatApprovalPanel.razor`, `ChatTracePanel.razor`. Extract model classes into `Models/Chat/`. Thread cancellation tokens through all async calls.

---

### P0-003: Admin.razor Is a ~2328-Line Monolith

**File:** `MemorySmith.App/Components/Pages/Admin.razor`
**The bug:** Houses Users, OAuth, Models, Configuration, Variables, Audit, and History tabs in one file. Duplicated model profile editor logic (Admin.razor ~lines 215-268 vs. standalone `Models.razor`). Nested `MudTooltip` inside `MudTooltip` at line 276.
**Impact:** Unmaintainable; tab-specific changes risk cascading regressions. Duplicate editor code will drift.
**Confidence:** 90%
**Recommendation:** Split into dedicated page components (`AdminUsers.razor`, `AdminOAuth.razor`, `AdminConfiguration.razor`, etc.) and extract shared model profile editor into `ModelProfileEditor.razor`.

---

## P1 — High

### P1-001: Data-Protection Keys Path Uses Relative CWD Path — Catastrophic Key Loss on Deployment

**File:** `MemorySmith.App/Hosting/MemorySmithSecuritySetup.cs` (lines 93-94)
**The bug:** Data-protection keys path defaults to `Path.Combine("..", "Data", "Keys")` which is relative to `Directory.GetCurrentDirectory()`, not `AppContext.BaseDirectory`. In published/deployed scenarios, CWD may differ from the app location.
**Impact:** All users logged out, all encrypted data (auth cookies, sessions) unreadable after deployment to a different working directory.
**Confidence:** 95%
**Recommendation:** Use `Path.Combine(AppContext.BaseDirectory, "..", "Data", "Keys")` or make the default an absolute path.

---

### P1-002: API Key Env Var Mismatch — `MS_LLM_API_KEY` vs `MSA_LLM_API_KEY`

**Files:** `MemorySmith.App/Hosting/MemorySmithConfigurationSetup.cs` (line 22), `Services/OpenAICompatibleChatProvider.cs`
**The bug:** Configuration setup maps `MS_LLM_API_KEY` env var to `MemorySmith:Secrets:OpenAIApiKey`, but `OpenAICompatibleChatProvider.ResolveApiKey` reads from `MSA_LLM_API_KEY`. The provider never picks up the configured key.
**Impact:** API key configured via env var is silently ignored; OpenAI-compatible provider silently fails.
**Confidence:** 95%
**Recommendation:** Align to `MSA_LLM_API_KEY` consistently, or remove the redundant mapping from ConfigurationSetup.

---

### P1-003: SQLiteMemorySmithDatabase Is a ~1500-Line God Class

**File:** `MemorySmith.Storage/SqliteMemorySmithDatabase.cs`
**The bug:** Single class implements 10 interfaces directly in one file — auth, RBAC, audit, versioning, semantic index, API tokens, settings. `_initializeLock` (`SemaphoreSlim`) is never disposed. `OpenSqliteConnectionAsync` may leak connections on throw. `DisableAsync` has no transaction for read-modify-write. `RevokeAsync` never records `revokedByUserId`.
**Impact:** Maintainability sink; any schema change touches avalanche of code; impossible to unit-test stores in isolation.
**Confidence:** 100%
**Recommendation:** Decompose into dedicated store classes per interface (`SqliteUserStore`, `SqliteRoleStore`, `SqliteAuditStore`, etc.) with a shared connection factory. Keep `SqliteMemorySmithDatabase` as a composite facade.

---

### P1-004: Silent Catches Pervasively Violate Rule E-3 (25+ Instances)

**Files:** Multiple — `ChatServices.cs` (10+), `Chat.razor` (3+), `LoggingObservabilityService.cs`, `MemoryGovernanceServices.cs`, `FileEventStore.cs`, `RequestMetadata.cs`, `MemorySmithLocalDevelopmentPostConfigure.cs`, `OperationalDiagnosticsService.cs`, `FileMemoryStore.cs`, plus test tear-downs.
**The bug:** Bare `catch {}` or `catch (Exception)` blocks with no logging, no diagnostics recording, no counters. Violates AGENTS.md Rule E-3 pervasively.
**Impact:** Failures in argument parsing, file cleanup, temp directory operations, policy loading, event reading, and diagnostic collection are completely invisible.
**Confidence:** 95%
**Recommendation:** At minimum log `LogWarning` in every catch block. Use `catch (JsonException)` instead of bare `catch (Exception)` where possible.

---

### P1-005: MemoryStateMachine Has No Demotion or Recovery Paths

**File:** `MemorySmith.Core/StateMachine/MemoryStateMachine.cs`
**The bug:** No `Core → Working` demotion, no `Working → Unconsolidated` regression, and no `Deprecated → Working` recovery. Records that degrade in score stay at their highest achieved status forever. Deprecated records can never be revived.
**Impact:** Stale high-status records accumulate; quality degradation is undetectable. Once deprecated, records are permanently lost to the state machine.
**Confidence:** 100%
**Recommendation:** Add demotion rules (Core→Working when score falls below CoreThreshold) and recovery (Deprecated→Working when score rises above WorkingThreshold).

---

### P1-006: Scoring in MemoryMaintenanceTasks Duplicates (and Drifts From) MemoryStateMachine

**Files:** `MemorySmith.Core/StateMachine/MemoryStateMachine.cs`, `MemorySmith.App/Services/MemoryMaintenanceTasks.cs`
**The bug:** `DeprecateObsoleteRecords` duplicates deprecation logic inline rather than calling `_stateMachine.Evaluate()`. `PromoteStableRecords` uses completely different criteria (30-day age + ≥2 references + confidence ≥ 0.7) to promote Working→Core, bypassing the state machine entirely.
**Impact:** Two competing promotion/deprecation pathways that will inevitably drift. No single authority for memory lifecycle rules.
**Confidence:** 100%
**Recommendation:** Refactor maintenance tasks to use `MemoryStateMachine.Evaluate` as the single authority for all status transitions.

---

### P1-007: MemoryIndex Uses Unsynchronized Collections — Race Condition

**File:** `MemorySmith.Core/Indexing/MemoryIndex.cs`
**The bug:** `ById`, `ByTag`, and `ByReference` are plain `Dictionary<,>` and `HashSet<>` with zero synchronization. Public dictionaries expose mutable references. Registered as singleton in DI.
**Impact:** Index corruption under concurrent access; `ArgumentException` ("key already added") under load.
**Confidence:** 95%
**Recommendation:** Use `ConcurrentDictionary`, make dictionaries private, expose query methods only. Add transactional rebuild with rollback on failure.

---

### P1-008: MemoryScorer Is Static — No Interface, No DI, No Testability

**File:** `MemorySmith.Core/StateMachine/MemoryScorer.cs`
**The bug:** Static class with no interface. Calls `DateTime.UtcNow` directly. Cannot be mocked, substituted, or tested deterministically.
**Impact:** Impossible to unit test state machine logic; prevents alternative scoring strategies. Already tracked as TSK-3051.
**Confidence:** 100%
**Recommendation:** Extract `IMemoryScorer`, convert to instance class, inject `ITimeProvider` for clock abstraction.

---

### P1-009: Docs/ Contains 45+ Historical/Stale Files — 10:1 Noise-to-Signal

**Files:** `MemorySmith.Core/Docs/Plans/` (InitialPlan, DashboardPlan, DashboardPlanV2), `Reviews/` (20+ files), `ProgressReports/` (25+ files)
**The bug:** Nearly all files describe the pre-May-2026 Worker+Dashboard architecture. `InitialPlan.md` references PostgreSQL, gRPC, and non-existent projects. Reviews from April 2026 cover the old two-process architecture. No archival markers or supersession banners.
**Impact:** New contributors waste time reading stale docs. P0 items in old plans appear to be active blockers.
**Confidence:** 96%
**Recommendation:** Archive pre-2026-05-15 files into a `historical/` subdirectory with a README. Add supersession banners to remaining files.

---

### P1-010: ChatController — Unguarded `_providers[0]` Access

**File:** `MemorySmith.App/Controllers/ChatController.cs` (lines 131-132)
**The bug:** `ResolveProvider` accesses `_providers[0]` with no guard. If no `IChatProvider` implementations are registered, this throws `ArgumentOutOfRangeException`.
**Impact:** Crash on any chat config or send request when no providers registered.
**Confidence:** 95%
**Recommendation:** Add `_providers.Count > 0` guard; log warning and return 503.

---

### P1-011: MCP Controller — Unwrapped Tool Execution Exceptions

**File:** `MemorySmith.App/Controllers/McpController.cs` (lines 185-213)
**The bug:** `DelegateToCatalogAsync` calls `tool.Execute(...)` without a catch block. Unhandled exceptions propagate as raw ASP.NET 500 HTML, breaking JSON-RPC protocol expectations.
**Impact:** Broken MCP clients on tool execution errors.
**Confidence:** 90%
**Recommendation:** Wrap `tool.Execute` in try/catch, log exception, return properly structured JSON-RPC error response.

---

### P1-012: MemoryChangePublisher Handler Failure Cascades to Caller

**Files:** `MemorySmith.App/Services/IMemoryChangePublisher.cs` (line 37-39), `MemoryApplicationService.cs`
**The bug:** `PublishAsync` uses `Task.WhenAll` which throws on first fault; `AuditAndPublishAsync` doesn't catch the exception. A failing subscriber crashes the entire memory create/update/delete operation.
**Impact:** One misbehaving event handler can kill memory writes.
**Confidence:** 90%
**Recommendation:** Wrap each handler invocation in try-catch that logs the error and continues.

---

### P1-013: OpenAI-Compatible Provider SSE Streaming — Tool Call Chunks Lost

**File:** `MemorySmith.App/Services/OpenAICompatibleChatProvider.cs` (lines 195-229)
**The bug:** `ReadOpenAIStreamToolCalls` only captures single-delta-chunk tool calls. Standard OpenAI SSE sends tool call arguments incrementally across multiple chunks — incremental arguments are silently lost.
**Impact:** Streaming tool calls from DeepSeek/OpenAI that send tool arguments incrementally are silently truncated.
**Confidence:** 90%
**Recommendation:** Implement accumulator state for incremental tool call chunks (index-keyed dictionary across stream iterations).

---

### P1-014: ChatContextPlanner Skips Preload When Interceptor Matches

**File:** `MemorySmith.App/Services/ChatContextPlanner.cs` (lines 57-70)
**The bug:** When `intentInterceptor.TryMatch` returns a match, the planner returns `None(...)` — skipping all context preloading. If the interceptor's tool then returns zero results, the LLM has no context at all.
**Impact:** Contradicts the purpose of context preloading — preload would have provided useful context but was skipped.
**Confidence:** 90%
**Recommendation:** Still preload context when the interceptor matches, or merge interceptor results with preloaded context.

---

## P2 — Medium (Consolidated by Theme)

### Observability & Logging (30+ findings)

| Theme | Count | Key Locations |
|-------|------:|---------------|
| Silent catch blocks (no logging) | 25+ | ChatServices.cs (10+), Chat.razor (3), FileEventStore.cs (2), MemoryGovernanceServices.cs (2), RequestMetadata.cs, OperationalDiagnosticsService.cs, MemorySmithLocalDevelopmentPostConfigure.cs, AgentSessionTests TearDown (3), FileMemoryStore.cs |
| No `ILogger` in Core | All | `MemorySmith.Core/StateMachine/`, `Indexing/`, `Models/` — zero logging |
| Chat.razor only 2 `Logger.Log` calls in 3232 lines | 1 | Chat.razor — model load failures, session load errors, proposal apply errors all only in Snackbar |
| Missing or wrong-level logging | 5+ | SM-005 (Evaluate no logging), SM-011 (MemoryIndex zero logging), S6-10/11 (Diagnostics silent catches), UI-003 (Chat.razor diagnostics gaps) |
| Telemetry gaps | 3 | C-16 (MCP telemetry off = no log fallback), S6-20 (ActivityTraceFlags not set), BNCH-004 (pragma-suppressed events) |

### Error Handling (15+ findings)

| Theme | Count | Key Locations |
|-------|------:|---------------|
| Silent fire-and-forget | 3 | C-02 (watchdog orphaned), UI-008 (draft persist), UI-020 (LoadKnownMemoryIdsAsync) |
| CancellationToken.None pervasively | 101 occurrences across 12 .razor files | All Blazor pages |
| Missing CancellationToken on async endpoints | 5+ | AuthController.Logout, AuthController.ExternalChallenge, SourceLinksController.Open, HealthController.Ready eventStore call, FileMemoryStore interface |
| Gateway-specific gaps | 3 | C-02 (watchdog CancellationToken.None), C-06 (GitHubCopilot model validation), C-23 (OpenAI 200-with-error silently accepted) |
| Connection/resource leaks | 3 | S8-02 (SemaphoreSlim not disposed), S8-03 (SqliteConnection leak on throw), UI-019 (PeriodicTimer not disposed) |

### Overcoupling & Architecture (20+ findings)

| Theme | Count | Key Locations |
|-------|------:|---------------|
| God classes / Monoliths | 6 | Chat.razor (3232 lines), Admin.razor (2328 lines), SqliteMemorySmithDatabase (~1500 lines), ChatServices.cs (~3279 lines), MemoryChatAgent (~3279 lines across partials), MemorySmith.Bridge/Program.cs (~700 lines) |
| Missing interfaces (no DI, no testability) | 5 | MemoryScorer (static), MemoryStateMachine (no interface), MemoryIndex (no interface), MemoryScorer (no ITimeProvider), ChatToolCatalog.BuildTools() (~700-line method) |
| Code duplication | 8 | Regex patterns across ChatContextPlanner + MemoryChatAgent, FindRepositoryRoot() (4 files), CopyDirectory() (2 files), StaticOptionsMonitor<T> (3 files), FormatContextPack (2 locations), DuplicateMemoryStore (multiple files), scoring formula in CodeSearchService (3 locations), filter logic across Admin tabs |
| Dual-write / competing pathways | 3 | Scoring in StateMachine vs MaintenanceTasks, SignalR write surface (ABS inline + DashboardPublisherImpl), file-backed vs SQLite stores |
| Pattern inconsistency | 5 | MemoryEvent (mutable class vs sealed record), VersionHistoryEntry vs VersionCreateRequest (duplicated fields), file stores (sync) vs SQLite (async), ID generation (GUID vs string), endpoint security patterns (inline vs RequireAuthorization) |

### Weak Guards (15+ findings)

| Theme | Count | Key Locations |
|-------|------:|---------------|
| Missing null checks | 5+ | MemoryIndex.Add (null record), ChatController._providers[0], MemoryChatAgent nullable constructor deps, Actor() fallthrough, Page tree builder null path |
| Missing input validation | 4 | GovernanceController no Ok() wrapping, DiagnosticsController hours<0, OAuthBridgeController ReadBodyAsync no size limit, SourceLink.EndLine no code enforcement |
| Schema/type safety gaps | 3 | Free-form status strings (SecurityModels.cs), oneOf int+string in memory.schema.json, training data `"tag"` vs `"tags"` |

### Tests & Quality (25 findings)

| Theme | Count | Key Locations |
|-------|------:|---------------|
| Skipped/disabled tests | 4 | McpAndSemanticSearchTests (2 Ignore), navigation-freeze.spec.ts (entire describe block skip), CudaEmbeddingBatchBenchmarkTests (2 Ignore) |
| Flaky/time-dependent tests | 5 | CodeSearchBenchmarkTests (tight latency thresholds), SearchBenchmarkTests (latency probes), TSK-019 (concurrent test timing), ConsolidationTaskRulesTests (DateTime.UtcNow) |
| Weak assertions / insufficient coverage | 4 | MemoryRecordTests (only 2 tests), ScoringTests (no integration with Evaluate), TagGovernanceTests (incomplete rollback verification), no UI component tests |
| Duplicate test infrastructure | 6 | FindRepositoryRoot() (4 files), CopyDirectory() (2 files), StaticOptionsMonitor<T> (3 files) |
| Silent catch in TearDown | 3 | AgentSessionTests, ChatToolCatalogAndInterceptTests, ChatToolLoopParityTests |

### Cross-Cutting Themes

1. **Silent failures are the #1 systemic risk** — 25+ catch blocks across 12+ files with no logging. This is a project-wide culture issue that requires explicit enforcement (a Roslyn analyzer for `catch` blocks without logging).

2. **The Core layer has zero observability** — `MemorySmith.Core/` has no `ILogger` anywhere. This is the foundational library — every bug in scoring, indexing, or state machine logic is invisible in production.

3. **Two competing memory lifecycle implementations** — The state machine (`MemoryStateMachine.Evaluate`) and maintenance tasks (`DeprecateObsoleteRecords`, `PromoteStableRecords`) implement the same business rules independently. This will inevitably drift.

4. **Documentation rot is accelerating** — The `Docs/` directory has 45+ historical files and no archival process. As new features are added, stale docs accumulate at ~5:1 ratio to active docs.

5. **Chat.razor and ChatServices.cs are the highest-priority refactoring targets** — Together they account for ~6500 lines of unmaintainable code with 30+ findings in the chat/agent partition alone.

### Conflicts Between Agents

| Conflict | Agent A | Agent B | Resolution |
|----------|---------|---------|------------|
| MemoryScorer severity | Agent 1 (P2) | Agent 2 (P1 → P0) | **P0** — Agent 2 found the instant-deprecation bug (SM-002), which elevates this from a math issue to a data-loss bug |
| References.Count normalization | Agent 1 (P2) | Agent 2 (P1) | **P1** — combined with SM-002 this is a critical scoring chain |
| `net10.0` vs `net9.0` target | Agent 1 (P2) | Multiple | **P2** — Agent 1 identified this; confirmed as intentional tracking of latest .NET |
| Chat.razor severity | Agent 7 (P0) | Prior audit (P1) | **P0** — Agent 7 confirmed 3232 lines, 20+ services, 101 CancellationToken.None |

### Out of Scope (Identified but Not Audited)

| Issue | Area | Notes |
|-------|------|-------|
| `MemorySmith.Agent` repo | Separate repository | Not in scope for this base-repo audit |
| Training/ and AgentSessions/ service subdirectories | `MemorySmith.App/Services/` | Partially read; deeper audit needed |
| `FilePageService` delete error paths | App Services | Not covered by any partition |
| `AuditLogService` error paths | App Services | Not covered by any partition |
| `MemoryMetadata` full audit | Core Models | Sampled but not exhaustive |
| `MemorySmith.Core/` remaining Indexing files | Core Indexing | Some files partially read |

### Methodology

- **Partition strategy:** 10 homogeneous subagents, each responsible for one independent directory/layer partition
- **Output format:** Structured markdown table per agent (ID, File, Line(s), Category, Description, Severity, Impact, Confidence %, Recommendation)
- **Deduplication:** Manual review of overlap between partitions (e.g., MemoryScorer findings appeared in both Agent 1 and Agent 2)
- **Conflict resolution:** Disagreements between agents are documented with evidence
- **Limitations:** Agent 7 (Blazor UI) file was partially truncated on retrieval; key findings still captured from available output

### Open Questions

1. **Is `net10.0` in MemorySmith.Core.csproj intentional tracking of latest .NET, and does MemorySmith.Agent need to match?**
2. **Does `ShouldPreloadContext` actually fire independently of `ChatContextPlanner.Plan` — or is one dead code?**
3. **What valid `Status` values does `SemanticIndexMetadata` and `IndexBuildRecord` actually use without enum constraints?**
4. **Are the file-backed stores (`FileMemoryStore`, `FileEventStore`) legacy from the pre-refactor era, or actively maintained?**
5. **Does `Data/Benchmarks/code-search-scorecard.json` exist — or would `CodeSearchBenchmarks.LoadScorecardProbes()` throw?**

### Next Steps

1. **Immediate (P0):** Fix MemoryScorer instant-deprecation bug (SM-002) — P0 data loss
2. **Immediate (P0):** Add `Unconsolidated` guard in `MemoryStateMachine.Evaluate`
3. **This sprint:** Create MCP task records for P0/P1 findings without existing coverage
4. **This sprint:** Propose council review for audit findings
5. **Sprint 61:** Chat.razor decomposition (child components + model extraction)
6. **Sprint 61:** MemoryIndex thread safety fix + interface extraction
7. **Sprint 61:** Add Roslyn analyzer for `catch` blocks without logging
8. **Backlog:** SqliteMemorySmithDatabase decomposition
9. **Backlog:** Admin.razor decomposition
10. **Backlog:** Docs archival project (archive pre-2026-05-15 files)
