# Refactor Memory Wiki

Date: 2026-05-07
Scope: MemorySmith project-wide refactor planning

## Evidence Log

- Worker wiring includes file storage, event store, memory index, telemetry tracker, and four hosted services (triage, consolidation, indexing, stats broadcast).
  - Source: MemorySmith.Worker/Program.cs
- Memories API currently computes stats and pushes SignalR updates after write operations.
  - Source: MemorySmith.Worker/Controllers/MemoriesController.cs
- Stats API exposes both aggregate stats and background service telemetry.
  - Source: MemorySmith.Worker/Controllers/StatsController.cs
- FileMemoryStore uses full directory enumeration for LoadAll and per-status probing for Load/Delete.
  - Source: MemorySmith.Storage/FileMemoryStore.cs
- Dashboard Health page combines polling plus SignalR and calls RefreshAsync on ReceiveMemoryUpdate.
  - Source: MemorySmith.Dashboard/Components/Pages/HealthStats.razor
- Memory viewer page contains UI, filtering, search mode control, CRUD orchestration, and pagination in one component.
  - Source: MemorySmith.Dashboard/Components/Pages/MemoryViewer.razor
- Test framework is NUnit (not xUnit), with worker/storage/core referenced in the test project.
  - Source: MemorySmith.Tests/MemorySmith.Tests.csproj

## Candidate Refactor Themes

- Reduce repeated store scans and duplicate stats computations on write paths.
- Separate UI rendering from orchestration logic in large Razor components.
- Unify API contracts to remove duplicate request models between dashboard and worker.
- Centralize service intervals and names to avoid drift between worker services and dashboard display.
- Improve operability by making health/readiness semantics explicit and testable.

## Work Log

- Created full refactor plan at `MemorySmith.Core/Docs/Plans/ProjectRefactorPlan_20260507.md`.
- Included: phased strategy, evidence map, risk register, assumptions, open questions, and confidence values.
- Verified test execution constraint: `dotnet test` can fail when `MemorySmith.Worker.exe` holds project binaries (file lock contention observed).
- Stopped running worker process and re-ran full build + tests: solution builds clean; tests pass when worker is not locking binaries.
- Revalidated current compile posture: `dotnet build MemorySmith.slnx -v minimal` succeeds with no code warnings.
- Updated Dashboard review with resolved open questions addendum to remove stale uncertainty and contradictory claims.

## Decisions Captured

- Prefer simplification before feature expansion (for example, index/search alignment before semantic stack additions).
- Preserve existing API routes/contracts while refactoring internals.
- Keep NUnit as the testing standard for all new test work in this repository.

## Refined Assumptions

- Superseded assumption: the earlier Worker API/SignalR + separate Blazor Server boundary should not be preserved as the final architecture.
- Final boundary: one deployable app host with file storage, preserved REST API routes, in-process UI/service calls, and no custom Dashboard-to-Worker bridge.
- Preserve API compatibility during simplification phases.
- Prioritize operational correctness over throughput micro-optimization in this cycle.

## Open Questions Answered

- Search strategy: use index-assisted keyword retrieval by default while preserving current wire contract.
- Stats freshness target: <=10s default, configurable 5-30s.
- Readiness semantics: fail on critical service drift (triage/indexing), not solely on consolidation lag.
- Consistency model: eventual consistency accepted for stats with bounded staleness.
- Dev workflow: managed worker lifecycle around build/test to prevent file-lock failures.
- Docs governance: maintain one active current-state doc; archive/supersede stale review snapshots.
- Warning posture: current compile baseline is clean; warning claims in active docs must remain date-stamped and evidence-backed.
- Stats push status: backend real-time stats push is implemented on write paths and periodic broadcast; contrary doc claims are stale/inaccurate.
- Health signal: dashboard system status should surface readiness (or both live+ready), not liveness only.

## Additional Issues Found

- Status dropdown option binding style is ambiguous in dashboard memory dialog; make expression explicit for maintainability.
- Response caching middleware configured but endpoint caching policy usage appears absent.
- Rate limiting policy configured but explicit enforcement wiring appears absent.
- Unbounded `pageSize` and `search limit` parameters in memories API.
- Silent corrupt-file skip in file store reduces observability.
- Mutating endpoints currently rely on client behavior and do not enforce required business fields server-side.

## Additional Issues Found (Second Follow-up)

- Dashboard dialog catches validation exceptions and writes only to debug output instead of structured logging.
- Dashboard production timeout is set to 2 seconds while development uses 10 seconds; may be too aggressive for transient network/service conditions.
- Documentation drift persists: some progress/review docs claim zero warnings and stubbed services, conflicting with current build output and current service implementations.
- Documentation drift also includes factual inaccuracies (for example, claims that `ReceiveStats` is never called even though code invokes it).
- Dashboard health card currently reports liveness only, even though readiness endpoint is available.

## Final Simplification Pass

- Final source-of-truth design created at `MemorySmith.Core/Docs/Plans/MemorySmith_FinalRefactorDesign_20260507.md`.
- Key decision: collapse Worker and Dashboard into one deployable web app while preserving REST API routes for automation.
- Key deletion targets after migration: Dashboard `HttpClient`, custom Dashboard SignalR hub/client, CORS policy for Dashboard origin, Worker/Dashboard URL config, and periodic stats broadcast service.
- Keep file storage, Core, Storage, and NUnit; harden storage and health rather than adding PostgreSQL, embeddings, queues, or a full auth system in this refactor.
- Historical plans/reviews are evidence only after this point; the final design doc is the active plan.

## TDD Implementation Log

- 2026-05-11: Added TDD execution design to the final refactor doc. The first red-test slice covers `MemoryApplicationService`, preserved API contracts, storage hardening, and public maintenance task handlers.
- Rule for this phase: red tests must fail for the real missing behavior before implementation; green must come from production code, not weakened assertions or coverage-only tests.
- 2026-05-11: Captured red state with `dotnet test MemorySmith.slnx -v minimal`; after adding red tests and `MemorySmith.App`, build failed because the new app had no entry point (`CS5001`).
- 2026-05-11: Implemented first green slice: `MemoryApplicationService`, app DTOs/options/validation, in-process change publisher, app API controllers, storage diagnostics/synchronization, and public maintenance task handlers.
- 2026-05-11: Migrated consolidation tests away from private reflection over Worker hosted service to public `MemoryMaintenanceTasks` tests.
- 2026-05-11: Added Blazor UI directly in `MemorySmith.App` using `MemoryApplicationService`; updated active solution and launch profile so `MemorySmith.App` is the only deployable host.
- 2026-05-11: Validation passed: `dotnet test MemorySmith.slnx -v minimal` reports 45/45 tests passing. Active app search shows no `WorkerApiBaseUrl`, `WorkerHubUrl`, `DashboardOrigin`, `StatsBroadcastSeconds`, `MemoryApiClient`, `HubConnection`, `DashboardHub`, or `StatsBroadcastService` references under `MemorySmith.App/**`.
- 2026-05-12: Seeded `Data/Memories/Core` with project-wiki memory records for architecture boundary, validation command, Data folder policy, scope boundaries, and storage rules.
- 2026-05-12: Added `ProjectWikiTestbaseTests` so the repository `Data/Memories` wiki is copied to temp storage and exercised through `FileMemoryStore`, `MemoryApplicationService`, and `MemorySmith.App` API integration without mutating the source wiki.
- 2026-05-12: Validation passed: `dotnet test MemorySmith.slnx -v minimal` reports 48/48 tests passing.
- 2026-05-12: Added `MemorySmith.App` launch profile `http` on port 5089 with `ASPNETCORE_ENVIRONMENT=Development`, so local `dotnet run --project MemorySmith.App --launch-profile http` serves Blazor/MudBlazor static web assets correctly.
- 2026-05-12: Fixed MudBlazor provider placement by rendering `HeadOutlet` and `Routes` with `InteractiveServer` and hosting providers in `MainLayout`. Browser smoke at `/memories` loads five project-wiki records from `Data/Memories` and search for `testbase` returns the Data Folder policy record.
- 2026-05-12: Added project-wiki records for MCP integration, search roadmap, semantic-search gaps, and general tool friction so future research can be driven from `Data/Memories` itself.
- 2026-05-12: Added `POST /api/memories/search/semantic` as a local token/tag/title/reference/alias scoring baseline with match explanations. This is not an embedding/vector index yet; the wiki records track that gap explicitly.
- 2026-05-12: Added `/mcp` HTTP JSON-RPC endpoint with `memorysmith_search`, `memorysmith_semantic_search`, and `memorysmith_get` tools over the project wiki. Updated `.vscode/mcp.json` to point at `http://localhost:5089/mcp`.
- 2026-05-12: Red-green validation for the tool slice: `McpAndSemanticSearchTests` first failed against missing routes, then passed 4/4 after implementing semantic search and MCP tools.
- 2026-05-12: Full validation passed: `dotnet test MemorySmith.slnx -v minimal` reports 52/52 tests passing.
- 2026-05-12: Live MCP smoke passed against `http://localhost:5089/mcp`: `tools/list` returned `memorysmith_search`, `memorysmith_semantic_search`, and `memorysmith_get`; `memorysmith_semantic_search` for `semantic search embeddings` returned the Semantic Search Gap record first with match reasons.
- 2026-05-12: Dogfooding refinement: `/memories` now exposes a Keyword/Semantic search mode switch. Semantic mode uses the existing local semantic baseline, shows snippets plus score/match reasons, and keeps confidence/usage metadata accurate in results. Focused validation passed: `dotnet test MemorySmith.slnx --filter "MemoryApplicationServiceTests|McpAndSemanticSearchTests" -v minimal` reports 10/10 tests passing. Full validation passed: `dotnet test MemorySmith.slnx -v minimal` reports 53/53 tests passing. Browser smoke passed for semantic query `semantic search embeddings` with tag `project-wiki`, and keyword mode returned to the standard list view. MCP semantic search for `semantic search UI keyword mode match reasons` returned `project-wiki-semantic-ui-current` first.
- 2026-05-12: Reworked `/memories` into a compact two-pane workbench with a dense command bar, persistent detail pane, internal scrolling, tag/reference chips, semantic score/match reason display, and copy actions for raw content or agent context. Added operational Windows Service install/uninstall flags to `MemorySmith.App` (`--install-service`, `--uninstall-service`, service metadata options, and runtime arguments after `--`) without adding new wiki record fields.
- 2026-05-12: Refined the active app shell and `/memories` workbench to default to a VS Code-inspired dark theme. MudBlazor now uses a dark palette by default, the app title bar/drawer/workbench panes use editor-like dark surfaces, and the page is fixed to viewport height with internal pane scrolling.
