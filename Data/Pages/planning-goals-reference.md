# MemorySmith.Agent — Planning & Goals Reference

**Last updated:** 2026-07-10
**Confidence:** 95% (verified against actual code after 12+ file reads across Agent.Planning, Agent.Core, Agent.Construction, WebUI.Blazor, and test projects)
**Source scope:** Deep-dive by Agent 4 of 10 in coordinated memory audit sweep

---

## Tags

`planning` `goals` `decomposers` `htn` `replan` `intent-manager` `llm-integration` `action-outcome` `build-facts` `consecutive-failure` `pipeline` `agent-4-audit`

---

## 1. Goal Hierarchy

**Source:** `Agent.Core/Interfaces/IGoal.cs` + `Agent.Planning/Goals/` (7 files) + `Agent.Core/Models/TaskSequenceGoal.cs`

All goals implement `IGoal` (namespace `Agent.Core`):

| Interface/Type | File | Key Properties | Purpose |
|---|---|---|---|
| `IGoal` | `Agent.Core/Interfaces/IGoal.cs` | Id, Name, Description, Phases, FailureReason, DamageInterruptThresholdHp, IsComplete, HasFailed | Base interface |
| `IBuildGoal` | `Agent.Planning/Goals/IBuildGoal.cs` | Blueprint, Blocks, Origin, HasExplicitOrigin | Marker interface for build goals; replaces fragile `is BuildGoal` type checks |
| `IItemSpecGoal` | `Agent.Core/Interfaces/IItemSpecGoal.cs` | Spec (ItemSpec), TargetCount (DIM default=1) | Item-count goal interface |
| `IGoalPrecondition` | `Agent.Core/Interfaces/IGoalPrecondition.cs` | CanAttempt(ExecutionContext) | Pre-flight feasibility check (Sprint 58 TSK-0310) |

**Concrete goals:**

| Goal Type | File | Completes When | Fails When |
|---|---|---|---|
| `BuildGoal` | `Agent.Planning/Goals/BuildGoal.cs` | Fact `goal:Build:{blueprintId}:complete` = true | Fact `goal:Build:{blueprintId}:failed` = true |
| `GenericGatherGoal` | `Agent.Planning/Goals/GenericGatherGoal.cs` | Inventory count of item/sourceBlocks >= targetCount | Fact `goal:Gather:{itemId}:{targetCount}:failed` or legacy `goal:Gather:{itemId}:failed` |
| `CraftItemGoal` | `Agent.Planning/Goals/CraftItemGoal.cs` | Inventory[itemId] >= count | Fact `goal:CraftItem:{itemId}:failed` = true |
| `SmeltGoal` | `Agent.Planning/Goals/SmeltGoal.cs` | Inventory[OutputItem] >= count | **Always returns false** — no write site exists for failure facts |
| `GatherWoodGoal` | `Agent.Planning/Goals/GatherWoodGoal.cs` | Any `*_log` variant in inventory >= targetCount | Fact `goal:GatherWood:failed` = true |
| `PlaceBlockGoal` | `Agent.Planning/Goals/PlaceBlockGoal.cs` | Volatile.Read(ref _dispatched) >= count | Inventory[item] <= 0 AND !IsInventoryStale |
| `SurviveNightGoal` | `Agent.Planning/Goals/SurviveNightGoal.cs` | IsDaytime(state) OR IsInShelter(state) | Health <= 4 HP |
| `TaskSequenceGoal` | `Agent.Core/Models/TaskSequenceGoal.cs` | All steps complete; delegates to current step's IsComplete | Current step's HasFailed |
| `SmeltGoal` (Sprint 44 TSK-0079) | `Agent.Planning/Goals/SmeltGoal.cs` | Inventory[OutputItem] >= count | Always false (consecutive-failure counter used instead) |

**Key design notes:**
- `BuildGoal.Origin` consolidated into `BuildOrigin` value object (`Agent.Planning/Goals/BuildOrigin.cs`) — atomic (all or nothing), no sentinel overloading, `Source` enum tracks provenance (Explicit, PlayerPosition, AutoScanned). TSK-0103.
- `GenericGatherGoal.Id` = `Guid.NewGuid()` per instance (Sprint 39). `IsComplete` guards on `IsInventoryStale` (Sprint 40 P0-B: removed creative-mode auto-complete shortcut).
- `CraftItemGoal` guards on `IsInventoryStale` (Sprint 22 P0).
- `PlaceBlockGoal._dispatched` uses `Interlocked` for thread safety (Sprint 58 Wave D TSK-0330).
- `TaskSequenceGoal.MaxSteps` = 5. `TryAdvance()` advances on current step completion. Sprint 56 TSK-0274 fixed circular dependency where sequence could never complete.

---

## 2. Planning Pipeline

**Source:** `Agent.Planning/Interfaces/IPlanner.cs`, `Agent.Planning/Router/PlannerRouter.cs`, `WebUI.Blazor/Program.cs`

```
Chat → IChatInterpreter.InterpretAsync → IntentDraft
  → AgentBackgroundService / IntentManager.BuildGoalRequest → GoalRequest
    → GoalFactory.CreateAsync → IGoal
      → IPlanner.PlanAsync → ActionPlan (ActionData[])
        → enqueue → dispatch → monitor → complete/fail → replan
```

### DI Registration Order (Planning Components)

**Source:** `WebUI.Blazor/Program.cs` lines 80-390

1. `GoalFactory` / `IGoalFactory` — singleton
2. `IntentManager` — singleton (Sprint 37 P1-A)
3. `ChatInterpreter` — singleton (deterministic fallback)
4. `LlmChatInterpreter` / `IChatInterpreter` — singleton, injects IntentManager + tool descriptions
5. `ToolDispatcher` — for LLM fallback
6. `ActionRegistry` — from ToolDispatcher.All (Sprint 57)
7. `HtnTaskLibrary` — singleton
8. `HtnPlanner` — singleton, with ILlmProvider + ToolDispatcher for LLM fallback
9. `DecomposerRegistry` — ordered registration (order matters):
   1. `BuildGoalDecomposer` (matches IBuildGoal)
   2. `PlaceBlockGoalDecomposer` (matches PlaceBlockGoal)
   3. `GatherGoalDecomposer` (matches GatherWoodGoal OR IItemSpecGoal)
   4. `SurviveNightGoalDecomposer` (matches SurviveNightGoal)
   5. `CraftItemGoalDecomposer` (matches CraftItemGoal)
   6. `SmeltGoalDecomposer` (matches SmeltGoal)
   7. `TaskSequenceGoalDecomposer` (matches TaskSequenceGoal)
10. `PlannerRouter` / `IPlanner` — singleton, routes through decomposer registry first, falls back to HtnPlanner
11. `IReplanGovernor` → `ReplanGovernor(identicalPlanThreshold: 5)` — singleton
12. `ILlmEvaluator` → `LlmEvaluatorImpl` — singleton (Sprint 39 P1)

### PlannerRouter Routing

**Source:** `Agent.Planning/Router/PlannerRouter.cs`

`PlannerRouter.Select(goal, state)`:
1. **Check DecomposerRegistry** — returns `DecomposerPlanner` wrapping first `IGoalDecomposer` where `CanHandle` returns true
2. **Fallback to HtnPlanner** — pure phase-by-phase fallback with optional LLM fallback

`PlannerStrategy` enum has 4 values but only 2 are implemented:
- `GoalDecomposer` — [IMPLEMENTED] via DecomposerRegistry
- `Htn` — [IMPLEMENTED] final fallback
- `Goap` — [ASPIRATIONAL] not wired
- `LlmAssisted` — [ASPIRATIONAL] not wired

### GoalFactory.CreateAsync Routing

**Source:** `Agent.Planning/GoalFactory.cs`

Prefix-based routing for async goal creation:
- `GatherItem:{itemId}` → ItemRegistry lookup → fallback to `TryMakeBuiltInSpec` → `GenericGatherGoal`
- `Build:{blueprintId}` → BlueprintRepository → `BlueprintParser.Parse` → `BuildGoal`
- `CraftItem:{itemId}` → `CraftItemGoal`
- `SmeltItem:{inputItem}` → `SmeltGoal` (Sprint 44 TSK-0079)
- `PlaceBlock:{item}` → `PlaceBlockGoal` (Sprint 54)

Synchronous goals (via `Create`):
- `GatherWood` → `GatherWoodGoal(count)`
- `SurviveNight` → `SurviveNightGoal()`

---

## 3. Decomposer System

**Source:** `Agent.Planning/Decomposition/` (7 files), `Agent.Planning/HtnTaskLibrary.cs`

### DecomposerRegistry

**Source:** `Agent.Planning/Decomposition/DecomposerRegistry.cs`

- Thread-safe (lock on `_decomposers` list)
- `Register(IGoalDecomposer)` — adds to list
- `Find(IGoal)` — returns first matching decomposer via `CanHandle`
- `All` — snapshot of all registered decomposers

### Decomposer Details

| Decomposer | File | CanHandle | Delegates To |
|---|---|---|---|
| `BuildGoalDecomposer` | `Decomposition/BuildGoalDecomposer.cs` | `IBuildGoal` | `HtnTaskLibrary.DecomposeBuild()` |
| `PlaceBlockGoalDecomposer` | `Decomposition/PlaceBlockGoalDecomposer.cs` | `PlaceBlockGoal` | Direct PlaceBlock actions |
| `GatherGoalDecomposer` | `Decomposition/GatherGoalDecomposer.cs` | `GatherWoodGoal` OR `IItemSpecGoal` | `HtnTaskLibrary.DecomposeGatherItem()` |
| `SurviveNightGoalDecomposer` | `Decomposition/SurviveNightGoalDecomposer.cs` | `SurviveNightGoal` | `HtnTaskLibrary.Decompose("SurviveNight")` |
| `CraftItemGoalDecomposer` | `Decomposition/CraftItemGoalDecomposer.cs` | `CraftItemGoal` | `HtnTaskLibrary.DecomposeCraftItem()` |
| `SmeltGoalDecomposer` | `Decomposition/SmeltGoalDecomposer.cs` | `SmeltGoal` | `HtnTaskLibrary.DecomposeSmeltItem()` |
| `TaskSequenceGoalDecomposer` | `Decomposition/TaskSequenceGoalDecomposer.cs` | `TaskSequenceGoal` | Registry.Find(current step) |

**Ordering constraint:** `GatherGoalDecomposer` must be registered BEFORE `CraftItemGoalDecomposer` in the registry because `PlaceBlockGoal` does NOT implement `IItemSpecGoal` — if it did, `GatherGoalDecomposer` would hijack it. `PlaceBlockGoalDecomposer` must be registered before `GatherGoalDecomposer`.

### HtnTaskLibrary — Method Catalog

**Source:** `Agent.Planning/HtnTaskLibrary.cs`

**11 string-keyed task methods** (registered in constructor):
`GatherWoodDecompose`, `FindTreeDecompose`, `MineWoodDecompose`, `CollectDecompose`, `SurviveNightDecompose`, `FindShelterDecompose`, `LightAreaDecompose`, `WaitDecompose`, `WanderDecompose`, `ExploreDecompose`, `FindFlatAreaDecompose`

**Named entry points** (public methods, not in dictionary):
- `DecomposeBuild(blueprint, blocks, origin, state, requireOrigin)` — ~350 lines with creative/survival branching (lines 420-700)
- `DecomposeCraftItem(itemId, count, state)` — pre-gather + craft pipeline (lines ~200-380)
- `DecomposeSmeltItem(inputItem, count, state)` — pre-gather fuel + smelt (Sprint 44 TSK-0079)
- `DecomposeGatherItem(spec, parameters, state)` — wander (conditional) + MineBlock per source block (Sprint 38 P0-A: GetStatus removed)
- `BuildCraftingChain(blueprint, materials, state, hasTorch, torchNeeded)` — handles CraftingChainOrder items

### GatherGoalDecomposer — Auto-Tool Crafting (Sprint 54 TSK-0208)

**Source:** `Agent.Planning/Decomposition/GatherGoalDecomposer.cs`

Before calling `DecomposeGatherItem`, checks if a tool (pickaxe/axe/shovel) is required for the target blocks via `ToolRequirements.GetRequiredToolType(spec)`. If no suitable tool in inventory, inserts `DecomposeCraftItem` for the wooden-tier tool first.

---

## 4. DecomposeBuild — Creative/Survival Branching

**Source:** `Agent.Planning/HtnTaskLibrary.cs` lines 420-700+

### Origin Resolution (Sprint 35, TSK-0107)

Origin resolution priority:
1. **Explicit origin** from `BuildOrigin.Source == BuildOriginSource.Explicit` — coords used as-is
2. **Non-zero non-explicit** — caller resolved from facts, used verbatim
3. **Null or all-zero** — `ResolveAutoOrigin(state, ref x, ref y, ref z)` reads auto-origin facts from world state

### requireOrigin Guard (lines ~497+)

When `requireOrigin = true` and origin is (0,0,0) and source isn't Explicit:
- Reads `BuildFactKeys.LastFlatArea` fact to check if a flat area was found
- If `lastArea == 0`: checks `event:FlatAreaFound:SearchedRadius` fact
  - If `searchedRadius >= FlatAreaRetryRadius` (48): returns empty array → goal fails via consecutive-failures counter
  - Otherwise: emits `FindFlatArea(searchRadius=48, minFlatArea=25)`
- If no prior scan: emits `FindFlatArea(radius=30, minFlatArea=25)`

### Creative Path (state.IsCreativeMode == true)

- No material gathering, no tool ensures, no crafting chain
- `BlueprintExecutor.Execute()` → per-block status filtering (`BuildFactKeys.BlockStatus`) — skips already-placed blocks
- Self-position skip (TSK-0123): skip blocks at bot's current position
- No `MoveTo(origin)` — adapter navigates per-block via pathfinder.goto()
- Ends with `GetStatus`

### Survival Path (state.IsCreativeMode == false)

1. Material MineBlock loop: iterates `blueprint.Materials`, mines direct-mine blocks not yet in inventory
2. Torch coal pre-gather: if blueprint needs torches, mines coal_ore
3. Iron smelt: if blueprint needs iron_ingot, mines iron_ore + SmeltItem
4. `BuildCraftingChain`: iterates `CraftingChainOrder`, emits `CraftItem` for each needed material
5. `BlueprintExecutor.Execute()` → PlaceBlock loop (shared with creative)
6. Per-block status filtering, self-position skip
7. Ends with `GetStatus`

### Shared PlaceBlock Loop (after branch, lines ~630-700)

- `BlueprintExecutor.Execute(blocks, originX, originY, originZ)` — floor-first ordering
- Per-block status filter via `BuildFactKeys.BlockStatus(blueprint.Name, i)`
- Self-position skip (TSK-0123)
- Sets context keys `PlaceBlockProgressBlueprintId` and `PlaceBlockProgressBlockIndex`
- Ends with `GetStatus`

### BuildFactKeys

**Source:** `Agent.Core/BuildFactKeys.cs`

| Constant/Method | Pattern | Purpose |
|---|---|---|
| `AutoOriginX` | `build:auto:origin:x` | Auto-detected build origin X |
| `AutoOriginY` | `build:auto:origin:y` | Auto-detected build origin Y |
| `AutoOriginZ` | `build:auto:origin:z` | Auto-detected build origin Z |
| `BuildProgressIndex(id)` | `build:{id}:progress:index` | Legacy checkpoint key (TSK-0125 replaced with per-block) |
| `BlockStatus(id, i)` | `build:{id}:block:{i}:status` | Per-block placement status |
| `SkipReason(id, i)` | `build:{id}:block:{i}:skipReason` | Why a block was skipped |
| `BlockStatusPrefix(id)` | `build:{id}:block:` | Prefix for clearing all block facts |
| `PlaceBlockProgressBlueprintId` | `build:progress:blueprintId` | Context key on ActionData |
| `PlaceBlockProgressBlockIndex` | `build:progress:blockIndex` | Context key on ActionData |

**Block status values:** `pending` (default absent), `in-progress`, `placed`, `skipped`

---

## 5. Replan System

**Source:** `Agent.Core/IReplanGovernor.cs`, `Agent.Core/ReplanGovernor.cs`, `WebUI.Blazor/AgentBackgroundService.cs` lines 1250-1350

### ReplanGovernor — Implementation Details

**Source:** `Agent.Core/ReplanGovernor.cs`

- **Threshold:** Default `identicalPlanThreshold = 3`. DI registration uses `5` (set in `Program.cs`).
- **Plan fingerprint:** Computed by caller as `GoalName:Tool1,Tool2,Tool3` (goal key + ordered action type sequence). Parameters excluded.
- **Graduated stall recovery delays (Sprint 52):** `[5, 10, 20, 30]` seconds (reduced from `[10, 20, 30, 60]`).
- **Sprint 59 (TSK-0342) fix:** `CurrentStallDelay` and `TryAutoRecover` both use `_stallAttempt - 1` (clamped ≥ 0) so the first stall reads tier 0 = 5s. Auto-recovery does NOT reset `_stallAttempt` — only `RecordProgress`/`Reset` clears the counter, allowing backoff to escalate: 5→10→20→30→30s.
- **Thread-safe** — all state under `_lock`.

### Governor States

| State | Meaning | Entry | Exit |
|---|---|---|---|
| ACTIVE | Replanning allowed | Default | 3+ identical fingerprints → STALLED |
| STALLED | Replanning suppressed | 3 consecutive identical plans | RecordProgress, Reset, or graduated timeout elapsed |

### Recovery from STALLED
1. `RecordProgress()` — inventory or position change; resets counter and attempt level
2. `Reset()` — new goal or user command; clears all tracking
3. Graduated retry: auto-allows replan after `[5, 10, 20, 30]s` — attempt counter escalates

### Key Replan Facts (Production)

**Source:** `AgentBackgroundService.cs`

- `_currentGoal` is **NEVER replaced** during replan — original goal object preserved (line ~1358: `Planner.PlanAsync(_currentGoal, _worldState, ct)`)
- `_queue.Clear()` before re-enqueue to prevent stale action pileup
- `GenericGatherGoal.IsComplete()` correctly checks `inventory >= targetCount` — prevents over-gathering from overshoot planning
- `ReplanGoalContext` (in `Agent.Core/Models/ReplanGoalContext.cs`) is used extensively in tests but **never instantiated in production** — production loop calls `PlanAsync` directly, not `ReplanAsync`

### Overshoot Planning (Known Issue)

**Source:** `Agent.Planning/Decomposition/GatherGoalDecomposer.cs` line 40-56

`GatherGoalDecomposer.Decompose()` passes the FULL `TargetCount` to `DecomposeGatherItem`. If inventory has 60/64 logs, it still plans to gather 64 more logs. `IsComplete` gates prevent over-gathering, but unnecessary mine actions may be queued.

---

## 6. Consecutive Failure Guard

**Source:** `AgentBackgroundService.cs` lines 1281-1292, 1624-1699, 2224

| Property | Value | Notes |
|---|---|---|
| `_maxConsecutiveFailures` (constructor param) | 3 (default) | Increased from 3 in Sprint 41 post-fix |
| DI registration value | used from constructor default | Not explicitly set in Program.cs |

### Counter Behavior

**Reset (to 0) on:**
- `IsProgressSignalTool` returns true for successful tool: MineBlock, PlaceBlock, CraftItem, SmeltItem
- Lifecycle events: SetGoal, CancelGoal, goal completion, death, continue intent

**Increment on:**
- ALL tool failures EXCEPT Chat (line 1624-1699 switch)

**Abandonment on threshold exceeded (lines 1281-1292):**
- `_currentGoal = null`
- Clear queue
- Clear pending actions

**Game errors:** Separate escalation path — 2+ consecutive game errors → `TryRecoverFromGameErrorAsync`

---

## 7. IntentManager

**Source:** `Agent.Planning/IntentManager.cs`

### Intent → GoalRequest Mapping

| Intent | Condition | GoalRequest |
|---|---|---|
| `smelt` | Item non-null | `SmeltGoalRequest(item, count ?? 1)` |
| `gather` | Item non-null | `GatherGoalRequest(item, count ?? 10)` |
| `place` | Item non-null | `PlaceGoalRequest(item, count ?? 1, x, y, z)` |
| `craft` | Item non-null | `CraftGoalRequest(item, count ?? 1)` |
| `build` | Always (blueprint resolved) | `BuildGoalRequest(blueprint, origin)` |
| `navigate` | All coords non-null | `NavigateGoalRequest(x, y, z)` |
| Other | Always | null (no goal produced) |

### "make" Routing (Sprint 36)

The ChatInterpreter regex `\b(craft|forge|smelt|make)\b` routes "make planks" to CraftRegex, producing `ChatIntentType.CreateGoal` with intent="craft", item="planks". IntentManager then resolves through `BuildGoalRequest` which routes to `CraftGoalRequest` — separately from BuildRegex which uses `\b(build|construct)\b`.

### Blueprint Alias Resolution (Sprint 41)

`TryBuildGoal` resolves common blueprint names via `AliasRegistry.BlueprintAliases` (e.g. "house" → "small-house") so the LLM doesn't need exact internal IDs.

### Item Alias Resolution (Sprint 43 P1-1)

`ResolveItem()` merges ChatInterpreter player-shorthand mappings with IntentManager LLM-output normalization entries (e.g. "wool" → "white_wool", "planks" → "oak_planks").

### Command String Parsing (TSK-0205)

`ParseCommandString(command)` supports simple patterns for multi-step chaining:
- `craft N item` / `craft item` → CraftGoalRequest
- `gather/mine/get N item` → GatherGoalRequest
- `build blueprint` → BuildGoalRequest
- `place N item at X Y Z` / `place N item in front` → PlaceGoalRequest
- `move/go/navigate/walk to X Y Z` → NavigateGoalRequest
- `smelt N item` / `smelt item` → SmeltGoalRequest

### GoalRequest Hierarchy (Sprint 39 P3)

**Source:** `Agent.Planning/IntentManager.cs` (end of file)

- `abstract record GoalRequest(string GoalName)` — base with virtual `Parameters` property
- `GatherGoalRequest(string Item, int Count = 10)` → `GoalName = "GatherItem:{Item}"`
- `CraftGoalRequest(string Item, int Count = 1)` → `GoalName = "CraftItem:{Item}"`
- `BuildGoalRequest(string Blueprint, BuildOrigin? Origin = null)` → `GoalName = "Build:{Blueprint}"`
- `NavigateGoalRequest(int X, int Y, int Z)` → `GoalName = "MoveTo"`
- `SmeltGoalRequest(string InputItem, int Count = 1)` → `GoalName = "SmeltItem:{InputItem}"` (Sprint 44 TSK-0079)
- `PlaceGoalRequest(string Item, int Count = 1, int? X = null, int? Y = null, int? Z = null)` → `GoalName = "PlaceBlock:{Item}"` (Sprint 54)

---

## 8. LLM Integration

### Provider Implementations

**Source:** `Agent.Planning/Llm/`

| Provider | Class | Created by |
|---|---|---|
| Ollama | `OllamaProvider` | `LlmProviderFactory.Create("ollama")` |
| Anthropic | `AnthropicProvider` | `LlmProviderFactory.Create("anthropic")` |
| Gemini | `GeminiProvider` | `LlmProviderFactory.Create("gemini")` |
| OpenAI-Compatible | `OpenAICompatibleProvider` | `LlmProviderFactory.Create("openai")` |

### LLM Chat Pipeline

**Source:** `Agent.Planning/LlmChatInterpreter.cs`

```
ChatEvent → LlmChatInterpreter.InterpretAsync
  → 1. Truncate at MaxMessageLength
  → 2. Distance gate (far + not named → NotAddressed)
  → 3. Fast-path: cancel, status, inventory, help, navigate (never touch LLM)
  → 4. Rate-limit check
  → 5. BuildSystemPrompt (tools, chat history, safety config)
  → 6. ILlmProvider.CompleteAsync
  → 7. ParseDecision → IntentDraft (or TryParseTruncatedJson recovery)
  → 8. Confidence < threshold + ClarificationQuestion → Unknown + ask clarifying
  → 9. Fallback to pattern-matcher if LLM fails
```

**Sprint 34 fixes:**
- Added `ILogger<OllamaProvider>` with warning logging for all error types
- Added `"conversation"` intent to prompt and ParseDecision
- Increased thinking indicator delay from 1.5s to 5s to prevent server anti-spam kicks
- Added `LogInformation` when LLM is skipped (provider unavailable or rate-limited)

**Sprint 35 P1-B:** Removed fast-path for CreateGoal. Only cancel, status, help, navigate remain as fast-paths.
**Sprint 43 (P0-1):** Re-added navigate fast-path ("come here" is zero-risk).

### IntentDraft Schema

**Source:** `Agent.Core/Models/IntentDraft.cs`

```json
{
  "addressed": "yes" | "maybe" | "no",
  "intent": "gather" | "build" | "craft" | "smelt" | "place" | "navigate" | "cancel"
            | "status" | "help" | "conversation" | "clarify" | "ignore",
  "item": "<minecraft_id or null>",
  "blueprint": "<blueprint_id or null>",
  "count": <integer or null>,
  "x": <integer or null>, "y": <integer or null>, "z": <integer or null>,
  "confidence": <0.0–1.0>,
  "clarificationQuestion": "<question or null>",
  "response": "<in-game reply, max 50 words>",
  "nextSteps": ["<command strings for chaining>"]
}
```

**Key fields:**
- `addressed` — `"yes"`, `"maybe"`, or `"no"`
- `intent` — 12 allowed values (Sprint 44 added `"smelt"`, Sprint 54 added `"place"`)
- `confidence` — gates clarification flow: if `confidence < LlmConfidenceThreshold` (default 0.6) AND `clarificationQuestion` is non-empty → `ChatIntentType.Unknown`
- `nextSteps` — TSK-0205: multi-step chaining (e.g. "gather wood then build a house")

**Sprint 39 P1-C:** `IntentDraft` moved from `Agent.Planning` to `Agent.Core/Models/` to avoid circular project dependency with `Agent.Core.Runtime`.

### LlmEvaluatorImpl

**Source:** `Agent.Planning/LlmEvaluatorImpl.cs`

- **MinOutcomesBeforeEval:** 3 (avoids per-action LLM calls)
- **Fast-paths:** Too few outcomes, all succeeded, provider unavailable
- **Sprint 54 (TSK-0222):** forceEvaluate parameter for governor stall scenario
- **Sprint 58 Wave C (TSK-0320):** Don't fast-path on all-success when `WorldStateDiff.HasMismatch`
- **Evaluation prompt:** Goal name + world snapshot + last 10 outcomes → LLM returns `{"replan": bool, "reason": "..."}`
- **Conservative default:** On any error → false (continue current plan)

---

## 9. ActionOutcome System

**Source:** `Agent.Core/Models/ActionOutcome.cs`

### Core Types

```csharp
public record ActionOutcome(
    Guid GoalId,
    string ToolName,
    OutcomeType Outcome,
    string ObservationSummary,
    IReadOnlyList<StructuredEffect> Effects,
    DateTimeOffset Timestamp) : IObservationSummary;
```

### OutcomeType Enum (Sprint 40 P0-B)

| Value | Meaning |
|---|---|
| `Completed` | Action succeeded, expected result achieved |
| `NoProgress` | Tool succeeded but no measurable progress |
| `Failed` | Action failed due to error/exception |
| `Blocked` | Prerequisite not met (no reachable block, missing tool) |
| `Unreachable` | Pathfinding could not find a route |
| `TimedOut` | Action timed out |

`Success` property: computed — true only for `Completed`.

### StructuredEffect Type Vocabulary

| Type | Example |
|---|---|
| `ItemCollected` | `("ItemCollected", "oak_log", 5)` |
| `ItemConsumed` | `("ItemConsumed", "oak_planks", 4)` |
| `ItemCrafted` | `("ItemCrafted", "oak_planks", 4)` |
| `PositionChanged` | `("PositionChanged")` |
| `BlockPlaced` | `("BlockPlaced", "cobblestone", 1)` |
| `BlockMined` | `("BlockMined", "iron_ore", 3)` |
| `StatusRefreshed` | `("StatusRefreshed")` |
| `MemorySearched` | `("MemorySearched", Detail: "blueprints/")` |
| `MemoryPageCreated` | `("MemoryPageCreated", Detail: "new-page-id")` |

### Factory Helpers

```csharp
ActionOutcome.Collected(goalId, tool, item, count)      // OutcomeType.Completed
ActionOutcome.Succeeded(goalId, tool, summary)           // OutcomeType.Completed
ActionOutcome.Failed(goalId, tool, reason)               // OutcomeType.Failed
ActionOutcome.NoProgress(goalId, tool, summary)          // OutcomeType.NoProgress
```

### Wiring Status

- Sprint 35: Records and stubs added
- Sprint 36: `CallWithOutcomeAsync` in `ToolDispatcher` and `LogOutcome` in `IAgentJournal` — still stub
- Sprint 39 P1: `_cycleOutcomes` accumulated per dispatch cycle in `AgentBackgroundService`
- Correlated actions use `correlationId` (Guid) stored in `ActionData.Context`
- `_correlatedActions` tracks lifecycle: Dispatched → Completed/Failed/TimedOut

---

## 10. Build Fact Tracking — Fix History

**Source:** Repo memory `/memories/repo/build-fact-tracking-tsk0125-fix.md`, `AgentBackgroundService.cs`

### TSK-0125 (2026-06-27) — Volatile + Prefix Mismatch

**Problem:** `DecomposeBuild` returned all 215 PlaceBlock actions every cycle regardless of confirmed placements. Per-block status facts were written but not found at read time.

**Root causes:**
1. `_worldState` field was NOT `volatile` — thread visibility issue between `ProcessEventsAsync` (writer) and `DispatchActionsAsync` (reader), which run concurrently via `Task.WhenAll`
2. `ClearBuildFacts(IGoal goal)` used goal name suffix (`"small-house"` from `blueprint.Id`) but fact keys use `blueprint.Name` (`"Small Survival House"`) — prefix mismatch meant facts were never cleared

**Fixes applied:**
1. `private volatile WorldState _worldState` in `AgentBackgroundService.cs:172`
2. `ClearBuildFacts` fixed to use `IBuildGoal.Blueprint.Name` instead of `blueprint.Id`
3. `CancelGoal` captures `IBuildGoal` reference before nulling `_currentGoal`
4. Diagnostic logging in `EmitBuildPlacementLoop` (`HtnTaskLibrary.cs:670-688`)

---

## 11. Replan Flooding Fix (Sprint 36)

**Source:** Repo memory `/memories/repo/replan-flooding-fix-sprint36.md`, `AgentBackgroundService.cs`

### MineBlock Replan Flooding

**Root cause:** When `GatherItem:dirt` is active, planner produces `[SearchMemory, MineBlock×N, GetStatus]`. All MineBlock actions are fire-and-forget (0ms C# dispatch). C# planner replans every ~2s and re-pushes same MineBlock commands, flooding Node.js command queue.

**Fix:** Before dispatching a fire-and-forget tool, check `HasPendingActionOfTool()` — skip if same tool type already has an in-flight pending action.

### Error Event Action Name Mismatch

**Root cause:** Node.js sends `action: "mine"` in error events, but C# pending actions use tool name `"MineBlock"`. `FailCorrelatedActionByTool("mine")` never matched.

**Fix:** Added `MapNodeActionToToolName()` to translate wire names (mine→MineBlock, move→MoveTo, place→PlaceBlock, etc.).

### Inventory Inflation from Cumulative Count

**Root cause:** Node.js `mine` loop sent `blockMined` events with `count: mined` (cumulative: 1, 2, 3...). C# `ApplyBlockMined` called `AddInventoryItem(itemKey, e.Count)`, treating cumulative count as additive delta.

**Fix:** Changed `blockMined` event to send `count: 1` (delta per dig). `ApplyBlockMined` already handles delta correctly.

---

## 12. TaskSequenceGoal Decomposer + LLM Fallback (Sprint 55)

**Source:** Repo memory `/memories/repo/tasksequence-decomposer-llm-fallback-sprint55.md`

### Fix 1: TaskSequenceGoalDecomposer (TSK-0236)
- New file: `Agent.Planning/Decomposition/TaskSequenceGoalDecomposer.cs`
- Takes `DecomposerRegistry`, delegates `Decompose` to current step's decomposer
- Registered in `Program.cs` DecomposerRegistry

### Fix 2: LLM Fallback in HtnPlanner (TSK-0237)
- `HtnPlanner` now accepts `ILlmProvider?` and `ToolDispatcher?`
- `TryLlmFallback(goal, state)` constructs prompt with goal info, world state, tool list
- Calls `ILlmProvider.CompleteAsync`, parses JSON response via `ParseLlmActions`
- Throws only if LLM unavailable/fails
- `Agent.Planning.csproj` now references `Agent.Tools`

---

## 13. Per-Sprint Technical Debt (Outstanding)

| ID | Issue | Sprint | Status | File Reference |
|---|---|---|---|---|
| TSK-0159 | All 15 `goto()` calls lack timeouts in Node.js adapter | — | Unfixed | `MineflayerAdapter/index.js` |
| AG-001 | `chatFilter.js` referenced but never created | — | Unfixed | `MineflayerAdapter/` |
| AG-002 | `stopState.js` extracted but never wired | — | Unfixed | `MineflayerAdapter/` |
| AG-003 | ConstructBlueprint tool — pipeline exists but integration incomplete | — | Unfixed | `Agent.Tools/` |
| — | `ReplanGoalContext` never instantiated in production (test-only artifact) | Sprint 28 | Unfixed | `Agent.Core/Models/ReplanGoalContext.cs` |
| — | Overshoot planning: `GatherGoalDecomposer` passes full `TargetCount` ignoring current inventory | Sprint 26 | Unfixed | `Agent.Planning/Decomposition/GatherGoalDecomposer.cs` |
| — | `ILlmEvaluator` wired but observation-driven replanning loop still partial | Sprint 39 | Partial | `Agent.Planning/LlmEvaluatorImpl.cs` |
| — | `DashboardPublisherImpl` has hardcoded values (QueuedActions=0, OnlinePlayers=1, Blueprints=1) and dual write surface | Sprint 49 | Unfixed | `WebUI.Blazor/Dashboard/DashboardPublisherImpl.cs` |
| — | `SmeltGoal.HasFailed` always returns false — no write site exists | Sprint 44 | Open | `Agent.Planning/Goals/SmeltGoal.cs` |
| — | `GenericGatherGoal.HasFailed` reads facts that are never written (consecutive-failure counter used instead) | Sprint 30 | Open | `Agent.Planning/Goals/GenericGatherGoal.cs` |
| — | `_stopRequested` not checked in craft/smelt action handlers | — | Unfixed | `MineflayerAdapter/index.js` |
| — | `ChatInterpretation` type removed but `ChatIntentType` enum remains with stale values | Sprint 44 | Cleanup | `Agent.Planning/ChatModels.cs` |
| — | `PlaceBlockGoal.SmeltGoal` registered twice in master KB (duplicate row) | — | Cosmetic | Master KB §6 |
| — | `ChatIntentType.CreateGoal` still exists but never produced by any code path | Sprint 35 | Dead enum value | `Agent.Planning/ChatModels.cs` |

---

## 14. Chat Models — Intent Types

**Source:** `Agent.Planning/ChatModels.cs`

```csharp
public enum ChatIntentType
{
    NotAddressed,  // Message not directed at bot
    CreateGoal,    // Player wants a goal — DEAD (never produced post-Sprint 35)
    CancelGoal,    // Stop current goal
    QueryStatus,   // Status or inventory report
    QueryHelp,     // Available commands
    NavigateTo,    // Move to coordinates
    Unknown,       // Clarifying question needed
    Chat,          // Conversational
}
```

Note: `ChatIntentType` is a legacy enum. The actual pipeline now uses `IntentDraft.Intent` strings directly (12 allowed values). The enum is retained for the `ChatInterpreter` deterministic fallback path but `CreateGoal` is never produced by any code path since Sprint 35 removed the fast-path.

---

## 15. IntentDraft Transport

**Source:** `Agent.Core/Models/IntentDraft.cs`

```csharp
public record IntentDraft(
    string Addressed,          // "yes" | "maybe" | "no"
    string Intent,             // gather | build | craft | smelt | place | navigate | cancel
                               // | status | help | conversation | clarify | ignore
    string? Item,              // Minecraft item ID (no namespace)
    string? Blueprint,         // Blueprint ID
    int? Count,                // Quantity
    int? X, int? Y, int? Z,   // Coordinates
    double Confidence,         // 0.0–1.0
    string? ClarificationQuestion,  // Non-null when confidence < threshold
    string Response,           // In-game reply (max ~50 words)
    IReadOnlyList<string>? NextSteps);  // Multi-step chaining (TSK-0205)
```

---

## 16. File Map — All Key Files

| File | Purpose |
|---|---|
| `Agent.Core/Interfaces/IGoal.cs` | Goal interface |
| `Agent.Core/IReplanGovernor.cs` | Replan governor interface |
| `Agent.Core/ReplanGovernor.cs` | Governor implementation with graduated delays |
| `Agent.Core/BuildFactKeys.cs` | World-state fact key constants |
| `Agent.Core/Models/ActionOutcome.cs` | ActionOutcome record + OutcomeType + StructuredEffect |
| `Agent.Core/Models/IntentDraft.cs` | IntentDraft record (Sprint 39 P1-C moved here) |
| `Agent.Core/Models/TaskSequenceGoal.cs` | Compound multi-step goal |
| `Agent.Core/WorldStateProjector.cs` | Pure function event → state projector |
| `Agent.Planning/HtnTaskLibrary.cs` | Core HTN task decomposition (11 methods + DecomposeBuild/DecomposeCraftItem/DecomposeSmeltItem/DecomposeGatherItem) |
| `Agent.Planning/HtnPlanner.cs` | HTN planner with LLM fallback (Sprint 55) |
| `Agent.Planning/GoalFactory.cs` | Goal creation (sync + async prefix-based) |
| `Agent.Planning/IntentManager.cs` | Intent → GoalRequest mapping + ParseCommandString |
| `Agent.Planning/IntentDraft.cs` | Migration shim pointing to Agent.Core |
| `Agent.Planning/ChatModels.cs` | ChatIntentType enum (legacy) |
| `Agent.Planning/ChatInterpreter.cs` | Deterministic fallback chat interpreter |
| `Agent.Planning/LlmChatInterpreter.cs` | LLM-powered chat interpreter |
| `Agent.Planning/LlmEvaluatorImpl.cs` | LLM-backed action outcome evaluator |
| `Agent.Planning/Interfaces/IPlanner.cs` | Planner interface |
| `Agent.Planning/Interfaces/IGoalDecomposer.cs` | Decomposer interface |
| `Agent.Planning/Router/PlannerRouter.cs` | Routing through decomposer registry → HtnPlanner |
| `Agent.Planning/Decomposition/DecomposerRegistry.cs` | Thread-safe decomposer registry |
| `Agent.Planning/Decomposition/BuildGoalDecomposer.cs` | BuildGoal decomposer |
| `Agent.Planning/Decomposition/GatherGoalDecomposer.cs` | Gather/IItemSpecGoal decomposer (auto-tool crafting) |
| `Agent.Planning/Decomposition/CraftItemGoalDecomposer.cs` | CraftItemGoal decomposer |
| `Agent.Planning/Decomposition/PlaceBlockGoalDecomposer.cs` | PlaceBlockGoal decomposer |
| `Agent.Planning/Decomposition/SmeltGoalDecomposer.cs` | SmeltGoal decomposer (Sprint 44) |
| `Agent.Planning/Decomposition/SurviveNightGoalDecomposer.cs` | SurviveNightGoal decomposer |
| `Agent.Planning/Decomposition/TaskSequenceGoalDecomposer.cs` | TaskSequenceGoal decomposer (Sprint 55) |
| `Agent.Planning/Goals/BuildGoal.cs` | Blueprint construction goal |
| `Agent.Planning/Goals/IBuildGoal.cs` | Build goal marker interface |
| `Agent.Planning/Goals/BuildOrigin.cs` | Build origin value object |
| `Agent.Planning/Goals/GenericGatherGoal.cs` | Generic item gathering goal |
| `Agent.Planning/Goals/CraftItemGoal.cs` | Item crafting goal |
| `Agent.Planning/Goals/SmeltGoal.cs` | Item smelting goal (Sprint 44) |
| `Agent.Planning/Goals/PlaceBlockGoal.cs` | Single/multi block placement goal (Sprint 54) |
| `Agent.Planning/Goals/GatherWoodGoal.cs` | Legacy wood gathering goal |
| `Agent.Planning/Goals/SurviveNightGoal.cs` | Night survival goal |
| `Agent.Planning/Llm/OllamaProvider.cs` | Ollama LLM provider |
| `Agent.Planning/Llm/AnthropicProvider.cs` | Anthropic LLM provider |
| `Agent.Planning/Llm/GeminiProvider.cs` | Gemini LLM provider |
| `Agent.Planning/Llm/OpenAICompatibleProvider.cs` | OpenAI-compatible provider |
| `Agent.Construction/BlueprintExecutor.cs` | Floor-first PlaceBlock action generation |
| `Agent.Construction/BlueprintParser.cs` | Markdown → Blueprint schema parsing |
| `WebUI.Blazor/Program.cs` | DI registration, GoalFactory, DecomposerRegistry, PlannerRouter setup |
| `WebUI.Blazor/AgentBackgroundService.cs` | Main agent loop: SetGoal, DispatchActionsAsync, ProcessEventsAsync, replan, consecutive-failure guard |
