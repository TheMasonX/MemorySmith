# Internal Codebase Audit — Sprint 60 (10-Agent Swarm)

**Date:** 2026-07-10
**Scope:** MemorySmith.Agent + MemorySmith (both repos, all layers)
**Type:** Systematic codebase quality sweep (subagent-swarm, 10 partitions)
**Methodology:** 10 parallel subagents, each exploring an independent partition, findings merged and deduplicated.

## Executive Summary

A 10-agent swarm swept both repositories across all layers: Agent.Core, Agent.Planning/Personality, Agent.Tools/Construction, Agent.Memory/Vision, Agent.World/MineflayerAdapter, WebUI.Blazor, MemorySmith.Core, MemorySmith.Storage/App/Bridge, Tests/Training/Benchmarks, and Data/Scripts/CI/Docs.

**Initial findings:** ~144 total (16 P1, 63 P2, 65 P3).
**After 6-chair council peer review:** Recalibrated to **9 P1, 69 P2, 63 P3** (2 P1 findings removed as incorrect, 5 P1 downgraded to P2, 3 P2 upgraded to P1).

**Council overall confidence in the audit report:** 72% — thorough methodology but several findings needed recalibration.

Three major themes emerge:
1. **Error handling violations:** Silent catch blocks, thrown exceptions instead of ToolResult returns — 5 findings confirmed after council recalibration.
2. **Task/CI infrastructure drift:** PascalCase task records mixed with camelCase, orphan `.md` files, no Dependabot/CodeQL — 3 P1 findings.
3. **Runtime correctness bugs:** PlaceBlockGoalDecomposer places all blocks at same coordinates, MemoryScorer weights sum to 1.23, SignalR event name drift — 3 P1 findings confirmed after council.

## Severity Distribution (Post-Council Recalibration)

| Severity | Agent.Core | Planning+Personality | Tools+Construction | Memory+Vision | World+Adapter | WebUI.Blazor | MS.Core | Storage+App+Br | Tests+Training | Data+Scripts+CI | **Total** |
|----------|:----------:|:-------------------:|:------------------:|:-------------:|:-------------:|:------------:|:-------:|:--------------:|:--------------:|:---------------:|:---------:|
| **P1** | 0 | 0 | 0 | 1 | 0 | 1 | 2 | 1 | 0 | 3 | **9** |
| **P2** | 5 | 8 | 6 | 4 | 7 | 8 | 8 | 7 | 11 | 6 | **70** |
| **P3** | 7 | 7 | 11 | 7 | 8 | 6 | 4 | 6 | 4 | 6 | **66** |
| **Total** | 12 | 15 | 17 | 12 | 15 | 15 | 14 | 14 | 15 | 15 | **144** |

---

## P1 — Critical

### P1-AG-001: PlaceBlockGoalDecomposer places all blocks at same coordinates
**File:** `MemorySmith.Agent/Agent.Planning/Decomposition/PlaceBlockGoalDecomposer.cs` (line ~36–51)
**Category:** Logic Error
**The bug:** The loop iterates `pg.Count` times but never increments X/Y/Z — all N blocks target the same position. Blocks cannot occupy the same space; this silently loses N-1 place actions.
**Impact:** Each decomposed PlaceBlockGoal emits N identical PlaceBlock actions, but only the first succeeds; the rest fail at pathfinder goto (already at target) or silently overwrite.
**Recommendation:** Add per-iteration offset (e.g., `x + i` or sequential position advancement).

### P1-AG-002: CreatePageTool throws ArgumentException instead of returning ToolResult
**File:** `MemorySmith.Agent/Agent.Tools/Tools/CreatePageTool.cs` (line ~58, 62)
**Category:** Error handling contract violation
**The bug:** `CreatePageTool.ExecuteAsync` throws `ArgumentException` for missing required parameters instead of returning `ToolResult(false, ...)`. Violates the Sprint 25 P0-C contract: "tool exceptions produce ToolResult(false, ...) instead of propagating."
**Impact:** The LLM/planner calling CreatePage without required params gets an exception instead of a structured failure. While ToolDispatcher's outer catch converts it, this is inconsistent with all 13 other tools that return failure results directly.
**Recommendation:** Replace `throw new ArgumentException(...)` with `return ToolResult(false, "...")`.

### P1-AG-003: SignalR event name drift — hardcoded strings ignore DashboardHubEvents constants
**File:** `MemorySmith.Agent/WebUI.Blazor/AgentBackgroundService.cs` (line ~3580–3596)
**Category:** Data accuracy / SignalR drift
**The bug:** `PushChatToDashboardAsync` sends `"ChatMessage"` (not `DashboardHubEvents.ChatReceived = "ChatReceived"`). `PushGoalToDashboardAsync` sends `"GoalUpdate"` (missing the trailing `d`). The constants in `DashboardHubEvents.cs` are dead code — never referenced at any `SendAsync` call site.
**Impact:** If any other code path ever uses the constants, events will silently not appear on the dashboard. The JS dashboard registers listeners for the correct constant names, so this currently works by accident (the dashboard listens for both old and new names).
**Recommendation:** Replace hardcoded strings with `DashboardHubEvents.ChatReceived` and `DashboardHubEvents.GoalUpdated` constants.

### P1-AG-004: RestMemoryGateway.GetPageAsync has no try-catch — network failures propagate unhandled
**File:** `MemorySmith.Agent/Agent.Memory/RestMemoryGateway.cs` (line ~73–83)
**Category:** Error handling gap
**The bug:** `GetPageAsync` has no try-catch at all. Network failures, DNS errors, timeouts, and JSON deserialization errors propagate as unhandled exceptions. Contrast with `SearchAsync` which has 3 properly logged catch blocks.
**Impact:** A transient network glitch during page fetch crashes the agent's tool dispatch loop.
**Recommendation:** Mirror the `SearchAsync` error handling pattern.

### P1-AG-005: TaskCanceledException swallowed as "Gateway timeout" — cancels caller cancellation
**File:** `MemorySmith.Agent/Agent.Memory/MemorySmithBlueprintRepository.cs` (line ~119, 149, 200) and `MemorySmithItemRegistry.cs` (line ~98, 103)
**Category:** Error handling gap
**The bug:** `TaskCanceledException` is caught and treated as "Gateway timeout" in 5 places, but `TaskCanceledException` also fires on genuine caller-initiated cancellation (via CancellationToken). This swallows cooperative cancellation.
**Impact:** If a caller cancels an operation (e.g., shutdown, timeout), the cancellation is silently converted to a null result. The agent continues as if the gateway timed out rather than acknowledging the cancellation request.
**Recommendation:** Use the two-catch pattern from `RestMemoryGateway.SearchAsync`: catch `OperationCanceledException when ct.IsCancellationRequested → throw`, catch `TaskCanceledException → log timeout`.

### P1-AG-006: chatFilter.js referenced but never created (AG-001)
**File:** `MemorySmith.Agent/MineflayerAdapter/chatFilter.js` — **DOES NOT EXIST**
**Category:** Missing module
**The bug:** `AGENTS.md` documents `chatFilter.js` as "referenced but never created." Grep across both repos confirms zero references to "chatFilter" anywhere in the codebase. The file does not exist.
**Impact:** Chat events from server admins and system messages reach the LLM pipeline unfiltered (only filtered by the `SYSTEM_MESSAGE_PATTERNS` regex list). Vulnerable to server-echo injection attacks.
**Recommendation:** Create `chatFilter.js` per documented contract, or update AGENTS.md to remove the reference if filtering is handled differently.

### P1-AG-007: craft and smelt handlers do not check _stopRequested
**File:** `MemorySmith.Agent/MineflayerAdapter/index.js` (line ~1342–1428 craft, ~1430–1500 smelt)
**Category:** Missing stop guard
**The bug:** Neither craft nor smelt handlers check `_stopRequested` at any point. Both can be long-running (craft: pathfinding + crafting; smelt: 40-second timeout). A stop signal during craft/smelt is silently ignored until the operation completes.
**Impact:** Emergency stop cannot interrupt craft/smelt operations. The bot continues crafting for up to 40 additional seconds after a stop command.
**Recommendation:** Add periodic `if (_stopRequested) return` checks in both handlers, particularly before and after `bot.pathfinder.goto()` calls.

### P1-MS-001: MemoryScorer weights sum to 1.23 (not 1.0)
**File:** `MemorySmith/MemorySmith.Core/StateMachine/MemoryScorer.cs` (line ~8–14)
**Category:** Bug — score normalization
**The bug:** Weights sum to 1.23 (0.63 + 0.3 + 0.2 + 0.1), not 1.0. A brand-new record with `UsageCount=0, Confidence=0, References=0, LastUpdated=now` scores 0.1, which is below `DeprecationThreshold=0.2` — so every new record is immediately deprecated on first triage cycle. Thresholds (Working=1.0, Core=2.0) are meaningless against an unbounded score.
**Impact:** The entire state machine (Deprecation/Working/Core thresholds) operates on incorrectly scaled scores. Every newly created memory is instantly deprecated on first maintenance cycle.
**Recommendation:** Normalize weights to sum to 1.0. Recalibrate threshold constants accordingly.

### P1-MS-002: Duplicate `[HttpPost("setup")]` route — AmbiguousActionException risk
**File:** `MemorySmith/MemorySmith.App/Controllers/AdminController.cs` (line ~51, 60)
**Category:** Duplicate route
**The bug:** Two `[HttpPost("setup")]` methods with identical route template but different `[Consumes]` attributes. ASP.NET Core may throw `AmbiguousActionException` at runtime when the JSON consumer is matched first.
**Impact:** Startup or runtime failure on the `/setup` endpoint. Users cannot complete initial admin setup.
**Recommendation:** Use distinct route templates or consolidate into a single method with content-negotiation.

### P1-MS-003: `DisableAsync` has no transaction — concurrent write race
**File:** `MemorySmith/MemorySmith.Storage/SqliteMemorySmithDatabase.cs` (line ~255–270)
**Category:** Error handling / race condition
**The bug:** `DisableAsync` loads user via `GetByIdAsync` (connection A), mutates in-memory, then calls `UpdateAsync` (connection B). No transaction wraps the two operations — a concurrent write between them can silently race. No audit log entry is recorded.
**Impact:** Concurrent disable operations can race, and there's no audit trail of who disabled which user and when.
**Recommendation:** Wrap load+mutate+save in a transaction. Add audit log entry.

### P1-MS-004: Silent catch in `FileMemoryStore.Save()` — temp file cleanup failure
**File:** `MemorySmith/MemorySmith.Storage/FileMemoryStore.cs` (line ~108–117)
**Category:** Error handling / logging gap
**The bug:** Temp file cleanup failure in `Save()` is silently swallowed — violates AGENTS.md Rule E-3 ("Never Swallow Exceptions or Drop Events Silently").
**Recommendation:** Add `LogWarning` at minimum when temp file cleanup fails. Consider fallback rename strategy.

### P1-MS-005: Silent catch in `FileEventStore.GetEvents()` — malformed JSON lines silently skipped
**File:** `MemorySmith/MemorySmith.Storage/FileEventStore.cs` (line ~63–82)
**Category:** Error handling / observability
**The bug:** Two silent catch blocks: malformed JSON lines silently skipped (line 77), and file read errors return `Enumerable.Empty<>()` (line 82). No diagnostic recording, no logging, no corruption tracking.
**Impact:** Corrupted event records are silently dropped. Auditors cannot detect tampering or storage corruption.
**Recommendation:** Log at `LogWarning` for malformed lines. Log at `LogError` for file read failures. Track corruption count via `StorageDiagnostics`.

### P1-DATA-001: PascalCase field violation in legacy task records
**File:** `MemorySmith.Agent/Data/Tasks/tsk-0001-*.json` (and ~50+ other legacy files)
**Category:** Task schema violation
**The bug:** Legacy task records use PascalCase (`Id`, `Key`, `Title`, `Status`) instead of the mandated camelCase (`id`, `key`, `title`, `status`). These fail `Test-TaskRecords.ps1` validation.
**Recommendation:** Bulk-migrate legacy PascalCase records to camelCase. Run validation script to confirm compliance.

### P1-DATA-002: Orphan `.md` task files remain on disk
**File:** `MemorySmith.Agent/Data/Tasks/TSK-0100.md`, `TSK-0101.md`, `TSK-0105.md`, `TSK-0125-per-block-checkpoint.md`, `TSK-0126-verification-pass-band-aid-reversion.md`, `TSK-0127-fact-lifecycle-infrastructure.md`, `TSK-0128-timeout-event-latency-mismatch.md`, and others
**Category:** Task schema violation
**The bug:** At least 8+ orphan `.md` files remain in `Data/Tasks/` despite being "converted" to JSON. The `Test-TaskRecords.ps1` validator checks for orphan `.md` files.
**Recommendation:** Delete or archive orphan `.md` files after confirming their content has been migrated to the corresponding `.json` records.

### P1-DATA-003: Missing required fields in task records
**File:** `MemorySmith.Agent/Data/Tasks/tsk-0144-*.json` and others
**Category:** Task schema violation
**The bug:** Task `tsk-0144` is missing `createdAtUtc`, `updatedAtUtc`, and `revision` — all listed as required in `AGENTS.md` schema contract. Also uses `estimatedHours` (non-standard field).
**Recommendation:** Add missing required fields. Remove non-standard fields unless documented in schema.

---

## P2 — High (Selected Key Findings)

| ID | File | Finding |
|----|------|---------|
| P2-01 | `Agent.Core/Models/ActionQueue.cs:118` | `ClearAndEnqueueAsync` silently discards stopCallback exceptions (`_ = ex;`) — no logging, violating Rule E-3 |
| P2-02 | `Agent.Core/Events/WorldEvents.cs:108` | Stale XML doc — `ItemCraftedEvent`/`ItemConsumedEvent` still labeled as "stubs" but are fully wired |
| P2-03 | `Agent.Core/Models/WorldState.cs:18` | `WorldState.Inventory`/`Facts` have `init` setters but are mutable Dictionaries — treated as immutable in comments but aren't |
| P2-04 | `Agent.Core/Runtime/IAgentRuntimeComponent.cs` | 6 runtime interfaces defined (`IIntentManager` etc.) but ZERO concrete implementations — only marker records exist |
| P2-05 | `Agent.Planning/Goals/SmeltGoal.cs:63` | `HasFailed` always returns `false` — if smelting cannot complete, goal loops forever with no backstop |
| P2-06 | `Agent.Planning/Goals/GenericGatherGoal.cs:70` | `HasFailed` checks two fact keys that are never written — dead code lulling readers into thinking failure detection works |
| P2-07 | `Agent.Planning/HtnPlanner.cs:234` | `ReplanAsync` creates `SimpleGoal` with `_ => false` for `IsComplete` — replanned goal can never complete through standard IGoal interface |
| P2-08 | `Agent.Planning/HtnPlanner.cs:190` | `CreateCreativeBuildActions` is a private method never called — orphaned 26-line method |
| P2-09 | `Agent.Planning/Llm/OpenAICompatibleProvider.cs:65` | Hardcoded `MaxTokens = 512` ignores `options.LlmMaxResponseTokens` — OllamaProvider respects config |
| P2-10 | `Agent.Planning/Goals/PlaceBlockGoal.cs:87` | `HasFailed` triggers on empty inventory even in creative mode — no `IsCreativeMode` guard |
| P2-11 | `Agent.Planning/Llm/LlmChatInterpreter.cs:534` | `ParseDecision` wraps entire JSON parsing in single catch-all — masks programming errors |
| P2-12 | `Agent.Planning/Llm/LlmEvaluatorImpl.cs:94` | `EvaluationResult` constructed with init-only property setters — uses object initializer on non-init properties |
| P2-13 | `Agent.Tools/Tools/*.cs` (7 files) | `InputSchema` getter calls `JsonDocument.Parse(...).RootElement` on every access — no caching (AT-5) |
| P2-14 | `Agent.Tools/Tools/SearchMemoryTool.cs:85` | Throws `ArgumentException` instead of returning `ToolResult` — anti-pattern (AT-8) |
| P2-15 | `Agent.Tools/Tools/QueryBlocksTool.cs:102` | Throws `ArgumentException` for missing args (AT-9) |
| P2-16 | All 13 physical-world tools | No tool-level timeout — all rely on ABS-level timeout (~30s) (AT-13) |
| P2-17 | `Agent.Memory/RestMemoryGateway.cs:73` | `GetPageAsync` returns null silently for all non-success HTTP codes — can't distinguish 404 from 500 |
| P2-18 | `Agent.Memory/MemorySmithItemRegistry.cs:124` | Search-fallback gate uses `content is null` instead of `IsNullOrWhiteSpace` — copy-paste drift from BlueprintRepository |
| P2-19 | `Agent.Memory/MemorySmithBlueprintRepository.cs:186` | `BlueprintParser.Parse(content)` called without try-catch in 3 locations — malformed blueprint crashes caller |
| P2-20 | `MineflayerAdapter/stopState.js` | Extracted but never imported by index.js — completely orphaned (AG-002) |
| P2-21 | `MineflayerAdapter/index.js` (7 goto sites) | All `bot.pathfinder.goto()` calls lack timeout parameters — known issue TSK-0159 (AG-004) |
| P2-22 | `MineflayerAdapter/index.js:376` | `playerCollect` handler uses simplified metadata pattern differing from documented safe fallback (AG-005) |
| P2-23 | `Agent.World.Minecraft/MinecraftAdapter.cs:61` | `DisconnectAsync` has silent catch on `kill -TERM` (Windows — always throws) with no logging (AG-007) |
| P2-24 | `Agent.World.Minecraft/WebSocketBridge.cs:176` | WebSocket reconnect uses fixed 5s delay — no exponential backoff (AG-008) |
| P2-25 | `MineflayerAdapter/index.js:73` | `_stopRequested` not reset in craft, smelt, wander — survives across reconnections (AG-009) |
| P2-26 | `MineflayerAdapter/index.js:1448` | smelt handler does NOT reset `_stopRequested` before execution (AG-013) |
| P2-27 | `WebUI.Blazor/Managers/DashboardPublisherImpl.cs:95` | `QueuedActions` hardcoded to 0 — Sprint 40+ never wired |
| P2-28 | `WebUI.Blazor/AgentBackgroundService.cs:610` | `DeniedCommands` property creates new `HashSet` on every get — hot path allocation |
| P2-29 | `WebUI.Blazor/AgentBackgroundService.cs:710` | Silent catch in reconnection `finally` block — violates Rule E-3 |
| P2-30 | `WebUI.Blazor/Managers/IntentManagerImpl.cs:24` | `DefaultOnlinePlayers = 1` hardcoded — Sprint 40 never wired to `IWorldAdapter` |
| P2-31 | `WebUI.Blazor/Managers/RecoveryManagerImpl.cs:25` | Stub — always returns false; recovery still in ABS |
| P2-32 | `WebUI.Blazor/AgentBackgroundService.cs:1750` | `_preDispatchSnapshot` is single field, not per-action — observe→evaluate uses stale snapshot for concurrent places |
| P2-33 | `WebUI.Blazor/Program.cs:250` | Duplicate DeniedCommands normalization — inline + PostConfigure do identical work |
| P2-34 | `WebUI.Blazor/Dashboard/DashboardHubEvents.cs:14` | `ChatReceived`/`GoalUpdated` constants are dead code — never referenced by any `SendAsync` |
| P2-35 | `MemorySmith.Core/Services/ChatServices.cs:1-3279` | ~3,279-line monolith violating SRP (MS-03) |
| P2-36 | `MemorySmith.App/Controllers/SearchController.cs:31` | `OrderByDescending(result => result.Score ?? 0)` — pages with null Score and memories with Score=0 are indistinguishable |
| P2-37 | `MemorySmith.App/Controllers/OAuthBridgeController.cs:78` | `ReadBodyAsync` reads entire body with no size limit — DoS vector (MS-005) |
| P2-38 | `MemorySmith.App/Controllers/HealthController.cs:34` | Readiness check reads entire event log under write lock for single Take(1) (MS-006) |
| P2-39 | `MemorySmith.App/Hosting/MemorySmithStorageSetup.cs:25` | Config path binding bypasses `IOptions<MemorySmithOptions>` (MS-007) |
| P2-40 | `MemorySmith.Storage/SqliteMemorySmithDatabase.cs:695` | `ApplyPendingMigrationsAsync` has no rollback on failure (MS-011) |
| P2-41 | `MemorySmith.App/Hosting/MemorySmithChatSetup.cs:38` | ChatProvider registration order is undefined — `_providers[0]` may not be intended default (MS-012) |
| P2-42 | `MemorySmith.Agent.Tests/Sprint20Tests.cs:252` | Reflection-based fragile pattern — private method invocation via `BindingFlags.NonPublic` (T-04) |
| P2-43 | `MemorySmith.Tests/McpAndSemanticSearchTests.cs` | 3 permanently `[Ignore]`-d tests with no associated task records (T-03) |
| P2-44 | `MemorySmith.Tests/SemanticEmbeddingPathTests.cs:22` | Global state mutation via `Directory.SetCurrentDirectory()` — fragile SetUp/TearDown (T-10) |
| P2-45 | `MemorySmith.Agent.Tests/AgentBackgroundServiceTests.cs:95` | Timing-dependent polling with `Task.Delay(10)` — known flakiness (T-11) |
| P2-46 | `MemorySmith.Training/synthetic/starter_sft.jsonl` | Training corpus extremely small (14 examples) — no write-operation examples (T-12) |
| P2-47 | `MemorySmith.Tests/MemoryApplicationServiceTests.cs` | No tests for page CRUD operations — FilePageService untested (T-13) |
| P2-48 | `MemorySmith.Benchmarks/MemorySmith.Benchmarks.csproj:8` | Stale BenchmarkDotNet version `0.15.8` — not a stable published version (T-08) |
| P2-49 | `MemorySmith.Tests/SearchBenchmarkTests.cs:150` | Throughput baselines use hardcoded timeouts with single-pass thresholds — no warm-up, no statistics (T-14) |
| P2-50 | No `.github/dependabot.yml` in either repo | No Dependabot — automated vulnerability alerts absent (10-05) |
| P2-51 | No CodeQL/security scanning in either CI | No CodeQL, no secret scanning (10-06) |
| P2-52 | No `.editorconfig` in either repo | Formatting drift across agent/human contributors (10-07) |
| P2-53 | Agent CI runs task validation AFTER build+test | If build/test fails, task validation never runs (10-08) |
| P2-54 | Agent CI missing memory/page validation steps | Unlike base repo CI which runs 4 validation steps (10-08) |
| P2-55 | `AGENTS.md` contains Minecraft-specific code examples | Not relevant to MemorySmith product work (10-09) |
| P2-56 | `tsk-0281` stale Backlog for 29 days | CI lint for data JSON well-formedness — surfaced by actual corrupted records (10-12) |

---

## P3 — Low / Observability (Selected Representative Findings)

| ID | File | Finding |
|----|------|---------|
| P3-01 | `Agent.Core/Models/WorldModel.cs:175` | `PredictPlace` comment says "inventory unchanged (consumed by action)" — self-contradictory |
| P3-02 | `Agent.Core/Models/AgentJournal.cs:19` | `All` materializes entire 1000-entry queue reversed on every access — O(n) allocation |
| P3-03 | `Agent.Core/Models/WorldModel.cs:30` | Thread safety fragility — `_belief` read under lock then used lock-free in static predictors |
| P3-04 | `Agent.Planning/Llm/LlmContextLogger.cs:97` | GZip stream saved with `.zip` extension — standard zip utilities cannot open |
| P3-05 | `Agent.Planning/IntentManager.cs:9` | Stray `z` character in XML doc comment typo |
| P3-06 | `Agent.Planning/Goals/SurviveNightGoal.cs:8` | `CriticalHealthThreshold = 4` is hardcoded named constant but not configurable |
| P3-07 | `Agent.Planning/ChatHistory.cs:50` | CAS loop has no backoff or retry limit — unbounded spin under contention |
| P3-08 | `Agent.Planning/OllamaLlmClient.cs` | Legacy OllamaLlmClient.cs exists alongside newer Llm/OllamaProvider.cs |
| P3-09 | `Agent.Tools/ToolDispatcher.cs:178` | `RegisteredNames` materializes new sorted list on every access (AT-4) |
| P3-10 | `Agent.Tools/Tools/MoveToTool.cs:36` | No coordinate range validation — x/y/z:999999 dispatches absurd navigation target (AT-10) |
| P3-11 | `Agent.Tools/ToolDispatcher.cs:239` | `ValidateAgainstSchema` silently accepts unknown JSON Schema type keywords (AT-11) |
| P3-12 | `Agent.Tools/Tools/ChatTool.cs:53` | Message truncation at 256 chars is silent — no log warning (AT-12) |
| P3-13 | `Agent.Construction/BlueprintExecutor.cs:27` | Magic string `"place"` instead of `ActionProtocol.Place` (AT-14) |
| P3-14 | `Agent.Construction/BlueprintExecutor.cs:33` | No try/catch or error handling — malformed ActionData passes silently (AT-15) |
| P3-15 | `Agent.Memory/LocalKnowledgeResolver.cs:69` | Assumes MemorySmith search scores are [0,1] — BM25/distance scores may exceed range |
| P3-16 | `Agent.Memory/MemorySmithItemRegistry.cs:38` | TTL cache has no invalidation mechanism — stale entries persist until expiry |
| P3-17 | `Agent.Vision/WorldVision.cs:1` | No interface, no DI registration — thin wrapper with no abstraction for testing |
| P3-18 | `MineflayerAdapter/creativeProvider.js:24` | `_nextSlotIndex` module-level state not reset across reconnections (AG-006) |
| P3-19 | `MineflayerAdapter/index.js:306` | Two separate reconnection mechanisms fight each other (AG-011) |
| P3-20 | `MineflayerAdapter/creativeProvider.js:1` | `require()` (CommonJS) in ESM project via `createRequire` shim (AG-012) |
| P3-21 | `Agent.World.Minecraft/WebSocketBridge.cs:193` | WebSocket close reason/status discarded — no diagnostic info on why connection closed (AG-014) |
| P3-22 | `Agent.World.Minecraft/WebSocketBridge.cs:158` | Reconnection doesn't re-send handshake when secret is null — no auth-detection mechanism (AG-015) |
| P3-23 | `WebUI.Blazor/wwwroot/index.html:514` | Legacy `StatusUpdated` listener is dead code — migration shim from Sprint 5A |
| P3-24 | `WebUI.Blazor/AgentBackgroundService.cs:1280` | `default` switch branch logs at `Debug` — genuinely missing handlers invisible in production |
| P3-25 | `WebUI.Blazor/AgentBackgroundService.cs:3570` | Three SignalR push methods catch at `LogDebug` (not `LogWarning`) + fire-and-forget means catches never execute |
| P3-26 | `WebUI.Blazor/AgentHub.cs:1` | No logging on connect/disconnect — impossible to diagnose SignalR connection health from logs |
| P3-27 | `.bak files` (16 total across both repos) | Stale backup files: Agent.Core (8), WebUI.Blazor (8 in Managers/), Agent.Tools (4), Tests (6) |
| P3-28 | `MemorySmith.App/Program.cs:52` | `CancellationToken.None` during startup initialization — can't cancel on shutdown |
| P3-29 | `MemorySmith.Bridge/Program.cs` | No timeout configured on HttpClient — MCP endpoint hang blocks indefinitely |

---

## Architecture Notes

### AgentRuntime Decomposition — Still Incomplete
The Sprint 36 decomposition produced 6 runtime interfaces (`IIntentManager`, `IPlanningManager`, `IExecutionManager`, `IRecoveryManager`, `IStateManager`, `IDashboardPublisher`) and the `AgentRuntime` data record. However, zero concrete implementations exist in `Agent.Core/Runtime/` — only marker records. The actual runtime logic remains in `AgentBackgroundService.cs` (~3,600 lines). This architectural decomposition remains aspirational.

### Dual SignalR Write Surface — Data Race Risk
`AgentBackgroundService` pushes SignalR updates inline, and `DashboardPublisherImpl` exists as a parallel publisher. Neither is the single source of truth. `DashboardPublisherImpl` has hardcoded values (QueuedActions=0, OnlinePlayers=1, Blueprints=[1 entry]) and is never called from the ABS main loop. This dual surface risks inconsistent dashboard state.

### .bak File Accumulation (16 files)
Stale `.bak` files remain across both repos: Agent.Core (8), WebUI.Blazor/Managers/ (6), Agent.Tools (4), Tests (6). Some contain base64-encoded content (encoding mishap artifacts from AGENTS.md Rule E-2 violations). These should be cleaned up systematically.

---

## Methodology

1. Phase 1: Gathered context — repo memory, roadmap, AGENTS.md, git history (30 commits each repo)
2. Phase 2: Partitioned the codebase into 10 independent areas:
   - Agent 1: Agent.Core (Models, Events, Interfaces, Runtime)
   - Agent 2: Agent.Planning + Agent.Personality
   - Agent 3: Agent.Tools + Agent.Construction
   - Agent 4: Agent.Memory + Agent.Vision
   - Agent 5: Agent.World.Minecraft + MineflayerAdapter
   - Agent 6: WebUI.Blazor (Program.cs, ABS, Dashboard, Options)
   - Agent 7: MemorySmith.Core
   - Agent 8: MemorySmith.Storage + App + Bridge
   - Agent 9: Tests + Training + Benchmarks (both repos)
   - Agent 10: Data + Scripts + CI/CD + Docs + Config (both repos)
3. Launched 10 parallel subagents with structured output format requirements
4. Merged and deduplicated ~144 findings from all agents
5. Categorized by severity (P1/P2/P3) and domain
6. Pending: 6-chair council peer review (Phase 4)

---

## Peer Review Results (added after Phase 4 — 6-Chair Council)

**Date:** 2026-07-10
**Methodology:** 6 independent council seats via heterogeneous subagent swarm. Each seat received the audit report and instructions to verify claims against source code. Results merged and reconciled below.

### Council Seat Summary

| Seat | Recommendation | Confidence | Blocking Concern |
|------|---------------|:----------:|-----------------|
| **Source-Grounded Archivist** | 12 of 16 P1 findings confirmed; 1 partially confirmed; 3 overclaimed. | 95% | P1-AG-005 scope overstated — BlueprintRepository is correct; only ItemRegistry vulnerable |
| **Data Model Architect** | 3 upgrades (P2→P1). MemoryScorer is P0 (root cause deeper). Runtime decomposition is P1. PageSummary Score gap is P1. | 95% | MemoryScorer scoring model is unbounded — entire state machine broken |
| **Retrieval Specialist** | 1 upgrade (P2→P1 for PageSummary Score). 1 downgrade (P2→P3 for search-fallback gate). RRF scores so small they zero out wiki search path. | 85% | LocalKnowledgeResolver wiki search path is effectively dead weight |
| **Human Learning Advocate** | ChatServices monolith and task schema violations are top developer-frustration items. No severity changes. | 90% | Root cause pattern: deferred decomposition across 7 findings |
| **Skeptical Reviewer** | 2 P1 findings WRONG. 4 P1 findings over-severity (should be P2). 6 P1 findings confirmed. Overall confidence: 72% | 72% | P1-AG-006 (chatFilter.js) is dead wrong — file was intentionally deleted. P1-MS-002 (duplicate route) is wrong — ASP.NET Core supports [Consumes] disambiguation |
| **Synthesizer** | Proposed Wave C split (C1+C2) to avoid scope creep in Sprint 60. | 85% | Wave C has too many P1 findings for one sprint wave |

### Severity Recalibrations

| Finding | Initial Severity | Council Severity | Reason |
|---------|:---------------:|:----------------:|--------|
| P1-AG-001: PlaceBlock coords | P1 | **P1** ✅ | Confirmed — genuine correctness bug |
| P1-AG-002: CreatePageTool throws | P1 | **→ P2** | ToolDispatcher outer catch converts to ToolResult; end-user impact identical |
| P1-AG-003: SignalR drift | P1 | **P1** ✅ | Real drift; dashboard listens for both names but fragile |
| P1-AG-004: GetPageAsync no try-catch | P1 | **→ P2** | All existing callers wrap in own try-catch; no crash path in current code |
| P1-AG-005: TaskCanceledException | P1 | **→ P2** | Scope overstated (5→3 sites); BlueprintRepository already correct; only ItemRegistry vulnerable |
| P1-AG-006: chatFilter.js missing | P1 | **REMOVED** | File was intentionally deleted in Sprint 56 (TSK-0279). Finding is factually wrong |
| P1-AG-007: craft/smelt stop guard | P1 | **→ P2** | handleStop() cancels pathfinder for non-place/move; smelt has 40s timeout backstop |
| P1-MS-001: MemoryScorer weights | P1 | **→ P0** | Root cause deeper: scoring model is dimensionally incompatible and unbounded |
| P1-MS-002: Duplicate route | P1 | **REMOVED** | ASP.NET Core supports [Consumes] disambiguation natively |
| P1-MS-003: DisableAsync no transaction | P1 | **→ P2** | SQLite serializes writes; last-write-wins prevents corruption |
| P1-MS-004: FileMemoryStore silent catch | P1 | **→ P3** | Temp file cleanup in finally block; universal pattern, no recovery possible |
| P1-MS-005: FileEventStore silent catches | P1 | **→ P2** | Inner catch (malformed JSON) is legitimate design; outer catch should log |
| P1-DATA-001/002/003: Task schema | P1 | **P1** ✅ | Data integrity crisis confirmed; 26 orphan .md files (not 8) |
| **Upgrades:** | | | |
| P2-04: Runtime interfaces 0 impls | P2 | **→ P1** | Connects 7 other findings under same root cause; deferred ~30 sprints |
| P2-36: PageSummary no Score | P2 | **→ P1** | Breaks unified search ranking entirely |
| P2-35: ChatServices monolith | P2 | **→ P1** | Same deferred-decomposition root cause as P2-04 |

### Final Severity Distribution

| Severity | Original | Recalibrated |
|----------|:--------:|:------------:|
| **P0** | 0 | 1 (MemoryScorer unbounded model) |
| **P1** | 16 | **9** (3 confirmed + 3 DATA + 3 upgrades) |
| **P2** | 63 | **70** (5 downgraded from P1 + 5 new P1s become P2 + 57 original P2) |
| **P3** | 65 | **66** (2 downgraded from P2 + 1 new) |

### Missing Task Tracking

The following P1 findings have no existing task coverage and require new MCP tasks:
- MemoryScorer weights root cause (P0 upgrade) — **new task needed**
- PlaceBlockGoalDecomposer same-coordinates bug (P1) — **new task needed**
- SignalR event name drift (P1) — **new task needed** (related to TSK-0326 which is closed)
- PageSummary Score property (P1 upgrade) — **new task needed**
- Runtime decomposition (P1 upgrade) — relates to existing TSK-0293
- ChatServices monolith (P1 upgrade) — **new task needed**

### Compensating Controls Noted

Several findings have compensating controls that reduce operational risk but don't eliminate the need for fixes:
- CreatePageTool throws → ToolDispatcher outer catch (P1→P2)
- GetPageAsync no try-catch → all callers wrap it (P1→P2)
- SignalR drift → dashboard listens for both legacy and canonical names
- craft/smelt stop → pathfinder cancellation + 40s timeout
- DisableAsync race → SQLite serializes writes
