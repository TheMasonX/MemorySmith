# Dashboard Plan Review — Verification Against Codebase

**Reviewer:** GitHub Copilot  
**Date:** 2026-04-23  
**Plan reviewed:** `MemorySmith.Core/Docs/Plans/DashboardPlan.md`  
**Codebase state:** MemorySmith (net10.0), current working tree

---

## Executive Summary

The Dashboard Plan was written against an earlier, conceptual state of the project. Since then, **significant portions of the plan have already been implemented** — the Worker API, SignalR hub, Dashboard Blazor app, and MudBlazor UI all exist. However, the plan contains several **inconsistencies with the current implementation**, promotes approaches that have been superseded (e.g., PostgreSQL/pgvector, xUnit, X.PagedList), and is silent on real system components (background worker services, MemoryIndex, MemoryScorer) that are worth surfacing in the dashboard.

**Overall confidence that the plan accurately describes the current system: Low–Medium (35%).**  
Most of the implementation diverges from the plan's specifics in meaningful ways.

---

## 1. What Is Already Implemented

The following plan items are **fully or substantially implemented** and require no action:

| Plan Item | Status | Notes |
|---|---|---|
| `GET /api/memories` with filtering and pagination | ✅ Done | `MemoriesController.GetAll` with `status`, `tags`, `page`, `pageSize` |
| `GET /api/memories/{id}` | ✅ Done | |
| `POST /api/memories` (Create) | ✅ Done | |
| `PUT /api/memories/{id}` | ✅ Done | |
| `DELETE /api/memories/{id}` | ✅ Done | Returns 204 as planned |
| `POST /api/memories/{id}/usage` | ✅ Done | *Response differs — see §3* |
| `POST /api/memories/search` | ✅ Done | *Keyword only — see §3* |
| `GET /api/health/live` and `/ready` | ✅ Done | *No differentiation — see §3* |
| `GET /api/stats` | ✅ Done | `StatsController` returning `StatsSnapshot` |
| SignalR `DashboardHub` | ✅ Done | *Interface name and method signatures differ — see §3* |
| MudBlazor Dashboard project | ✅ Done | `MemorySmith.Dashboard` with Memory Viewer + Health & Stats pages |
| `MemoryApiClient` typed HttpClient | ✅ Done | Covers all endpoints including search and stats |
| `PagedResult<T>` model | ✅ Done | Custom implementation, not X.PagedList |
| `MemoryDetailDialog` | ✅ Done | MudBlazor dialog for create/edit |
| Rate limiting (fixed window) | ✅ Done | 100 req/min, `UseRateLimiter()` in pipeline |
| Response caching | ✅ Done | `AddResponseCaching()` + `UseResponseCaching()` |
| Health checks | ✅ Done | `AddHealthChecks()` + `MapHealthChecks()` |
| CORS policy ("Dashboard") | ✅ Done | Configurable via `DashboardOrigin` setting |
| Serilog | ✅ Done | Console + rolling file sink |
| Swagger/OpenAPI | ✅ Done | Swashbuckle in dev mode |

---

## 2. Significant Gaps: Plan Ignores Real System Components

The plan was written without awareness of the following **real, already-implemented components**. This is a meaningful gap because the dashboard should arguably surface all of them.

### 2.1 Background Worker Services

Three `BackgroundService` implementations run in `MemorySmith.Worker`. None are mentioned in the plan:

| Service | Interval | What it does |
|---|---|---|
| `TriageService` | Every 5 minutes | Evaluates every record through `MemoryStateMachine` and promotes/deprecates based on score |
| `ConsolidationService` | Every 24 hours | Currently a stub — logs count, future merging/deduplication |
| `IndexingService` | Every 1 hour | Rebuilds `MemoryIndex` (keyword/tag/reference index) |

**Impact:** The Health & Stats dashboard has no visibility into whether triage/indexing ran recently, what records were transitioned, or whether consolidation is active. The plan's proposed "queue length" and "pending embedding jobs" metrics don't map to any real system concept — there is no queue or embedding pipeline.

### 2.2 MemoryStateMachine and MemoryScorer

The scoring formula (`MemoryScorer.Score`) combines `UsageCount`, `Confidence`, `References.Count`, and recency into a composite score. State transitions are driven by thresholds (Working: ≥1.0, Core: ≥2.0, Deprecated: <0.2). The dashboard plan makes no mention of:

- Surfacing per-memory score in the Memory Viewer
- Showing recent state transitions (a `MemoryEvent` model *exists* for this)
- Explaining why a memory is in a given status

### 2.3 MemoryIndex

`MemoryIndex` maintains in-memory dictionaries: `ById`, `ByTag`, and `ByReference`. This is rebuilt hourly by `IndexingService`. The search endpoint does **not** use it — it does a `LoadAll()` linear scan. The plan doesn't mention the index at all, but the dashboard could expose tag-cloud counts or reference-graph navigation using it.

### 2.4 MemoryEvent Model

`MemoryEvent` (with `Timestamp`, `MemoryId`, `Action`, `Details`) exists in the Core models. However, events are constructed by `MemoryStateMachine.Evaluate()` but **never persisted** — in `TriageService`, the returned `evt` is discarded. The plan does not address this and the audit/history feature implied by the model is not implemented.

---

## 3. Inconsistencies Between Plan and Implementation

### 3.1 SignalR Hub Interface — **HIGH SEVERITY**

The plan defines the hub interface as:

```csharp
// Plan
public interface IStatsClient {
    Task ReceiveMetricUpdate(MetricData data);
    Task ReceiveSystemStatus(SystemStatus status);
}
public class DashboardHub : Hub<IStatsClient> { }
```

The actual implementation is:

```csharp
// Reality (Hubs/DashboardHub.cs)
public interface IDashboardClient {
    Task ReceiveMemoryUpdate(MemoryUpdateEvent update);
    Task ReceiveStats(StatsSnapshot stats);
}
public class DashboardHub : Hub<IDashboardClient> { }
```

The plan's type names (`MetricData`, `SystemStatus`) don't exist in the codebase. Any code following the plan's sample would fail to compile. The Dashboard's `HealthStats.razor` correctly uses `ReceiveMemoryUpdate`, not the plan's `ReceiveMetricUpdate`.

**Confidence (plan is accurate here): Very Low (5%).**

### 3.2 SignalR Hub URL — **MEDIUM SEVERITY**

- **Plan says:** `app.MapHub<DashboardHub>("/dashboardHub")`  
- **Reality:** `app.MapHub<DashboardHub>("/hubs/dashboard")`

The `MemoryApiClient` and Dashboard components must use `/hubs/dashboard`. Any implementation following the plan's URL would fail to connect.

**Confidence (plan is accurate here): Very Low (5%).**

### 3.3 `ReceiveStats` Never Called — **MEDIUM SEVERITY**

`IDashboardClient.ReceiveStats(StatsSnapshot)` is defined in the hub interface but **no code in the Worker ever calls it**. The `StatsController` does not inject `IHubContext` and never pushes stats updates. The Health & Stats dashboard therefore only receives `ReceiveMemoryUpdate` events, not live stats. To get updated stats, the dashboard polls via `MemoryApiClient.GetStatsAsync()` (if it does — this is not confirmed).

### 3.4 Search Is Keyword-Only, Not Semantic — **HIGH SEVERITY**

The plan dedicates significant space to semantic/vector search with pgvector, OpenAI embeddings, and HNSW indexes. The actual search implementation is:

```csharp
records = records.Where(r =>
    r.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
    r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
    r.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
```

This is a linear full-text keyword scan over all records loaded from disk. There are no embeddings, no vector index, no pgvector, no external AI API calls. The `Data/Graph/embeddings/` folder exists in the workspace but appears empty.

**Confidence (semantic search is implemented): Very Low (5%).**

### 3.5 Storage Is File-Based, Not PostgreSQL — **HIGH SEVERITY**

The plan discusses PostgreSQL + EF Core as the data store and describes a migration path from JSON files. The implementation uses `FileMemoryStore` exclusively — flat JSON files organised into status-named subdirectories. No database, no EF Core, no Npgsql. The plan's data model discussion (embedding columns, migrations) does not apply.

**Confidence (PostgreSQL is in use): Very Low (5%).**

### 3.6 Health Endpoints Are Identical — **LOW SEVERITY**

The plan distinguishes:
- `/api/health/live` — simple liveness (is the process up?)
- `/api/health/ready` — readiness with dependency checks (DB, embedding service, etc.)

In reality, both endpoints use the same `MapHealthChecks()` with no custom health check registrations. They return identical responses and there are no dependency checks (because there are no dependencies like a DB to check). This is correct given the current file-based architecture, but the plan's description of readiness checks is aspirational.

### 3.7 `/api/memories/{id}/usage` Response — **LOW SEVERITY**

- **Plan says:** 204 No Content  
- **Reality:** 200 OK with `{ usageCount: N }`

This is an improvement over the plan. The `MemoryApiClient` should be checked to ensure it handles the 200 response correctly (it does — it checks `IsSuccessStatusCode` but discards the body).

### 3.8 Test Framework — **MEDIUM SEVERITY**

The plan recommends xUnit throughout. The project uses **NUnit** (NUnit 4.3.2 with NUnit3TestAdapter 5.0.0), as stated explicitly in `copilot-instructions.md`. Any test code generated following the plan's guidance would use the wrong framework.

**Confidence (plan test guidance is applicable): Very Low (5%).**

### 3.9 X.PagedList Package — **LOW SEVERITY**

The plan recommends the X.PagedList NuGet package. The implementation uses a custom `PagedResult<T>` class with manual `Skip`/`Take`. This is cleaner and has no additional dependency. X.PagedList should not be added.

### 3.10 Rate Limiting Not Applied Per-Endpoint — **LOW SEVERITY**

The plan suggests applying `[EnableRateLimiting("fixed")]` per controller. The Worker registers the `"fixed"` policy and calls `UseRateLimiter()`, but no controller or action has the attribute. The limiter is in the pipeline but not actually enforced unless a global limiter is also set (it isn't). This means rate limiting is effectively inactive.

### 3.11 `MemoryRecord` Has No `CreatedAt` Field — **LOW SEVERITY**

The plan mentions "Created/Updated" timestamps. The model only has `LastUpdated`. The schema (`memory.schema.json`) also does not include a `createdAt` property. Any dashboard feature showing creation date would need a model change.

### 3.12 `SearchRequest.Status` Filter — **NOTE**

The plan's API table shows search request as `{ "query": string, "limit": int }`. The actual `SearchRequest` class also includes an optional `Status` filter. This is a superset of the plan — no problem, but the plan is incomplete.

### 3.13 copilot-instructions.md References `SplitBrain.Meta` — **NOTE**

The `copilot-instructions.md` references `src/SplitBrain.Meta/` as a documentation-only utility project. No such project exists in this workspace. This appears to be content copied from a different project (`SplitBrain`). The MemorySmith solution does not have a Meta project.

---

## 4. Packages: Plan vs Reality

| Package | Plan Recommends | Actually Used |
|---|---|---|
| MudBlazor | ✅ | ✅ v9.4.0 |
| Radzen.Blazor | Optional alternative | ❌ Not used |
| Blazored.Modal | Optional | ❌ Not used (MudDialog used instead) |
| ChartJS.Blazor | For charts | ❌ Not used |
| Serilog.AspNetCore | ✅ | ✅ v10.0.0 |
| X.PagedList | ✅ | ❌ Custom `PagedResult<T>` |
| StackExchange.Redis | Optional | ❌ Not used |
| EF Core / Npgsql | ✅ | ❌ File storage only |
| Prometheus / OpenTelemetry | Recommended | ❌ Not implemented |
| Azure.AI.OpenAI | For embeddings | ❌ Not implemented |
| xUnit | For tests | ❌ NUnit used instead |
| SignalR.Client | Implied | ✅ v10.0.7 in Dashboard |
| Swashbuckle | ✅ | ✅ v10.1.7 |

---

## 5. Assumptions and Open Questions

### Assumptions Made by the Plan
1. **PostgreSQL is or will be the data store.** — Not true currently. Migration path from files is not planned in any concrete way.
2. **Semantic search via pgvector/embeddings is available.** — The `Data/Graph/embeddings/` folder exists but is empty. No embedding pipeline exists.
3. **There is a queue for embedding jobs.** — No queue exists. Queue length as a metric is meaningless in the current system.
4. **Authentication will be added soon.** — No auth implementation exists anywhere. No placeholder or TODO comments.
5. **xUnit is the test framework.** — NUnit is used.

### Open Questions

1. **When is semantic search planned?** The plan treats it as a near-term implementation task, but the codebase has no foundation for it. Is this in scope for the dashboard or a separate concern?

2. **Should `MemoryEvent` transitions be persisted?** `TriageService` discards `MemoryEvent` objects produced by `MemoryStateMachine.Evaluate()`. A transition history log would be valuable for the Health & Stats dashboard. Is this desired?

3. **Should `ReceiveStats` be pushed in real time?** The hub method exists but is never called. Should `TriageService` push updated `StatsSnapshot` via hub after each triage cycle?

4. **Is rate limiting intended to be enforced?** The middleware is registered but no policy is applied globally or per-endpoint. Intentional (bypass during development)?

5. **Is `ConsolidationService` expected to remain a stub?** If future consolidation logic is planned, the dashboard should surface its last-run time and outcomes.

6. **Should background service health be exposed?** The readiness endpoint could check whether TriageService and IndexingService are running normally (e.g., last successful run timestamp).

7. **Should `MemoryRecord` gain a `CreatedAt` field?** The dashboard UI would benefit from showing when a memory was first created, not just last modified.

---

## 6. Recommended Corrections to the Plan

The following changes to `DashboardPlan.md` would align it with the current codebase:

1. **Update hub examples** to use `IDashboardClient`, `ReceiveMemoryUpdate(MemoryUpdateEvent)`, `ReceiveStats(StatsSnapshot)`, and hub URL `/hubs/dashboard`.
2. **Remove or defer the semantic search section** (pgvector, OpenAI embeddings). Replace with a note that the current implementation uses keyword search via `Contains`, and semantic search is a future enhancement contingent on storage migration.
3. **Remove PostgreSQL/EF Core as current targets.** The plan should acknowledge the file-based store is in production and describe the migration as a future phase, not current implementation.
4. **Replace xUnit references with NUnit.**
5. **Remove X.PagedList recommendation** — `PagedResult<T>` is already implemented and sufficient.
6. **Add a section on the background worker services** (Triage, Consolidation, Indexing) and what the Health & Stats dashboard should show about them.
7. **Correct the health dashboard metrics** — replace "queue length" and "pending embedding jobs" with real available metrics: `TotalCount`, `Unconsolidated`, `Working`, `Core`, `Deprecated`, `AverageConfidence`, `TotalUsage`, and (once implemented) service last-run timestamps.
8. **Add `[EnableRateLimiting("fixed")]` to the plan's controller guidance** and note that the `GlobalLimiter` should also be considered.
9. **Note the `ReceiveStats` gap** — recommend that `TriageService` inject `IHubContext<DashboardHub, IDashboardClient>` and push stats after each cycle.

---

## 7. Summary Confidence Scores

| Plan Section | Accuracy vs Codebase | Confidence |
|---|---|---|
| API endpoint table (routes, methods) | Mostly correct | 75% |
| API request/response schemas | Substantially correct (search superset) | 70% |
| SignalR hub interface and method names | Incorrect | 10% |
| SignalR hub URL | Incorrect | 5% |
| Semantic search (pgvector, embeddings) | Not implemented | 5% |

---

## 8. Open Questions Resolved (2026-05-07 Addendum)

The following resolves the open questions listed in section 5 using current code verification.

1. Semantic search timeline and scope
    - Resolution: Treat semantic search as out-of-scope for the current simplification cycle; keep keyword search and pursue index-assisted filtering first.
    - Evidence: `MemorySmith.Worker/Controllers/MemoriesController.cs`, `MemorySmith.Core/Indexing/MemoryIndex.cs`
    - Confidence: 0.88

2. MemoryEvent transition persistence
    - Resolution: State transitions are now persisted by triage via `IEventStore.AppendEvent`.
    - Evidence: `MemorySmith.Worker/Services/TriageService.cs`
    - Confidence: 0.99

3. Real-time `ReceiveStats` push
    - Resolution: Implemented on both mutation-triggered and periodic paths.
    - Evidence: `MemorySmith.Worker/Controllers/MemoriesController.cs`, `MemorySmith.Worker/Services/StatsBroadcastService.cs`
    - Confidence: 0.99

4. Rate limiting intent
    - Resolution: Treat as intended control that should be explicitly enforced (global and/or endpoint policy wiring) in next hardening pass.
    - Evidence: `MemorySmith.Worker/Program.cs` has policy registration and middleware but no explicit endpoint/global limiter assignment.
    - Confidence: 0.90

5. ConsolidationService status
    - Resolution: No longer a stub; deduplication/promotion/deprecation logic is implemented.
    - Evidence: `MemorySmith.Worker/Services/ConsolidationService.cs`
    - Confidence: 0.97

6. Background service health exposure
    - Resolution: Service telemetry endpoint exists (`/api/stats/services`); readiness semantics should still be strengthened separately.
    - Evidence: `MemorySmith.Worker/Controllers/StatsController.cs`, `MemorySmith.Worker/Program.cs`
    - Confidence: 0.92

7. `CreatedAt` on `MemoryRecord`
    - Resolution: Keep schema unchanged for now (no `CreatedAt`) to avoid unnecessary contract expansion in the current refactor scope.
    - Evidence: `MemorySmith.Core/Models/MemoryRecord.cs`, `Schemas/memory.schema.json`
    - Confidence: 0.84

8. Dashboard health signal source
    - Resolution: Current dashboard status is liveness-only (`api/health/live`); move to readiness (or dual live+ready display) for operator-facing status.
    - Evidence: `MemorySmith.Dashboard/Services/MemoryApiClient.cs`, `MemorySmith.Worker/Program.cs`
    - Confidence: 0.94
| Storage (PostgreSQL/EF Core) | Not implemented | 5% |
| MudBlazor UI components | Correct | 90% |
| Rate limiting / response caching / health checks | Correct in structure, gaps in enforcement | 65% |
| NuGet package recommendations (general) | Mostly appropriate, xUnit wrong | 55% |
| Test framework (xUnit) | Wrong — NUnit used | 0% |
| Background worker services section | Missing entirely | 0% |
| Health dashboard metrics (queue, latency) | Metrics don't map to real system | 20% |
| Security guidance | Appropriate, not yet implemented | 70% |
| Overall plan accuracy | | **35%** |
