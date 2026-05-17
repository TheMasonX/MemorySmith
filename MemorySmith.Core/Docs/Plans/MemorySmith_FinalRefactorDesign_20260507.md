# MemorySmith Final Refactor Design

Date: 2026-05-07  
Status: Final source of truth for the simplification refactor  
Owner: MemorySmith project  
Confidence: 0.91 overall

2026-05-12 update: user direction added a small MCP/search tooling slice. The active implementation now includes a local semantic-search baseline and an HTTP JSON-RPC MCP endpoint over the project wiki.

2026-05-17 update: user direction added the focused `SemantingSearch.md` plan. The active implementation now includes an optional local ONNX Runtime embedding ranker with token-scoring fallback. Durable vector indexes, PostgreSQL, queues, OpenTelemetry, Redis, and gRPC remain out of this simplification refactor unless a later plan explicitly accepts that complexity.

## 1. Purpose

This document replaces the earlier refactor and dashboard planning notes with one current design for making MemorySmith robust, elegant, and shippable without carrying architecture that the product does not need yet.

This design supersedes these planning/review artifacts for refactor direction:

- `MemorySmith.Core/Docs/Plans/ProjectRefactorPlan_20260507.md`
- `MemorySmith.Core/Docs/Plans/DashboardPlan.md`
- `MemorySmith.Core/Docs/Plans/DashboardPlanV2.md`
- `MemorySmith.Core/Docs/Reviews/DashboardPlan_Review.md`
- older progress reports and reviews under `MemorySmith.Core/Docs/ProgressReports/` and `MemorySmith.Core/Docs/Reviews/`

Those files remain useful as historical evidence only. When implementing this refactor, treat this document as the active plan.

## 2. Final Answer

MemorySmith should become a single deployable ASP.NET Core web application with:

- Blazor Server UI in the same host as the API.
- Existing REST routes preserved for external automation.
- A small MCP endpoint for local agent/tool access to the project wiki.
- File-backed storage retained and hardened.
- A small application service layer used by both UI and controllers.
- One maintenance scheduler for triage, consolidation, and index rebuilds.
- No custom Dashboard-to-Worker HTTP client, custom Dashboard SignalR hub, CORS policy, worker URL config, or stats broadcast service.
- No PostgreSQL, durable vector index, queue, OpenTelemetry, Redis, or gRPC in this refactor. The optional ONNX embedding ranker is a local search enhancement and falls back to token scoring when model assets are unavailable.

Final solution shape:

```text
MemorySmith.Core/        Domain models, scoring, state machine, in-memory index types
MemorySmith.Storage/     File-backed memory/event stores and storage abstractions
MemorySmith.App/         Single web host: Blazor UI, REST API, maintenance scheduler, app services
MemorySmith.Tests/       NUnit tests for domain, storage, app services, API integration
Schemas/                 JSON schema for memory records
Data/                    Runtime memory data plus this project's own memory wiki and copied testbase source
```

During migration, `MemorySmith.App` may be created from the current Worker and Dashboard projects. At the end of the refactor, `MemorySmith.Worker` and `MemorySmith.Dashboard` are removed from the solution or left only as temporary compatibility branches until the new app is green.

## 3. Why This Is The Right Simplification

The current codebase is small enough that two web processes create more cost than value. The split introduces CORS, hub URLs, API base URLs, timeout drift, two launch profiles, a custom SignalR bridge, duplicated request models, and file-lock friction during build/test. None of the current product behavior requires independent scaling or deployment of Worker and Dashboard.

Keeping the REST API is still important. Agents, scripts, and future integrations should continue using `/api/memories`, `/api/stats`, and `/api/health/*`. The simplification is about collapsing process boundaries, not removing automation access.

## 4. Evidence From Current Code

| ID | Evidence | Source | Confidence |
|---|---|---|---|
| E1 | The solution currently has separate Worker and Dashboard web projects. | `MemorySmith.slnx` | 1.00 |
| E2 | Worker hosts storage, event store, index, controllers, SignalR, health, CORS, rate limiting, response caching, and four hosted services. | `MemorySmith.Worker/Program.cs` | 0.99 |
| E3 | Dashboard is another Blazor Server host that configures a typed `HttpClient` pointed at `WorkerApiBaseUrl`. | `MemorySmith.Dashboard/Program.cs` | 0.99 |
| E4 | Dashboard config carries `WorkerApiBaseUrl`, `WorkerHubUrl`, `WorkerApiTimeoutSeconds`, and `StatsPollingSeconds`. | `MemorySmith.Dashboard/appsettings*.json` | 0.99 |
| E5 | Worker config carries `DashboardOrigin` and `StatsBroadcastSeconds`, which exist because Dashboard is cross-origin from Worker. | `MemorySmith.Worker/appsettings*.json` | 0.98 |
| E6 | Memory CRUD/search API exists and should be preserved. | `MemorySmith.Worker/Controllers/MemoriesController.cs` | 0.99 |
| E7 | Dashboard UI pages call `MemoryApiClient` even though they are server-side components. | `MemorySmith.Dashboard/Components/Pages/*.razor`, `MemorySmith.Dashboard/Services/MemoryApiClient.cs` | 0.97 |
| E8 | The custom SignalR hub only bridges Worker events into Dashboard. | `MemorySmith.Worker/Hubs/DashboardHub.cs`, `MemorySmith.Dashboard/Components/Pages/HealthStats.razor` | 0.97 |
| E9 | Stats are recomputed by API, write paths, and periodic broadcast. | `StatsController.cs`, `MemoriesController.cs`, `StatsBroadcastService.cs` | 0.96 |
| E10 | Triage event persistence is implemented. | `TriageService.cs`, `IEventStore.cs`, `FileEventStore.cs` | 0.99 |
| E11 | Consolidation is implemented, but simple and title-based. | `ConsolidationService.cs`, `ConsolidationServiceTests.cs` | 0.96 |
| E12 | File storage uses sanitized IDs and atomic temp-file writes, but still lacks a single synchronization boundary and skips corrupt records silently. | `FileMemoryStore.cs` | 0.94 |
| E13 | Search is keyword/substring search and does not use `MemoryIndex`. | `MemoriesController.cs`, `MemoryIndex.cs`, `IndexingService.cs` | 0.98 |
| E14 | Liveness and readiness endpoints are mapped the same today. | `MemorySmith.Worker/Program.cs` | 0.97 |
| E15 | Rate limiting and response caching middleware are registered, but the plan needs explicit endpoint/global behavior or removal. | `MemorySmith.Worker/Program.cs` | 0.92 |
| E16 | Historical docs still describe unimplemented or stale targets such as PostgreSQL, embeddings, queues, xUnit, and missing stats push. | `MemorySmith.Core/Docs/Plans/*`, `MemorySmith.Core/Docs/Reviews/*` | 0.96 |

## 5. Product Scope Locked For This Refactor

The refactor ships a focused local/single-operator memory app with an API for trusted automation. It is not a public multi-tenant SaaS service.

In scope:

- Browse, search, create, edit, delete, and increment usage on memories.
- Show lifecycle status, confidence, tags, references, usage, and last update.
- Run lifecycle maintenance in the background.
- Persist lifecycle events to an append-only log.
- Show health, readiness, stats, and maintenance telemetry.
- Preserve REST compatibility for scripts and agents.
- Expose local MCP tools for project-wiki record lookup, keyword search, and local semantic search.
- Publish/deploy as one app process.

Out of scope for this refactor:

- Embedding-backed/vector search.
- External semantic-search provider abstractions.
- PostgreSQL or EF Core.
- gRPC.
- Redis, queues, OpenTelemetry, Prometheus, or distributed tracing.
- Multi-user auth/authorization, tenancy, roles, or account management.
- Separate Worker and Dashboard deployment.
- A custom real-time hub for the dashboard.

## 6. Target Architecture

### 6.1 Project Responsibilities

`MemorySmith.Core`

- Owns domain models: `MemoryRecord`, `MemoryStatus`, metadata, stats snapshots, lifecycle events.
- Owns pure domain logic: scoring, state transition rules, in-memory index structure.
- Does not know about files, HTTP, Razor, logging, or hosting.

`MemorySmith.Storage`

- Owns `IMemoryStore` and `IEventStore`.
- Owns `FileMemoryStore` and `FileEventStore`.
- Provides file-backed persistence with atomic writes, ID safety, synchronization, and observable corrupt-file handling.
- Does not know about controllers, Razor, or maintenance scheduling.

`MemorySmith.App`

- The only deployable app.
- Hosts Blazor Server UI.
- Hosts REST controllers under existing routes.
- Registers storage, application services, health checks, maintenance scheduler, and UI state.
- Owns app-level validation, API key/local-only policy, error responses, and configuration.

`MemorySmith.Tests`

- Uses NUnit.
- Tests domain, storage, app services, controller integration, and maintenance behavior.

### 6.2 Runtime Flow

```text
Blazor components
    -> MemoryApplicationService
        -> IMemoryStore / IEventStore / MemoryIndex / StatsCache / MemoryChangePublisher

REST controllers
    -> MemoryApplicationService
        -> same logic as UI

MemoryMaintenanceService
    -> MemoryApplicationService and maintenance task handlers
        -> storage, events, telemetry, index, stats cache
```

There is one path for memory behavior. UI and API differ only at the presentation/transport edge.

## 7. Application Service Layer

Create one cohesive app service, named `MemoryApplicationService` or `MemoryCatalogService`. Use one name consistently.

Responsibilities:

- Validate create/update/search/list inputs.
- Clamp page and limit values.
- Normalize tags and references.
- Save memory records.
- Append audit events for user-visible mutations and lifecycle transitions.
- Maintain or invalidate stats cache.
- Update `MemoryIndex` incrementally where practical.
- Publish in-process change notifications for UI refresh.
- Return stable DTOs for UI and API.

Suggested public surface:

```csharp
Task<PagedResult<MemoryMetadata>> GetMemoriesAsync(MemoryListQuery query, CancellationToken cancellationToken);
Task<IReadOnlyList<MemoryRecord>> SearchAsync(MemorySearchQuery query, CancellationToken cancellationToken);
Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken);
Task<MemoryRecord> CreateAsync(MemoryRecord record, CancellationToken cancellationToken);
Task<MemoryRecord?> UpdateAsync(string id, MemoryRecord record, CancellationToken cancellationToken);
Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
Task<MemoryRecord?> IncrementUsageAsync(string id, CancellationToken cancellationToken);
Task<StatsSnapshot> GetStatsAsync(CancellationToken cancellationToken);
Task<IReadOnlyList<BackgroundServiceTelemetry>> GetTelemetryAsync(CancellationToken cancellationToken);
```

The controller should become thin transport code. The Blazor components should stop using `HttpClient` and call this service directly.

## 8. API Contract

Preserve current routes:

| Route | Keep | Notes |
|---|---:|---|
| `GET /api/memories` | Yes | Add bounds: `page >= 1`, `1 <= pageSize <= 100`. |
| `GET /api/memories/{id}` | Yes | Return 404 for missing or invalid IDs. |
| `POST /api/memories` | Yes | Validate content and server-controlled fields. |
| `PUT /api/memories/{id}` | Yes | Validate body and route ID. |
| `DELETE /api/memories/{id}` | Yes | Keep 204 on success. |
| `POST /api/memories/search` | Yes | Keep keyword semantics, clamp `limit` to 1-100. |
| `POST /api/memories/search/semantic` | Yes | Optional ONNX embedding cosine ranking when configured; otherwise token/tag/title/reference/alias scoring with match explanations. |
| `POST /api/memories/{id}/usage` | Yes | Keep current success response unless tests prove clients expect 204. |
| `GET /api/stats` | Yes | Serve cached or freshly built stats through app service. |
| `GET /api/stats/services` | Yes | Serve maintenance telemetry. |
| `GET /api/health/live` | Yes | Process is alive. |
| `GET /api/health/ready` | Yes | Storage, event log, and critical maintenance readiness. |

Move `SearchRequest`/query DTOs out of controller-local classes into the app contract layer so UI, controller, and tests share one shape.

## 9. UI Design Target

The UI remains Blazor Server with MudBlazor, but it becomes a first-class part of the app host rather than a separate client of the API.

Changes:

- Delete `MemoryApiClient` after components use `MemoryApplicationService` directly.
- Delete `WorkerApiBaseUrl`, `WorkerHubUrl`, and `WorkerApiTimeoutSeconds` config.
- Delete the custom Dashboard hub and SignalR client dependency.
- Keep `DashboardState` or replace it with smaller scoped page state.
- Split large pages into focused components only where it reduces real complexity:
  - `MemoryViewer.razor`: toolbar/filter, table, pagination, dialog orchestration.
  - `HealthStats.razor`: health summary, stats summary, maintenance telemetry, recent events.
- Remove client-side tag filtering after search; filtering belongs in the app service.
- Make search pagination explicit. Either support paged search or hide pagination while in search mode with clear state handling.
- Replace debug-only exception output in dialogs with structured logging or user-visible validation results.

The first screen should remain the usable dashboard, not a marketing page.

## 10. In-Process Notifications

Use an in-process notification publisher instead of the custom Worker-to-Dashboard SignalR hub.

Suggested shape:

```csharp
public interface IMemoryChangePublisher
{
    event Func<MemoryUpdateEvent, Task>? MemoryChanged;
    event Func<StatsSnapshot, Task>? StatsChanged;
    Task PublishMemoryChangedAsync(MemoryUpdateEvent update);
    Task PublishStatsChangedAsync(StatsSnapshot stats);
}
```

Blazor components can subscribe on initialization and unsubscribe on disposal. Controllers and maintenance tasks publish through the app service.

Do not expose a realtime API until a real external consumer needs it. Blazor Server already has its own framework SignalR connection; adding a second app-specific hub is unnecessary for the same process.

## 11. Maintenance Scheduler

Replace four hosted services with one scheduler plus testable task handlers.

Current services:

- `TriageService`
- `ConsolidationService`
- `IndexingService`
- `StatsBroadcastService`

Final services:

- `MemoryMaintenanceService` as the only hosted background service.
- `MemoryMaintenanceTasks` or focused internal services for task logic.
- `BackgroundServiceTelemetryTracker` retained.

Scheduled tasks:

| Task | Default Interval | Critical For Readiness | Notes |
|---|---:|---:|---|
| Triage | 5 minutes | Yes | Evaluate state transitions and append events. |
| Index rebuild | 1 hour | Yes | Rebuild `MemoryIndex` until incremental updates are complete. |
| Consolidation | 24 hours | No at startup, warning after failure | Keep simple title-based merge unless domain rules change. |
| Stats refresh | On demand and after mutation | No separate timer | Remove periodic stats broadcast scan. |

The scheduler should run due tasks on startup after a short delay, then on configured intervals. It should record start, success, failure, and duration for each task.

## 12. Storage Design

Keep file storage. It is inspectable, simple, and matches the current project scale.

Required hardening:

- Keep sanitized IDs, but add explicit validation at the app service boundary.
- Reject empty IDs after normalization.
- Add a lock or `SemaphoreSlim` around save/delete/load-all sequences that can conflict.
- Preserve atomic temp-write plus move behavior.
- Stop silently swallowing corrupt file failures. Report skipped corrupt files through logging and a storage diagnostic service.
- Return stable snapshots from `LoadAll()` rather than lazy enumeration over files that may change during iteration.
- Keep event log append operations locked.

Do not add PostgreSQL in this refactor. Add a future ADR only when there is a measured scale problem or multi-writer requirement.

## 13. Search Design

Keep keyword search honest and add a clearly bounded semantic baseline for tool use.

Current search scans all records. Final search should be improved without pretending to be embedding search:

- Query title, content, and tags case-insensitively.
- Support status and tag filters in the same app service method.
- Use `MemoryIndex.ByTag` for tag pre-filtering where practical.
- Return deterministic ordering: status priority if needed, then `LastUpdated` descending, then title/id.
- Clamp result limits.
- Keep keyword search deterministic and wire-compatible.
- Provide a separate local semantic-search endpoint for tool use that scores title, tag, reference, content, and alias overlap.
- Return match explanations with semantic results so callers can judge whether the local score is useful.
- UI labels should say "Search" or "Keyword search" unless the UI is explicitly invoking the semantic baseline.

Do not introduce embeddings, vector indexes, provider abstractions, or PostgreSQL now. Treat those as future ADRs driven by observed search failures in the project wiki.

### 13.1 MCP Tooling

`MemorySmith.App` exposes `/mcp` as a small HTTP JSON-RPC endpoint for local tool access. The workspace `.vscode/mcp.json` points at `http://localhost:5089/mcp`.

Initial tools:

- `memorysmith_search`: keyword search over wiki records.
- `memorysmith_semantic_search`: local semantic baseline with score and match reason.
- `memorysmith_get`: fetch one memory by id.

The endpoint is local-first project tooling, not a public multi-user API boundary.

## 14. Stats And Health

Stats should be computed through one service.

Rules:

- Build stats from a stable storage snapshot or maintained cache.
- Invalidate/update cache on create, update, delete, usage increment, triage, consolidation, and index rebuild where relevant.
- Serve `GET /api/stats` and UI stats from the same source.
- Remove `StatsBroadcastService`; in-process notifications are enough.

Health semantics:

- `/api/health/live`: returns healthy if the process is running.
- `/api/health/ready`: checks data path readability/writability, event log appendability, and critical maintenance freshness after a startup grace period.
- UI Health page should display readiness as the operator status. It may also show liveness separately for diagnostics.

## 15. Security For A Shippable Local App

The app is not multi-tenant, but it should not be casually exposed.

Minimum shippable posture:

- Bind development launch profiles to localhost by default.
- Health live can remain anonymous.
- Readiness can remain anonymous only if it does not leak sensitive paths or secrets.
- API routes should support an optional API key policy for non-local access.
- Mutating API routes should require either local-only access or a configured API key.
- Blazor UI calls services directly and does not need the API key internally.
- Use standard ProblemDetails for validation and API errors.
- Apply rate limiting globally to API routes if remote API access is enabled; otherwise remove unused rate-limiter configuration.

This avoids adding a full identity system while still making the product safe to run deliberately.

## 16. Configuration Target

Keep configuration small.

Final keys:

```json
{
  "MemorySmith": {
    "DataPath": "../Data/Memories",
    "EventLogPath": "../Data/Events/audit.log",
    "ApiKey": null,
    "AllowRemoteApi": false,
    "Maintenance": {
      "TriageMinutes": 5,
      "IndexingMinutes": 60,
      "ConsolidationHours": 24,
      "StartupGraceSeconds": 30
    },
    "Limits": {
      "MaxPageSize": 100,
      "MaxSearchLimit": 100,
      "MaxContentLength": 20000,
      "MaxTags": 50,
      "MaxReferences": 200
    }
  }
}
```

`MemorySmith:DataPath` intentionally defaults to `../Data/Memories`. That folder is the project memory wiki for MemorySmith itself. Tests that need real project context must copy `Data/Memories` into a temp directory before exercising stores, services, or API hosts so the source wiki is not mutated during validation.

Remove these keys:

- `WorkerApiBaseUrl`
- `WorkerHubUrl`
- `WorkerApiTimeoutSeconds`
- `DashboardOrigin`
- `StatsBroadcastSeconds`
- `StatsPollingSeconds`, unless a UI fallback timer remains after direct service conversion

## 17. Documentation Governance

After this refactor starts:

- This document is the active design plan.
- `RefactorMemoryWiki.md` is the living memory log, not a competing plan.
- `Data/Memories` is the structured memory wiki consumed by the app and used as a copied test fixture source.
- Historical plans/reviews should be marked as superseded or moved under an archive folder.
- `DashboardRealtimeConfiguration.md` should be deleted or replaced because its knobs are removed by the single-host design.
- New implementation notes should cite current code paths and test results.

## 18. Migration Plan

### Phase 0 - Baseline And Guardrails

1. Keep this document as the active plan.
2. Confirm `dotnet build MemorySmith.slnx -v minimal` and `dotnet test MemorySmith.slnx` pass when no app process is locking binaries.
3. Add or update tests around current REST contracts before moving host code.
4. Add a superseded banner to historical plans if they keep causing confusion.

Acceptance criteria:

- Build and tests pass.
- Final design doc exists.
- Existing REST behavior has baseline tests.

### Phase 1 - Application Service Extraction

1. Create shared query/request DTOs for list/search.
2. Create `MemoryApplicationService` in the current host or new `MemorySmith.App`.
3. Move validation, paging, search, stats, write-side notifications, and event append behavior into the service.
4. Convert controllers to thin wrappers over the service.
5. Add NUnit tests for the service.

Acceptance criteria:

- Controllers contain no business logic beyond HTTP status mapping.
- Search/list bounds are enforced.
- Server-side validation exists for create/update.
- API tests pass.

### Phase 2 - Single Host Assembly

1. Create `MemorySmith.App` or convert one existing host into the single app.
2. Move Razor components into the single app.
3. Register controllers, Razor components, MudBlazor, storage, maintenance scheduler, health checks, and app services in one `Program.cs`.
4. Preserve all REST routes.
5. Delete Dashboard HTTP client use from pages.

Acceptance criteria:

- One launch profile starts UI, API, and maintenance.
- UI loads without the Worker app running.
- API routes still work at the same paths.
- `MemorySmith.slnx` no longer requires both Worker and Dashboard web hosts for normal operation.

### Phase 3 - Remove Split-Process Plumbing

1. Delete custom Dashboard hub and `Microsoft.AspNetCore.SignalR.Client` dependency unless another feature still needs it.
2. Delete CORS policy created solely for Dashboard origin.
3. Delete worker URL and hub URL configuration.
4. Delete `StatsBroadcastService`.
5. Replace live stats updates with in-process notifications and explicit refresh.

Acceptance criteria:

- No `WorkerApiBaseUrl`, `WorkerHubUrl`, `DashboardOrigin`, or `StatsBroadcastSeconds` remain in active app config.
- No dashboard component creates a `HubConnection` to MemorySmith itself.
- No UI page uses `MemoryApiClient`.

### Phase 4 - Maintenance And Storage Hardening

1. Replace separate maintenance hosted services with `MemoryMaintenanceService`.
2. Move triage/consolidation/index logic into testable task handlers.
3. Add readiness checks for storage and critical maintenance freshness.
4. Add synchronization and diagnostics to file storage.
5. Add tests for corrupt-file reporting and concurrent save/delete behavior.

Acceptance criteria:

- One maintenance hosted service is registered.
- Readiness fails when storage is unwritable.
- Corrupt files are observable in logs/diagnostics.
- Tests cover maintenance task success/failure telemetry.

### Phase 5 - UI Simplification

1. Split large pages only around natural responsibilities.
2. Move tag filtering/search pagination to app service results.
3. Make Health show readiness and service telemetry from the app service.
4. Replace debug-only logging in dialogs.
5. Keep visual layout functional and modest; no landing-page redesign.

Acceptance criteria:

- Memory Viewer has clear list/search/create/edit/delete workflows.
- Health page shows readiness, stats, maintenance telemetry, and recent memory events.
- No UI text promises semantic search, queues, embeddings, or unavailable metrics.

### Phase 6 - Shippability Closeout

1. Update README with one-command run instructions.
2. Update user docs for data path, API key/local access, backups, and maintenance intervals.
3. Archive or mark superseded docs.
4. Run full build/test.
5. Perform manual smoke test: open UI, create memory, edit memory, search, increment usage, delete, check health, call API route.

Acceptance criteria:

- One app can be published and run.
- Full build/test passes.
- README and user docs describe the final app, not the old split-host topology.
- Historical docs no longer appear to be active guidance.

## 19. Test Strategy

Use NUnit only.

Required tests:

- `MemoryApplicationService` list/search/page bounds, validation, CRUD, usage increment, stats invalidation.
- `FileMemoryStore` sanitization/validation behavior, atomic save, corrupt file diagnostics, concurrent write/delete safety.
- `FileEventStore` append/read/filter/malformed line behavior.
- `MemoryMaintenanceService` task due scheduling, telemetry success/failure, cancellation behavior.
- API integration tests with `WebApplicationFactory` for preserved routes.
- Health integration tests for live/ready semantics.
- Existing consolidation tests migrated away from private reflection if task handlers become public/internal testable services.

Optional but useful:

- One UI smoke test using a browser runner after the app project is stable.

## 20. Delete Or Keep Decisions

| Item | Decision | Reason |
|---|---|---|
| `MemorySmith.Worker` project | Delete or retire after `MemorySmith.App` is green | Separate process is unnecessary. |
| `MemorySmith.Dashboard` project | Delete or retire after UI moves into `MemorySmith.App` | Separate process is unnecessary. |
| REST controllers | Keep | External automation boundary. |
| Custom Dashboard SignalR hub | Delete | Only bridges two local processes. |
| `MemoryApiClient` | Delete | UI calls app service directly. |
| CORS policy for Dashboard | Delete | Same-origin single host. |
| `StatsBroadcastService` | Delete | In-process notifications and direct stats service replace it. |
| `MemoryIndex` | Keep | Useful for tag/reference lookup once wired into search. |
| File storage | Keep | Fits current scope and keeps shipping simple. |
| PostgreSQL/embeddings docs | Archive | Future ideas, not active plan. |
| NUnit | Keep | Project preference and existing test stack. |

## 21. Risks And Mitigations

| Risk | Mitigation | Confidence |
|---|---|---:|
| Moving two hosts into one app causes route or DI regressions. | Extract app service first; add API integration tests before moving UI. | 0.88 |
| UI loses live update behavior when custom hub is removed. | Replace with in-process notification publisher and explicit refresh. | 0.86 |
| File storage still has race conditions. | Add synchronization in storage and service-level tests for concurrent operations. | 0.84 |
| Historical docs continue to mislead future work. | Mark superseded docs and keep this final design as active plan. | 0.93 |
| API key/local-only security adds friction. | Default to local-only; require API key only for remote automation. | 0.82 |
| Single maintenance scheduler becomes too clever. | Keep task handlers small and individually tested. | 0.85 |

## 22. Resolved Decisions

| Decision | Resolution | Confidence |
|---|---|---:|
| One host or two? | One deployable host. | 0.94 |
| Preserve REST API? | Yes, preserve current routes. | 0.95 |
| Keep file storage? | Yes, harden it instead of adding a database. | 0.90 |
| Add semantic search now? | Yes, but only as a local token/alias scoring baseline for tool use. Defer embeddings/vector stores/provider abstractions. | 0.88 |
| Keep custom SignalR hub? | No. Use in-process notifications. | 0.89 |
| Keep periodic stats broadcast? | No. Stats update on demand/mutation/maintenance. | 0.88 |
| Add full auth system? | No. Use local-only plus optional API key for this product scope. | 0.83 |
| Keep four hosted services? | No. Use one maintenance scheduler and task handlers. | 0.86 |
| Add `CreatedAt` now? | No. Avoid schema expansion unless product needs it. | 0.80 |
| Use NUnit? | Yes. | 1.00 |

## 23. Open Questions

None remain for this refactor plan.

Future product questions, such as embedding provider choice, PostgreSQL migration, multi-user security, or public realtime APIs, are intentionally out of scope. They should be handled as new ADRs or plans after this simplification ships and after there is evidence that the added capability is needed.

## 24. Definition Of Done

This refactor is complete when:

- The solution has one deployable web app for UI, API, and maintenance.
- Existing REST API routes are preserved and covered by integration tests.
- Blazor pages call app services directly, not a localhost Worker API.
- Worker/Dashboard split-process config is removed.
- Custom Dashboard SignalR bridge and stats broadcast service are removed.
- Background maintenance is centralized, observable, and testable.
- File storage is synchronized and corrupt-file handling is observable.
- Health live/ready semantics are distinct.
- API validation, bounds, ProblemDetails, and local/API-key security are implemented.
- NUnit build/test pass from a clean terminal with no running app process locking binaries.
- README and user docs describe the final one-app product.
- Historical plans/reviews are clearly superseded.

## 25. Final Implementation Order

1. Add app service and shared DTOs.
2. Make controllers thin and test API contracts.
3. Assemble single host.
4. Move UI to direct service calls.
5. Remove inter-process plumbing.
6. Centralize maintenance scheduling.
7. Harden storage and readiness.
8. Clean docs and ship.

This order gives the safest path: behavior is centralized before process boundaries are removed, and tests protect the external API while the UI is simplified.

## 26. TDD Execution Design

This section is the implementation checklist for red-green work. Add or update these tests before each implementation slice, run them and observe the expected failure, then implement only enough production code to satisfy the behavior without weakening the assertion.

### Slice 1 - Application Service Contract

Test file: `MemorySmith.Tests/MemoryApplicationServiceTests.cs`

Required tests:

- `GetMemoriesAsync_ClampsBoundsAndOrdersDeterministically`: proves page numbers below 1 are normalized, page sizes above the configured maximum are clamped, tag/status filters apply together, and records are returned by `LastUpdated` descending with stable tie-breakers.
- `CreateAsync_WithBlankContent_ThrowsValidationAndDoesNotPersist`: proves invalid user input is rejected before storage, events, stats, or notifications are touched.
- `CreateAsync_NormalizesTagsReferencesAndAuditsMutation`: proves tags/references are trimmed, blank entries are removed, duplicates are folded case-insensitively, the created record is saved, an audit event is appended, and change notifications fire.
- `SearchAsync_AppliesQueryStatusTagsAndLimitClamp`: proves keyword search applies query, status, tag filters, deterministic ordering, and configured limit clamping.
- `IncrementUsageAsync_UpdatesRecordAuditsAndPublishesStats`: proves usage increment is persisted, timestamp changes, audit event is appended, and stats notification reflects the update.

Edge conditions covered: invalid content, invalid bounds, duplicate tags/references, missing memory IDs, status and tag filters combined, search limit abuse.

### Slice 2 - API Contract Preservation

Test file: `MemorySmith.Tests/AppApiContractTests.cs`

Required tests:

- `GetMemories_ClampsPageSizeAndKeepsRouteContract`: proves `GET /api/memories` keeps the existing route and returns `PagedResult<MemoryMetadata>` with bounded paging.
- `PostMemory_WithInvalidBody_ReturnsValidationProblem`: proves API errors use ProblemDetails/validation semantics instead of silent success.
- `CreateGetIncrementDelete_FullApiWorkflow_PersistsRealFiles`: proves the preserved REST routes work end-to-end against a temp file store.
- `HealthLiveAndReady_ReturnSuccessWithoutStartingWorker`: proves the single app serves health routes without a separate Worker process.

Edge conditions covered: bad API body, route preservation, real file-backed persistence, usage mutation, deletion, single-host health.

### Slice 3 - Storage Hardening

Test file: `MemorySmith.Tests/FileMemoryStoreHardeningTests.cs`

Required tests:

- `LoadAll_ReturnsStableSnapshot_WhenFilesChangeAfterCall`: proves callers receive a materialized snapshot, not a lazy directory iterator affected by later file changes.
- `LoadAll_RecordsCorruptFileDiagnosticsAndSkipsBadFile`: proves corrupt files are observable through diagnostics instead of being silently swallowed.
- `ConcurrentSaveDeleteLoadAll_DoesNotThrowOrEscapeBasePath`: exercises realistic concurrent save/delete/enumerate behavior against the file system.

Edge conditions covered: corrupt JSON, concurrent file operations, path traversal style IDs, snapshot stability.

### Slice 4 - Maintenance Task Handlers

Test file: `MemorySmith.Tests/MemoryMaintenanceTasksTests.cs`

Required tests:

- `RunTriageAsync_PersistsTransitionsAndEvents`: proves state transitions are saved and audited by a public/internal task handler, not private reflection.
- `RunIndexRebuildAsync_RebuildsIndexFromStorageSnapshot`: proves the index is rebuilt from current storage.
- `RunConsolidationAsync_MergesPromotesAndDeprecates`: proves existing consolidation behavior survives migration to testable task handlers.

Edge conditions covered: transition audit, missing transition no-op, duplicate merging, promotion rule, deprecation rule.

### Slice 5 - Single Host And UI Direct Calls

Validation targets:

- Build proves `MemorySmith.App` hosts Razor components and controllers in one deployable process.
- API tests prove no Worker process is required.
- Code search proves active app config contains no `WorkerApiBaseUrl`, `WorkerHubUrl`, `DashboardOrigin`, or `StatsBroadcastSeconds`.
- Code search proves active UI pages do not inject or use `MemoryApiClient`.

Manual smoke test after green automated tests: start `MemorySmith.App`, open the UI, create a memory, edit it, search it, increment usage through the API, delete it, and check health.

### Red-Green Rule

Every implementation slice must show the test failing before production code is added or changed. A compile failure from missing production types is acceptable as the red state for a new slice. Do not loosen assertions to make the green state easier; if a test is brittle, improve the behavior-oriented assertion while keeping the same user-visible guarantee.