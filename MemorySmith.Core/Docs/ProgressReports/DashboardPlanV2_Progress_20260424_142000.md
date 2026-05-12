# DashboardPlanV2 Progress Report
Date: 2026-04-24
Plan: `MemorySmith.Core/Docs/Plans/DashboardPlanV2.md`

## Iteration Focus
Completed hardening work to reduce duplicated logic, improve maintainability, and increase configurability and test coverage for real-time stats behavior.

## Files Updated
- `MemorySmith.Core/Models/StatsSnapshotFactory.cs` (new)
- `MemorySmith.Worker/Controllers/StatsController.cs`
- `MemorySmith.Worker/Controllers/MemoriesController.cs`
- `MemorySmith.Worker/Services/StatsBroadcastService.cs`
- `MemorySmith.Worker/appsettings.json`
- `MemorySmith.Worker/appsettings.Development.json`
- `MemorySmith.Tests/StatsSnapshotFactoryTests.cs` (new)

## Completed Work

### 1) Consolidated stats computation
Created `StatsSnapshotFactory` in Core:
- `StatsSnapshotFactory.Build(IEnumerable<MemoryRecord>)`
- Centralized all stats aggregation (counts, avg confidence, usage)
- Added null-guard via `ArgumentNullException.ThrowIfNull`

### 2) Refactored worker to shared stats utility
Updated worker consumers to use shared factory:
- `StatsController.GetStats()`
- `MemoriesController` SignalR stats broadcast path
- `StatsBroadcastService` periodic push path

This removed duplicated aggregation logic and reduced drift risk.

### 3) Added configurable periodic stats broadcast interval
Updated `StatsBroadcastService`:
- Reads `StatsBroadcastSeconds` from configuration
- Uses positive-value guard and defaults to 10 seconds

Updated configuration files:
- `MemorySmith.Worker/appsettings.json`
- `MemorySmith.Worker/appsettings.Development.json`

### 4) Added unit tests for shared stats utility
Created `StatsSnapshotFactoryTests` with NUnit:
- Empty input => zeroed snapshot
- Mixed statuses => expected counts/avg/usage
- Null input => `ArgumentNullException`

## Validation
- Build: `run_build` => **successful**
- Tests: `run_tests` project `MemorySmith.Tests` => **22 passed / 0 failed**

## Assumptions
1. Shared factory in Core is the correct ownership boundary for stats aggregation used by multiple worker components.  
   Confidence: 0.94
2. `StatsBroadcastSeconds` should default to 10s when absent/invalid to preserve prior runtime behavior.  
   Confidence: 0.91
3. Additional UI-specific test harness can be deferred while strengthening core/worker logic reliability first.  
   Confidence: 0.82

## Unknowns
1. Desired production tuning for `StatsBroadcastSeconds` under real load remains unknown.  
   Confidence: 0.74
2. Whether dashboard should display or expose current broadcast interval to operators is undecided.  
   Confidence: 0.67

## Open Questions
1. Should `StatsPollingSeconds` (dashboard) and `StatsBroadcastSeconds` (worker) be documented together in user docs for operational tuning?  
   Confidence this should be done: 0.86
2. Should a dedicated worker endpoint expose current runtime config/effective intervals for diagnostics?  
   Confidence this may be useful: 0.71
3. Should we add dashboard component tests (bUnit) in a future slice, or keep current test scope to core/worker only?  
   Confidence this needs team decision: 0.69

## Issues / Risks
- No blocking issues in this iteration.
- Remaining gap is primarily documentation and optional dashboard UI test depth.

## Recommended Next Steps
1. Add/update user-facing docs for dashboard and worker interval configuration.
2. Consider adding dashboard component tests when a UI test strategy is selected.
3. Add minor UX polish for selected-memory visual indication in Memory Viewer if desired.
