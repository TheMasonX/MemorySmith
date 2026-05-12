# MemorySmith Refactor Plan (Evidence-Based)

Date: 2026-05-07
Author: GitHub Copilot (GPT-5.3-Codex)
Scope: Streamline and simplify architecture, runtime behavior, and maintainability across Core, Storage, Worker, Dashboard, and Docs.

## 1) Objectives

- Reduce code complexity in UI and API layers while preserving behavior.
- Remove duplication and drift in contracts/configuration.
- Improve operational reliability (health/readiness, testability, concurrency safety).
- Align documentation with current implementation to reduce planning noise.

## 2) Evidence Map

| ID | Evidence-based claim | Evidence | Confidence |
|---|---|---|---|
| E1 | Write APIs trigger full stats recomputation repeatedly. | `MemorySmith.Worker/Controllers/MemoriesController.cs` (`Create`, `Update`, `Delete`, `IncrementUsage` each call `BroadcastStatsAsync` -> `StatsSnapshotFactory.Build(_store.LoadAll())`) | 0.98 |
| E2 | Stats are also recomputed periodically regardless of writes. | `MemorySmith.Worker/Services/StatsBroadcastService.cs` (default every 10s, `StatsSnapshotFactory.Build(_store.LoadAll())`) | 0.98 |
| E3 | Service interval/name strings are duplicated between backend and dashboard. | `MemorySmith.Worker/Services/TriageService.cs`, `ConsolidationService.cs`, `IndexingService.cs`; `MemorySmith.Dashboard/Components/Pages/HealthStats.razor` (`ServiceDefinitions`) | 0.95 |
| E4 | Search is linear scan over all records; index is rebuilt but not used by API search path. | `MemorySmith.Worker/Controllers/MemoriesController.cs` (`Search` uses `_store.LoadAll().Where(...)`); `MemorySmith.Core/Indexing/MemoryIndex.cs`; `MemorySmith.Worker/Services/IndexingService.cs` | 0.95 |
| E5 | File-backed store has no explicit synchronization around read/write paths under concurrent hosted services + API writes. | `MemorySmith.Storage/FileMemoryStore.cs` (no lock primitives in `Save`, `LoadAll`, `Delete`) and multi-writer architecture in `MemorySmith.Worker/Program.cs` | 0.88 |
| E6 | Two large Razor pages contain mixed concerns (view, orchestration, networking state, mapping). | `MemorySmith.Dashboard/Components/Pages/MemoryViewer.razor`; `MemorySmith.Dashboard/Components/Pages/HealthStats.razor` | 0.97 |
| E7 | API request DTO duplication exists for search request shape. | `MemorySmith.Worker/Controllers/MemoriesController.cs` (`SearchRequest`); `MemorySmith.Dashboard/Services/MemoryApiClient.cs` (`SearchRequest`) | 0.99 |
| E8 | Liveness/readiness endpoints are mapped identically and do not represent distinct semantics today. | `MemorySmith.Worker/Program.cs` (`MapHealthChecks` for both routes with same defaults) | 0.93 |
| E9 | Test runs can fail due to binary locks from running worker process. | `dotnet test` output: `MemorySmith.Worker.exe` lock on `MemorySmith.Core.dll`/`MemorySmith.Storage.dll` | 0.99 |
| E10 | Test framework standard is NUnit. | `MemorySmith.Tests/MemorySmith.Tests.csproj` (`NUnit`, `NUnit3TestAdapter`) and project instruction | 1.00 |
| E11 | A large body of review docs appears stale versus current code (for example, older claims of missing event persistence/consolidation). | `MemorySmith.Core/Docs/Reviews/*` compared with current `TriageService` + `ConsolidationService` implementations | 0.85 |
| E12 | Status option binding style in the memory dialog is ambiguous and should be made explicit for maintainability. | `MemorySmith.Dashboard/Components/MemoryDetailDialog.razor` uses `<MudSelectItem Value="s">@s</MudSelectItem>` under `T="MemoryStatus"`; prefer explicit expression style | 0.78 |
| E13 | Response caching middleware is configured but no response cache policies/attributes are applied to endpoints. | `MemorySmith.Worker/Program.cs` contains `AddResponseCaching`/`UseResponseCaching`; no `ResponseCache` usage found in worker controllers | 0.91 |
| E14 | Rate limiter middleware is configured but no explicit endpoint/global limiter policy wiring is present. | `MemorySmith.Worker/Program.cs` contains `AddRateLimiter`/`UseRateLimiter`; no `EnableRateLimiting` attributes or `GlobalLimiter` usage found | 0.90 |
| E15 | Pagination/search parameters are not bounded, which can increase load under extreme inputs. | `MemorySmith.Worker/Controllers/MemoriesController.cs` uses unbounded `pageSize` and `limit` | 0.93 |
| E16 | Storage layer silently swallows corrupt-file parse errors, reducing operability and debuggability. | `MemorySmith.Storage/FileMemoryStore.cs` (`catch { /* skip corrupt files */ }`) | 0.96 |
| E18 | UI component writes validation exceptions to debug output instead of structured app logging. | `MemorySmith.Dashboard/Components/MemoryDetailDialog.razor` uses `System.Diagnostics.Debug.WriteLine(...)` in catch blocks | 0.94 |
| E19 | Production dashboard timeout default is significantly lower than development and may cause avoidable transient failures. | `MemorySmith.Dashboard/appsettings.json` sets `WorkerApiTimeoutSeconds` to `2`; development uses `10` | 0.83 |
| E20 | Historical documentation claims of zero warnings and stubbed services conflict with current code/build state. | Multiple `Docs/ProgressReports` and `Docs/Reviews` entries claim `0 warnings` or consolidation TODO; current build shows warnings and consolidation implemented | 0.93 |
| E21 | Mutating API endpoints do not enforce required business fields server-side (for example content/title non-empty). | `MemorySmith.Worker/Controllers/MemoriesController.cs` `Create`/`Update` accept `MemoryRecord` with no explicit validation; `MemorySmith.Core/Models/MemoryRecord.cs` has no validation attributes | 0.92 |
| E22 | Some review documentation now contains factual inaccuracies (not just age), such as claiming stats push is missing. | `MemorySmith.Core/Docs/Reviews/DashboardPlan_Review.md` states `ReceiveStats` is never called; code calls it in `MemorySmith.Worker/Controllers/MemoriesController.cs` and `MemorySmith.Worker/Services/StatsBroadcastService.cs` | 0.96 |
| E23 | Dashboard health UI currently reflects liveness only and does not use readiness route, limiting operational signal quality. | `MemorySmith.Dashboard/Services/MemoryApiClient.cs` `GetHealthAsync` calls `api/health/live`; worker also exposes `api/health/ready` in `MemorySmith.Worker/Program.cs` | 0.94 |

## 3) Refactor Strategy (Phased)

### Phase 0: Guardrails and baselining (1-2 days)

1. Add deterministic local workflow scripts:
- `stop-worker` before tests, `start-worker` after tests.
- Prevent test failures caused by lock contention (E9).

2. Add architecture decision note for current storage/search scope:
- Explicitly state keyword search + file store is current production baseline (E4).
- Avoid future drift in docs and generated code.

3. Add smoke tests for the current API contracts:
- `api/memories`, `api/memories/search`, `api/stats`, `api/stats/services`, `api/health/live`, `api/health/ready`.
- Framework: NUnit only (E10).

Success criteria:
- Tests run reliably with no file lock failures.
- Documented baseline accepted by team.

Confidence: 0.92

### Phase 1: Consolidate contracts and configuration (2-3 days)

1. Extract shared contract types into Core models:
- Move/replace duplicated `SearchRequest` with a single model in Core (E7).
- Keep wire contract unchanged.

2. Centralize background service descriptors:
- Introduce one canonical list/config object for service name + interval labels.
- Worker uses it for telemetry keys; dashboard consumes same source (E3).

3. Formalize Worker endpoint settings:
- Single options class for API base URL, hub URL, polling/broadcast intervals.
- Remove scattered default literals across pages/services.

Success criteria:
- No duplicated request DTOs for search.
- One source of truth for service identifiers/interval labels.

Confidence: 0.90

### Phase 2: Reduce repeated full scans and simplify runtime data flow (4-6 days)

1. Introduce `IStatsProvider` with cache + invalidation:
- Compute snapshot once per change batch rather than on every caller path.
- `MemoriesController`, `StatsController`, `StatsBroadcastService` share provider (E1, E2).

2. Add lightweight write-through eventing:
- On create/update/delete/usage increment, invalidate or update cached stats incrementally.
- Keep full recompute fallback for safety.

3. Rationalize polling + push behavior:
- Keep SignalR push for updates, reduce redundant `RefreshAsync` calls where possible.
- Health page currently refreshes on events and periodic polling; simplify to avoid over-fetching (E6).

Success criteria:
- Fewer `_store.LoadAll()` calls per minute under normal operation.
- Stable dashboard freshness with lower backend work.

Confidence: 0.84

### Phase 3: Hardening file-storage concurrency and API consistency (3-5 days)

1. Add synchronization strategy in file memory store:
- Use `ReaderWriterLockSlim` (or equivalent) for save/delete/load operations (E5).
- Preserve current atomic write temp-file pattern.

2. Add error/result envelopes for mutating endpoints:
- Keep status codes, add consistent problem details/log correlation.
- Simplify dashboard client error handling paths.

3. Add readiness semantics:
- Keep liveness simple.
- Readiness should include minimum checks: data path writable, event log writable, optional last successful service run freshness threshold (E8).

Success criteria:
- No race-related corruption in stress tests.
- Readiness endpoint meaningfully differs from liveness.

Confidence: 0.79

### Phase 4: Dashboard decomposition for maintainability (4-7 days)

1. Split large pages into focused components:
- `MemoryViewer.razor`: extract filter bar, table, pagination bar, action handlers.
- `HealthStats.razor`: extract service status table, diagnostics panel, live event panel (E6).

2. Move orchestration logic to page-level controller classes/services:
- Keep Razor primarily declarative UI.
- Convert repetitive async/try/catch/snackbar flows into reusable helper methods.

3. Introduce thin view-model records for UI projection:
- Avoid ad hoc mapping blocks in page code.

Success criteria:
- Significantly smaller page files.
- Reduced cognitive load for changes and review.

Confidence: 0.87

### Phase 5: Search/index alignment decision (2-4 days, optional but high leverage)

Option A: Keep simple search intentionally.
- De-scope index from query path, keep only for analytics/internal features.

Option B: Integrate index-assisted filtering.
- Use `MemoryIndex` to preselect candidates by tag/reference before text contains checks.

Both options require documenting tradeoffs and selecting one explicitly (E4).

Success criteria:
- Clear, documented search architecture.
- No ambiguous dual-path implementation.

Confidence: 0.72

### Phase 6: Documentation cleanup and governance (2-3 days)

1. Add `CurrentState.md` as source of truth.
2. Mark outdated review files as archived or superseded.
3. Add doc freshness metadata (last validated date + validator).

Success criteria:
- New contributors can identify authoritative docs in under 5 minutes.

Confidence: 0.82

## 4) Prioritized Backlog (Top 10)

1. Create shared `SearchRequest` contract in Core and remove duplicate definitions. (0.99)
2. Centralize service names/interval labels in one shared descriptor source. (0.95)
3. Introduce `IStatsProvider` to unify stats production and reduce repeated scans. (0.90)
4. Add read/write locking strategy to `FileMemoryStore`. (0.88)
5. Separate liveness and readiness checks with explicit criteria. (0.93)
6. Refactor `HealthStats` networking loop to reduce redundant refreshes. (0.86)
7. Decompose `MemoryViewer` into 3-4 focused components. (0.90)
8. Add NUnit integration tests for `/api/stats/services` and SignalR stats events. (0.84)
9. Add test run profile that stops worker process first to avoid binary locks. (0.99)
10. Create docs authority index and archive stale review snapshots. (0.82)

## 5) Refined Assumptions

1. File storage + Worker API/SignalR remains the near-term architecture boundary for refactoring work.
2. Existing API routes and payload shapes are compatibility constraints for Phase 0-3 work.
3. Dependency minimization remains a priority unless a new dependency directly removes a verified operational risk.
4. NUnit is the enforced test framework standard for all new test work.
5. net10.0 preview SDK/tooling remains acceptable for this repository in the short term.
6. Operational correctness is prioritized over throughput optimization in this refactor pass.

Assumption confidence: 0.88

## 6) Open Questions Answered

1. Search strategy: adopt index-assisted keyword retrieval as the default now, while keeping lexical behavior and API contract intact.
  - Decision: proceed with Phase 5 Option B.
  - Rationale: reduces full-scan pressure without semantic-stack scope growth.
  - Confidence: 0.86

2. Stats freshness SLO: set dashboard freshness target at <=10 seconds for normal operations.
  - Decision: keep 10-second broadcast/poll defaults; permit tuning to 5-30 seconds by config.
  - Rationale: aligns with existing defaults and minimizes behavior change risk.
  - Confidence: 0.90

3. Readiness semantics: fail readiness on critical service drift only.
  - Decision: treat `TriageService` and `IndexingService` as critical; do not fail readiness solely for `ConsolidationService` lag.
  - Rationale: triage/indexing are higher-frequency and directly impact core behavior.
  - Confidence: 0.81

4. Stats consistency model: eventual consistency is acceptable; do not require immediate consistency post-write.
  - Decision: allow brief lag bounded by SLO and invalidation strategy.
  - Rationale: avoids expensive synchronous recompute on every mutation path.
  - Confidence: 0.85

5. Dev process for worker lifecycle: adopt managed lifecycle during build/test.
  - Decision: add scripts/tasks to stop worker before build/test and restart afterward when needed.
  - Rationale: verified file-lock failures when worker remains running.
  - Confidence: 0.99

6. Reviews documentation governance: classify dated review snapshots as historical by default and maintain one active source-of-truth document.
  - Decision: create `CurrentState.md`; archive/supersede stale review snapshots with a clear marker.
  - Rationale: reduces contributor confusion and contradictory guidance.
  - Confidence: 0.88

7. Build warning posture: treat current compile baseline as clean and prevent stale warning claims from persisting in active docs.
  - Decision: enforce date-stamped evidence for warning claims and remove stale warning findings from active plans.
  - Rationale: current `dotnet build MemorySmith.slnx -v minimal` succeeds with no code warnings.
  - Confidence: 0.97

8. Real-time stats push status: confirm whether backend-to-dashboard stats push is missing or implemented.
  - Decision: treat stats push as implemented; remove contrary claims from active review guidance.
  - Rationale: `ReceiveStats` is invoked on write paths and periodic broadcast service.
  - Confidence: 0.98

9. Dashboard health signal: should operator-facing health show liveness only or readiness semantics.
  - Decision: use readiness (or combined live+ready display) for dashboard system status indicator.
  - Rationale: liveness-only masks dependency/service-readiness problems and reduces operational usefulness.
  - Confidence: 0.91

## 7) Additional Issues Found In Follow-up Review

1. Status option binding style in memory dialog is ambiguous and should be made explicit for maintainability.
2. Response caching is configured but appears effectively inactive due to missing endpoint policies.
3. Rate limiter policy exists but appears not explicitly enforced per-endpoint or globally.
4. `pageSize` and search `limit` are unbounded and should be clamped.
5. Corrupt file handling in storage is silent and should produce observable diagnostics.
6. Mutating endpoints should enforce server-side validation for required fields (not only UI validation).
7. Dashboard dialog uses debug-only exception output instead of structured logging.
8. Dashboard production timeout default (`2s`) is likely too aggressive relative to development and observed API calls.
9. Documentation drift is material: older reports claim clean warnings and unfinished services contrary to current state.
10. Documentation drift also includes factual inaccuracies (for example, claims that stats push is not called).
11. Dashboard health card currently reports liveness only; readiness signal is not surfaced.

## 8) Risk Register

- Risk: Concurrency fixes in file storage can introduce deadlocks if lock hierarchy is unclear.
  - Mitigation: single lock owner strategy + lock duration minimization + stress tests.
- Risk: Stats caching can serve stale data.
  - Mitigation: strict invalidation on write paths + periodic full recompute fallback.
- Risk: Dashboard decomposition can create regressions in UX behavior.
  - Mitigation: component-level NUnit+bUnit coverage and page-level smoke tests.

## 9) Suggested Execution Order

1. Phase 0
2. Phase 1
3. Phase 2
4. Phase 3
5. Phase 4
6. Phase 6
7. Phase 5 (if product decides to invest now)

## 10) Confidence Summary

- Confidence in evidence accuracy: 0.93
- Confidence this plan reduces complexity measurably: 0.86
- Confidence in timeline (assuming one engineer, focused execution): 0.74
- Confidence that no behavior changes are required for Phases 0-2: 0.89
