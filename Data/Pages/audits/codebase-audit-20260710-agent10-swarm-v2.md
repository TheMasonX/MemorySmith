# Codebase Audit — 2026-07-10 (10-Agent Swarm, Synthesis Round 2)

**Task Description:** Comprehensive codebase audit of the MemorySmith repo — all layers.
**Author:** Agent Smith (swarm synthesis)
**Timestamp:** 2026-07-10
**Methodology:** 10 parallel subagents, partition-by-layer, homogeneous swarm (Branch A)

## Executive Summary

The 10-agent swarm examined the entire MemorySmith codebase (~60+ service files, 15 controllers, 40+ test files, 15+ hosting/setup files, storage layer, UI components, scripts, data, docs, and infrastructure). **94 findings** were identified across 8 categories.

**Severity distribution:**

| Severity | Count | Key Areas |
|----------|-------|-----------|
| **P0** | 2 | Path traversal vulnerability, NuGet non-existent version |
| **P1** | 22 | Unsynchronized concurrent state, god classes, missing auth guards, silent failures, stale index, dead CI time |
| **P2** | 48 | Observable logging gaps, weak guards, overcupling, data integrity, fragile tests |
| **P3** | 22 | Doc typos, cosmetic inconsistencies, dead config, minor DRY violations |

**Top cross-cutting themes:**
1. **Silent catch blocks / swallowed exceptions** — at least 12 locations across all layers
2. **Missing interfaces for testability** — MemoryIndex, MemoryScorer, MemoryStateMachine all have no interface
3. **Stale documentation** — Plans/Reviews/ProgressReports reference removed Worker/Dashboard architectures
4. **God classes / overcupling** — SqliteMemorySmithDatabase (10 interfaces), MemoryApplicationService (1500+ lines), McpController (9 deps)
5. **No thread safety in shared state** — MemoryIndex plain Dictionary, no synchronization
6. **Config/env path fragility** — Relative path assumptions, `LocalDevelopment` vs `Development` mismatch
7. **Missing audit trails** — Login failures, source link opens, MCP tool executions leave no trace

---

## P0 — Critical

### P0-001: Directory Traversal in FileMemoryStore.SanitizeId
**File:** `MemorySmith.Storage/FileMemoryStore.cs` (line ~76)
**The bug:** `SanitizeId()` regex `[/\\:?*]` does not filter parent-directory `..` sequences. A memory record with `Id = "../../etc/somefile"` would resolve outside the base path via `Path.Combine`, enabling arbitrary file read/write/delete.
**Impact:** Arbitrary file access within the data root via crafted memory record IDs.
**Recommendation:** Add `..` to the regex pattern or validate that `Path.GetFullPath(resolved)` starts with the base path.
**Confidence:** 95%

### P0-002: BenchmarkDotNet Version Does Not Exist on NuGet
**File:** `MemorySmith.Benchmarks/MemorySmith.Benchmarks.csproj` (line 8)
**The bug:** `BenchmarkDotNet` version `0.15.8` is specified but no such stable release exists on NuGet (latest stable is `0.14.0`). This is either a pre-release alpha or a typo.
**Impact:** Benchmarks fail to restore or produce unreliable results.
**Confidence:** 95%

---

## P1 — High

### P1-001: MemoryIndex No Thread Safety
**File:** `MemorySmith.Core/Indexing/MemoryIndex.cs` (lines 7-9)
**The bug:** Uses plain `Dictionary<,>` and `HashSet<>` with no locking. Concurrent `Add`/`Remove`/`Rebuild` from multiple callers can corrupt internal state.
**Impact:** Index corruption under concurrent load.
**Confidence:** 95%

### P1-002: MemoryIndex Maintained But Never Used for Queries
**File:** `MemorySmith.Core/Indexing/MemoryIndex.cs` (all)
**The bug:** `MemoryIndex` is fully wired and correctly maintained on CRUD, but every search method calls `_store.LoadAll()` — a full linear scan. The index is dead weight.
**Impact:** Wasted CPU; all searches are O(N) despite having an index.
**Confidence:** 100%

### P1-003: MemoryScorer Weights Sum to 1.23
**File:** `MemorySmith.Core/StateMachine/MemoryScorer.cs` (lines 10-13)
**The bug:** Weights sum to 1.23 (0.63+0.3+0.2+0.1), not 1.0. Score is an unbounded linear combination. `UsageCount=1000` contributes ~1.89 from usage alone, easily exceeding `CoreThreshold=2.0`.
**Impact:** Inflated scores cause premature promotion to Core.
**Confidence:** 100%

### P1-004: Two Divergent State Promotion Paths
**File:** `MemorySmith.Core/StateMachine/MemoryStateMachine.cs` + `MemoryMaintenanceTasks.cs`
**The bug:** `MemoryStateMachine.Evaluate()` uses scoring thresholds, but `PromoteStableRecords` uses different criteria (30 days + 2 refs + 0.7 confidence). Inconsistent promotions.
**Impact:** Records promoted by one path may not be promoted by the other.
**Confidence:** 100%

### P1-005: Index Not Updated During Consolidation
**File:** `MemorySmith.Core/Indexing/MemoryIndex.cs` + `MemoryMaintenanceTasks.cs`
**The bug:** `DeduplicateRecords`, `PromoteStableRecords`, `DeprecateObsoleteRecords` modify records and call `_store.Save()` but never call `_index.Remove()`/`_index.Add()`.
**Impact:** Searches miss deduped/promoted/deprecated records.
**Confidence:** 100%

### P1-006: SqliteMemorySmithDatabase God Class (10 Interfaces)
**File:** `MemorySmith.Storage/SqliteMemorySmithDatabase.cs` (all)
**The bug:** Single ~1500-line class implements 10 interfaces.
**Impact:** Impossible to test, maintain, or evolve independently.
**Confidence:** 100%

### P1-007: Hardcoded 'ADMIN' SQL String Literal
**File:** `MemorySmith.Storage/SqliteMemorySmithDatabase.cs` (lines 433-445)
**The bug:** `HasAnyAdminAsync` hardcodes `'ADMIN'` as SQL string literal instead of using `MemorySmithRoles.Admin` constant.
**Impact:** Logic error after role rename.
**Confidence:** 100%

### P1-008: AdminController Duplicate POST Routes
**File:** `MemorySmith.App/Controllers/AdminController.cs` (lines 51, 60)
**The bug:** Two `[HttpPost("setup")]` actions — POST without Content-Type header triggers ambiguous match → 500.
**Impact:** First-time setup can fail.
**Confidence:** 90%

### P1-009: AuthController Login Failure Has No Audit Event
**File:** `MemorySmith.App/Controllers/AuthController.cs` (lines 53-54)
**The bug:** `Login` returns `Unauthorized` without recording audit event.
**Impact:** Brute-force attacks leave no forensic trace.
**Confidence:** 100%

### P1-010: Setup Endpoint Lacks Rate Limiting
**File:** `MemorySmith.App/Controllers/AdminController.cs` (lines 38-45)
**The bug:** Setup endpoint has `[AllowAnonymous]` but no `[EnableRateLimiting]`.
**Impact:** Unauthenticated setup brute-force.
**Confidence:** 85%

### P1-011: ChatController Empty Provider List Crash
**File:** `MemorySmith.App/Controllers/ChatController.cs` (line 104)
**The bug:** `ResolveProvider` falls back to `_providers[0]` with no guard → 500 on empty list.
**Impact:** Chat endpoint returns 500 when no providers configured.
**Confidence:** 100%

### P1-012: McpController God Class (9 Constructor Dependencies)
**File:** `MemorySmith.App/Controllers/McpController.cs` (line ~210)
**The bug:** Single controller owns JSON-RPC protocol, tool dispatch, auth, telemetry, lifecycle.
**Impact:** Difficult to test; changes risk unrelated MCP concerns.
**Confidence:** 100%

### P1-013: OAuthBridge Missing CSRF/State Validation
**File:** `MemorySmith.App/Controllers/OAuthBridgeController.cs` (lines 39-60)
**The bug:** `ExchangeCode` proxies POST body to GitHub with no state validation or CSRF protection.
**Impact:** CSRF-based account linking attacks possible.
**Confidence:** 85%

### P1-014: SourceLinksController No Rate Limiting or Audit
**File:** `MemorySmith.App/Controllers/SourceLinksController.cs` (lines 19-21)
**The bug:** `Open` endpoint executes server-side shell operations with no rate limiting, audit, or confirmation.
**Impact:** DoS vector; no audit trail.
**Confidence:** 95%

### P1-015: launchSettings Uses Wrong Environment Name
**File:** `MemorySmith.App/Properties/launchSettings.json` (all profiles)
**The bug:** `ASPNETCORE_ENVIRONMENT` is `"LocalDevelopment"` but settings file is `appsettings.Development.json`.
**Impact:** Development config overrides silently ignored.
**Confidence:** 100%

### P1-016: Hardcoded Absolute Paths in appsettings.LocalOverrides.json
**File:** `MemorySmith.App/appsettings.LocalOverrides.json` (lines 6, 8)
**The bug:** `D:\temp\memorysmith-training\...` hardcoded absolute paths committed to repo.
**Impact:** Training harness fails on any other machine.
**Confidence:** 100%

### P1-017: E2E Navigation Freeze Tests Entirely Skipped
**File:** `e2e/tests/navigation-freeze.spec.ts` (line 4)
**The bug:** Entire test suite `test.describe.skip`. CI job runs zero tests, wastes ~4 min per run.
**Impact:** No navigation regression coverage; wasted CI time.
**Confidence:** 100%

### P1-018: MemoryChangePublisher Subscriber Failures Block Writes
**File:** `MemorySmith.App/Services/MemoryChangePublisher.cs` (lines 43-49)
**The bug:** `PublishAsync` does `Task.WhenAll` — any subscriber exception propagates up and blocks memory mutations.
**Impact:** Subscriber failure denies memory writes.
**Confidence:** 90%

### P1-019: Training Harness Fire-and-Forget With No Error Tracking
**File:** `MemorySmith.App/Services/Training/` (line ~226)
**The bug:** `_ = Task.Run(() => RunHarnessAsync(...), CancellationToken.None)` — returned task discarded.
**Impact:** Training harness failures completely invisible.
**Confidence:** 95%

### P1-020: Race Condition in IncrementUsageAsync
**File:** `MemorySmith.App/Services/MemoryApplicationService.cs` (lines 678-693)
**The bug:** `IncrementUsageAsync` read-mutate-write with no synchronization.
**Impact:** Lost usage-tracking data under concurrent access.
**Confidence:** 95%

### P1-021: StaticOptionsMonitor<T> Duplicated Across Test Files
**File:** Multiple test files
**The bug:** `StaticOptionsMonitor<T>` defined identically in >=3 test files.
**Impact:** DRY violation.
**Confidence:** 100%

### P1-022: Silent Catch in Transcript Cleanup
**File:** `MemorySmith.App/Services/ChatTranscriptWriter.cs` (lines 105-109)
**The bug:** `DeleteExpiredTranscripts` has empty `catch { }`.
**Impact:** File-delete errors invisible to operators.
**Confidence:** 100%

---

## P2 — Medium

### Observability & Logging Gaps

| ID | File | Line(s) | Description | Conf |
|----|------|---------|-------------|:----:|
| P2-001 | `FileEventStore.cs` | 53-59, 65-68 | Silent catch on corrupt log files | 100% |
| P2-002 | `FileMemoryStore.cs` | 176-177 | Corrupt files silently skipped (null diagnostics) | 100% |
| P2-003 | All Storage files | — | No ILogger anywhere in Storage project | 100% |
| P2-004 | `SemanticEmbeddingSearchService.cs` | 378-382 | Empty catch blocks | 100% |
| P2-005 | `ChatServices.cs` | 1064-1088 | Watchdog uses CancellationToken.None | 85% |
| P2-006 | `SecurityServices.cs` | 277 | CancellationToken.None on DB query | 80% |
| P2-007 | `AutoValidateAntiforgeryTokenFilter.cs` | 47 | CSRF failure with zero logging | 100% |
| P2-008 | `MemorySmithTelemetrySetup.cs` | 96 | NRE on startup if prefixes unconfigured | 95% |
| P2-009 | `AgentSessionCleanupService.cs` | 74-82 | Redundant SaveAsync before DeleteAsync | 100% |
| P2-010 | `ChatServices.cs` | 1092-1120 | Empty catch on temp file cleanup | 85% |
| P2-011 | `HealthController.cs` | 33-37 | Exception caught but not logged | 100% |
| P2-012 | `McpController.cs` | 230-236 | No logging fallback when telemetry disabled | 95% |
| P2-013 | `DiagnosticsController.cs` | 31-33 | Sync call in async action | 70% |
| P2-014 | `MemoriesController.cs` | 92-96 | Headers set before async search | 90% |

### Weak Guards & Input Validation

| ID | File | Line(s) | Description | Conf |
|----|------|---------|-------------|:----:|
| P2-015 | `AdminController.cs` | 64, 92 | pageSize not clamped | 100% |
| P2-016 | `ChatController.cs` | 66-77 | Feedback attributed to "anonymous" | 90% |
| P2-017 | `FileMemoryStore.cs` | 130-131 | Save() mutates record.Id as side effect | 95% |
| P2-018 | `FileEventStore.cs` | 39-41 | AppendEvent mutates Timestamp | 90% |
| P2-019 | `SqliteMemorySmithDatabase.cs` | 596, 621 | SQL string interpolation | 95% |
| P2-020 | `SqliteMemorySmithDatabase.cs` | 580-583 | Dead parameter | 100% |
| P2-021 | `SqliteMemorySmithDatabase.cs` | 634-636 | SemaphoreSlim never disposed | 85% |
| P2-022 | `DiagnosticsController.cs` | 38-48 | hours/limit not clamped | 95% |
| P2-023 | `MemoriesController.cs` | 29-31 | pageSize not clamped | 100% |
| P2-024 | `OAuthBridgeController.cs` | 30-33 | No validation before proxying | 95% |
| P2-025 | `TasksController.cs` | 212-220 | Actor() returns "authenticated-user" | 90% |
| P2-026 | `TasksController.cs` | 37 | List limit not clamped | 100% |
| P2-027 | `MemorySmithSecuritySetup.cs` | 48-49 | OAuth secret empty — no warning | 95% |
| P2-028 | `MemorySmithSerilogSetup.cs` | 68 | EventLog sink fails if source missing | 90% |
| P2-029 | `MemorySmithStorageSetup.cs` | 35+ | Fragile relative paths | 70% |
| P2-030 | `TaskDomainService.cs` | 690-710 | HardDelete path traversal risk | 70% |
| P2-031 | `MemoryChatAgent.ToolLoop.cs` | 266 | No per-tool timeout | 85% |

### Data Integrity & Indexing

| ID | File | Line(s) | Description | Conf |
|----|------|---------|-------------|:----:|
| P2-032 | `MemoryStateMachine.cs` | 17-28 | No demotion path | 100% |
| P2-033 | `MemoryStateMachine.cs` | 17-28 | No re-promotion from Deprecated | 100% |
| P2-034 | `MemoryStateMachine.cs` | 17-28 | Compile-time threshold | 100% |
| P2-035 | `MemoryMaintenanceTasks.cs` | 85-112 | Silent title-based dedup merges | 70% |
| P2-036 | `SqliteMemorySmithDatabase.cs` | 642+ | SchemaMigrations DDL duplicated | 100% |
| P2-037 | `SqliteMemorySmithDatabase.cs` | 419-428 | DisableAsync no early exit | 95% |
| P2-038 | `MemorySmith.Storage.csproj` | 5-7 | Redundant SQLitePCLRaw packages | 95% |
| P2-039 | Models/ | — | No JsonPropertyName attributes | 85% |

### Tests & Quality

| ID | File | Line(s) | Description | Conf |
|----|------|---------|-------------|:----:|
| P2-040 | `AppApiContractTests.cs` | 23-25 | Static Serilog.CloseAndFlush() in TearDown | 85% |
| P2-041 | `McpAndSemanticSearchTests.cs` | 26, 53 | Two [Ignore]d tests (dead code) | 100% |
| P2-042 | `SemanticToolQualityTests.cs` | 16-46 | Fragile rank assertions | 90% |
| P2-043 | `SearchBenchmarkTests.cs` | 15-22 | Non-discriminating query | 95% |
| P2-044 | `CodeSearchBenchmarkTests.cs` | 98 | MRR >= 1.0 too tight | 95% |
| P2-045 | `CodeSearchServiceTests.cs` | 63 | Missing [Category("Integration")] | 95% |
| P2-046 | `SemanticEmbeddingPrewarmServiceTests.cs` | 47 | Task.Delay timing hack | 90% |
| P2-047 | `FileMemoryStoreHardeningTests.cs` | 68 | Task.Yield() not true concurrency | 80% |
| P2-048 | `TestDoubles.cs` | 6-23 | InMemoryMemoryStore silently passes new members | 85% |

### Doc Staleness

| ID | File | Description | Conf |
|----|------|-------------|:----:|
| P2-049 | `vars.json` | Absolute Windows path broken on CI/Linux | 100% |
| P2-050 | Various `Docs/Plans/` | 4+ plans reference removed Worker/Dashboard | 100% |
| P2-051 | `maintenance-agent-task.md` | Non-existent schema fields in prompt | 85% |
| P2-052 | `wiki-chat-agent.modelfile` | SYSTEM prompt drift from canonical | 90% |
| P2-053 | `Docs/Reviews/` (22 files) | Pre-refactor, removed architectures | 95% |
| P2-054 | Various sprint plans | Stale task count references | 90% |

---

## P3 — Low

- `MemorySmith.Core.csproj` references non-existent `Docs\Memories\` directory
- `docs/Doxyfile`: `OPTIMIZE_OUTPUT_JAVA = YES` for C# project
- `MemoryIndex.Remove()` leaves orphaned empty HashSet entries
- `MemoryScorer.cs`: no `Math.Max(0,...)` guard on UsageCount
- `MemoryStateMachine.Evaluate()` silent fallthrough for unhandled statuses
- `MemoryEvent.Action` is plain string, not constrained vocabulary
- `MemoryDiagnostic.Severity` is string, not enum
- `StatsSnapshot` hardcodes property-per-status
- `MemoryRecord` has no `CreatedAt` field
- `SecurityModels.cs`: near-duplicate version classes

---

## Architecture Notes

1. **Index-As-Dead-Weight**: MemoryIndex fully wired, never read for query acceleration
2. **God Class Triad**: SqliteMemorySmithDatabase (10 interfaces), MemoryApplicationService (1500+ lines), McpController (9 deps)
3. **Two SQLite Patterns**: AgentSessionStore uses IMemorySmithDatabase; ChatFeedbackStore opens own connection
4. **Silent Catch Proliferation**: 12+ empty `catch { }` blocks
5. **Stale Docs**: Plans/Reviews/ProgressReports reference removed architectures
6. **Config Path Fragility**: Multiple `Path.Combine("..","Data",...)` resolve to working directory

---

## Supplemental Data

**Methodology:** 10 parallel subagents, by-layer partition
**Total findings:** 94 (2 P0, 22 P1, 48 P2, 22 P3)
**Coverage:** ~150+ files

| Category | Count |
|----------|-------|
| Bugs / Logic errors | 18 |
| Inconsistencies | 14 |
| Gaps | 10 |
| Weak guards | 22 |
| Error handling (silent catches) | 12 |
| Observability / Logging | 12 |
| Overcoupling | 6 |
| Stale documentation | 8 |

---

## Next Steps

1. **Immediate (P0):** Fix path traversal + BenchmarkDotNet version
2. **High (P1):** Create MCP tasks for top 15 findings
3. **Medium (P2):** Batch logging/guards/testability fixes
4. **Council Review:** 5-chair council to validate findings and prioritize
5. **Quick Fixes (P3):** Remove dead config, archive stale plans
6. **Validation:** Run `Test-TaskRecords.ps1` after MCP task creation
