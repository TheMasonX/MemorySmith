MemorySmith Dashboard Plan (v2 — Codebase-Aligned)

Location: MemorySmith.Core/Docs/Plans/DashboardPlan.md
Date: 2026-04-23
Target Runtime: .NET 10, Blazor Server
UI Stack: MudBlazor (existing)
License Constraint: MIT-compatible, minimal dependencies

1. Purpose

This document defines the implementation of a Blazor Server dashboard for MemorySmith with two primary areas:

Memory Viewer
Browse, search, filter, and edit memories
Health & Stats Dashboard
View runtime health, memory distribution, and background service activity

This plan is strictly aligned with the current implementation:

File-based storage (FileMemoryStore)
REST API (already implemented)
SignalR hub (DashboardHub)
Background services (Triage, Consolidation, Indexing)

No assumptions are made about:

PostgreSQL
Semantic/vector search
gRPC
External queues or embedding pipelines
2. Architecture Overview
2.1 High-Level Flow
Blazor Server (Dashboard)
        │
        ▼
MemoryApiClient (Typed HttpClient)
        │
        ▼
MemorySmith.Worker (REST API + SignalR)
        │
        ▼
FileMemoryStore + Background Services
2.2 Integration Rules (MANDATORY)

The dashboard MUST:

Use existing REST endpoints only
Use existing SignalR hub contract
NOT access storage directly
NOT introduce new backend dependencies
3. Project Structure
3.1 Dashboard Project
MemorySmith.Dashboard/
├── Components/
│   ├── Memory/
│   │   ├── MemoryList.razor
│   │   ├── MemoryDetail.razor
│   │   ├── MemoryEditDialog.razor
│   │   └── MemoryFilters.razor
│   ├── Stats/
│   │   ├── StatsSummary.razor
│   │   ├── ServiceStatusTable.razor
│   │   └── TrendsPanel.razor
│   └── Shared/
│       ├── LoadingState.razor
│       ├── EmptyState.razor
│       └── ErrorState.razor
│
├── Pages/
│   ├── Memories.razor
│   └── Health.razor
│
├── Services/
│   ├── MemoryApiClientAdapter.cs
│   ├── SignalRService.cs
│   └── DashboardState.cs
│
├── Models/
│   ├── MemoryListItemDto.cs
│   ├── MemoryDetailDto.cs
│   ├── StatsViewModel.cs
│   └── ServiceStatusViewModel.cs
4. API Integration
4.1 Endpoints (USE EXACTLY AS IMPLEMENTED)
Method	Route
GET	/api/memories
GET	/api/memories/{id}
POST	/api/memories
PUT	/api/memories/{id}
DELETE	/api/memories/{id}
POST	/api/memories/search
POST	/api/memories/{id}/usage
GET	/api/stats
GET	/api/health/live
GET	/api/health/ready
4.2 Rules
ALWAYS use MemoryApiClient
DO NOT duplicate HTTP logic
DO NOT change API contracts
5. SignalR Integration
5.1 Hub Contract (MUST MATCH CODEBASE)
public interface IDashboardClient
{
    Task ReceiveMemoryUpdate(MemoryUpdateEvent update);
    Task ReceiveStats(StatsSnapshot stats);
}
5.2 Hub Route
/hubs/dashboard
5.3 Behavior Rules
SignalR is optional enhancement, not required for correctness
Dashboard MUST function without it
Use polling fallback for stats
6. Memory Viewer
6.1 Features
Search (keyword-based)
Filter (status, tags)
Pagination
View details
Create/edit/delete
Increment usage
6.2 Search Behavior (IMPORTANT)

Current system supports:

Content.Contains
Title.Contains
Tag.Contains

NOT supported:

semantic search
embeddings
vector similarity

UI MUST NOT imply semantic capability.

6.3 Page Layout
[ Search Bar + Filters ]

[ Memory List (paged) ]

[ Detail Panel OR Dialog ]
6.4 Memory List Fields
Title
Status
Confidence
UsageCount
Tags
References count
LastUpdated
Content preview

DO NOT include:

CreatedAt (does not exist)
6.5 Component Responsibilities
MemoryList
Fetch paged data
Apply filters
Handle selection
MemoryDetail
Display full memory
Trigger usage increment
MemoryEditDialog
Create/edit memory
Validate inputs
Save via API
6.6 Paging

Use existing backend paging:

page
pageSize

DO NOT add X.PagedList.

7. Health & Stats Dashboard
7.1 Purpose

Expose real system metrics only.

7.2 Metrics to Display

From StatsSnapshot:

TotalCount
Working count
Core count
Deprecated count
AverageConfidence
TotalUsage
7.3 Background Services (CRITICAL ADDITION)

Display status for:

Service	Interval
TriageService	5 min
ConsolidationService	24h
IndexingService	1h
7.4 Required Enhancements (Backend)

To support dashboard, the worker SHOULD expose:

LastRun
LastSuccess
LastFailure
Duration

If not yet available:

show "Unknown"
do NOT fabricate values
7.5 Health Page Layout
[ Summary Cards ]

[ Service Status Table ]

[ Optional Trends Panel ]
7.6 DO NOT INCLUDE (Not Real)
queue length
embedding jobs
vector latency
database metrics
8. SignalR + Polling Strategy
8.1 Memory Updates
Subscribe to ReceiveMemoryUpdate
Update UI incrementally
8.2 Stats Updates

IF ReceiveStats is implemented:

use it

ELSE:

poll /api/stats every 5–10 seconds
9. State Management
9.1 DashboardState Service

Responsibilities:

cache stats
cache selected memory
notify UI on updates
9.2 Rules
Avoid global static state
Keep state minimal
Use events or NotifyStateChanged
10. Minimal Dependencies
10.1 Allowed
MudBlazor
SignalR.Client
ASP.NET Core built-ins
10.2 Forbidden (for this phase)
EF Core
Npgsql
X.PagedList
ChartJS packages
OpenTelemetry
Redis
OpenAI SDK
11. Error Handling
Required States

Every page must handle:

Loading
Empty
Error

Use shared components:

LoadingState
EmptyState
ErrorState
12. Implementation Phases
Phase 1 — Foundation
Setup layout
Configure API client
Configure SignalR service
Phase 2 — Memory Viewer
Implement list + paging
Implement search + filters
Implement detail panel
Implement create/edit/delete
Phase 3 — Health Dashboard
Implement stats cards
Implement service table
Add polling
Add SignalR (optional enhancement)
Phase 4 — Polish
Loading states
Error handling
UI consistency
Performance tuning
13. Future Extensions (NOT REQUIRED NOW)

These are explicitly deferred:

Semantic Search
Add ISemanticSearchProvider
Add embeddings
Add vector index
PostgreSQL
Add PostgresMemoryStore
Add provider switch
Audit Events
Persist MemoryEvent
Add timeline UI
14. Acceptance Criteria

The implementation is complete when:

Memory list loads and paginates correctly
Search works using current API
Filters work
Memory detail displays correctly
Create/edit/delete works
Stats page shows real values
Background services are visible (even if partial)
Dashboard works WITHOUT SignalR
No compile errors from mismatched APIs
No fake or future-only features are shown
15. Critical Guardrails (FOR AGENTS)

DO NOT:

Invent APIs
Change routes
Assume PostgreSQL exists
Assume semantic search exists
Introduce new infrastructure
Add unnecessary packages

ALWAYS:

Verify types exist before using them
Follow actual codebase naming
Prefer simple solutions over abstractions
Keep UI aligned with real data
Final Note

This dashboard is intentionally designed to reflect the real system as it exists today, not the future architecture.

Future capabilities (semantic search, Postgres, graph, embeddings) should be added incrementally after they exist in the backend, not assumed at the UI layer.