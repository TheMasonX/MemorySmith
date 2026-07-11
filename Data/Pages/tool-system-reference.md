# MemorySmith.Agent — Tool System Reference

> **Agent:** 6 of 10 — Memory Audit Sweep  
> **Date:** 2026-07-10  
> **Confidence:** 95%  
> **Tags:** `tool-system`, `itool`, `tooldispatcher`, `actionprotocol`, `schema-validation`, `actionoutcome`, `actiondata`, `audit`

---

## Table of Contents

1. [Overview](#overview)
2. [ITool Interface](#itool-interface)
3. [IToolCaller Interface](#itoolcaller-interface)
4. [All ITool Implementations](#all-itool-implementations)
5. [ActionProtocol — Wire Names](#actionprotocol--wire-names)
6. [ToolDispatcher Pipeline](#tooldispatcher-pipeline)
7. [Schema Validation](#schema-validation)
8. [ActionData Structure](#actiondata-structure)
9. [ActionOutcome Structure](#actionoutcome-structure)
10. [Tool Registration](#tool-registration)
11. [Tool-to-Adapter Mapping](#tool-to-adapter-mapping)
12. [Known Issues](#known-issues)
13. [ConstructBlueprintTool Status](#constructblueprinttool-status)
14. [Key File Paths](#key-file-paths)

---

## Overview

The tool system is the primary mechanism by which the LLM-driven planning pipeline interacts with the Minecraft world. Tools are registered in a `ToolDispatcher` at startup, exposed to the LLM via prompt injection, validated against JSON Schema at the dispatch boundary, and forwarded to the Node.js Mineflayer adapter over a WebSocket bridge.

**Pipeline:** `LLM Planner → ToolDispatcher.CallAsync → Schema Validation → ITool.ExecuteAsync → ActionData → WebSocketBridge → Mineflayer adapter → World events → ActionOutcome`

---

## ITool Interface

**File:** `Agent.Core/Interfaces/ITool.cs`

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}
```

Each tool provides:
- **`Name`** — Canonical name (PascalCase, e.g. `"MoveTo"`, `"MineBlock"`). Used as the primary registration key.
- **`Description`** — Prompt text describing what the tool does and when to use it.
- **`InputSchema`** — A JSON Schema document (as `JsonElement`) describing expected arguments and their types.
- **`ExecuteAsync`** — Accepts parsed JSON arguments, returns `ToolResult`.

**InputSchema caching:** Some tools cache the parsed `JsonDocument` as a `private static readonly` field (e.g., `FindFlatAreaTool`, `QueryBlocksTool`, `QueryEntitiesTool`). Others parse inline each time `InputSchema` is accessed, creating a new `JsonDocument` per call. This inconsistency is a known issue (see [Known Issues](#known-issues)).

---

## IToolCaller Interface

**File:** `Agent.Tools/Interfaces/IToolCaller.cs`

```csharp
public interface IToolCaller
{
    Task<ToolResult> CallAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default);

    async Task<(ToolResult Result, ActionOutcome Outcome)> CallWithOutcomeAsync(
        Guid goalId, string toolName, JsonElement arguments,
        CancellationToken cancellationToken = default);
}
```

- **`CallAsync`** — Validates and executes a named tool. Never throws — returns failure `ToolResult` on error.
- **`CallWithOutcomeAsync`** — Wraps `CallAsync` and produces an `ActionOutcome`. Default implementation maps `result.Success` to `Succeeded`/`Failed` factory methods.
- `ToolDispatcher` overrides `CallWithOutcomeAsync` with a richer implementation that preserves `OutcomeType` (Blocked, Unreachable, TimedOut, NoProgress).

---

## All ITool Implementations

There are **17 registered tool instances** covering **15 unique tool classes** (two tools have aliases). Below is the complete inventory.

### 1. MoveToTool

| Property | Value |
|---|---|
| **Class** | `MoveToTool` |
| **File** | `Agent.Tools/Tools/MoveToTool.cs` |
| **Name** | `"MoveTo"` |
| **Wire name** | `ActionProtocol.Move` → `"move"` |
| **Description** | "Navigate the bot to the specified block coordinates." |
| **Required args** | None (supports context carry) |
| **Optional args** | `x`, `y`, `z` (explicit); `nearestX`, `nearestY`, `nearestZ` (context-carried from SearchMemoryTool) |
| **Schema** | All optional; priority: explicit x/y/z > nearestX/Y/Z > error |
| **Adapter handler** | `case 'move'` — pathfinder `GoalNear(x, y, z, 1)`; Sprint 55 Wave C clamps Y to ground level |

### 2. GetStatusTool

| Property | Value |
|---|---|
| **Class** | `GetStatusTool` |
| **File** | `Agent.Tools/Tools/GetStatusTool.cs` |
| **Name** | `"GetStatus"` (+ alias `"Status"`) |
| **Wire name** | `ActionProtocol.Status` → `"status"` |
| **Description** | "Request current bot position, health, food level, and inventory." |
| **Required args** | None |
| **Schema** | `{"type":"object","properties":{}}` |
| **Adapter handler** | `case 'status'` — calls `sendBotStatus()` which emits `status` event with pos, HP, food, inventory, gameMode |

### 3. MineBlockTool

| Property | Value |
|---|---|
| **Class** | `MineBlockTool` |
| **File** | `Agent.Tools/Tools/MineBlockTool.cs` |
| **Name** | `"MineBlock"` |
| **Wire name** | `ActionProtocol.Mine` → `"mine"` |
| **Description** | "Mine specified blocks near the bot. Requires block name and optional count." |
| **Required args** | `block` (string) |
| **Optional args** | `count` (integer, default 1) |
| **Schema** | `block: string, count: integer` |
| **Adapter handler** | `case 'mine'` — mining loop with alias support, Y-level preference, dig failure tracking, item pickup. Emits `blockMined` (per block), `mineComplete` (finished), `blockNotFound`, `mineAborted` |

### 4. PlaceBlockTool

| Property | Value |
|---|---|
| **Class** | `PlaceBlockTool` |
| **File** | `Agent.Tools/Tools/PlaceBlockTool.cs` |
| **Name** | `"PlaceBlock"` (+ alias `"place"`) |
| **Wire name** | `ActionProtocol.Place` → `"place"` |
| **Description** | "Place a block at (x, y, z)." |
| **Required args** | `x` (integer), `y` (integer), `z` (integer), plus `material` OR `block` (string) |
| **Optional args** | `count` (integer, informational only) |
| **Schema** | Accepts both `material` and `block` as aliases for each other |
| **Known issue** | See [Known Issues — PlaceBlock "block" Alias](#known-issues) |
| **Adapter handler** | `case 'place'` — terrain collision detection, scaffold fallback, facing-aware placement, creative inventory provisioning |

### 5. ChatTool

| Property | Value |
|---|---|
| **Class** | `ChatTool` |
| **File** | `Agent.Tools/Tools/ChatTool.cs` |
| **Name** | `"Chat"` |
| **Wire name** | `ActionProtocol.Chat` → `"chat"` |
| **Description** | "Send a chat message in Minecraft as the bot." |
| **Required args** | `message` (string, max 256 chars) |
| **Adapter handler** | `case 'chat'` — `bot.chat()`. Guards against `bot.entity` being null (retry once after 500ms) |

### 6. CraftItemTool

| Property | Value |
|---|---|
| **Class** | `CraftItemTool` |
| **File** | `Agent.Tools/Tools/CraftItemTool.cs` |
| **Name** | `"CraftItem"` |
| **Wire name** | `ActionProtocol.Craft` → `"craft"` |
| **Description** | "Craft an item from materials in inventory. A crafting table must be nearby for 3x3 recipes." |
| **Required args** | `item` (string) |
| **Optional args** | `count` (integer, default 1) |
| **Adapter handler** | `case 'craft'` — finds recipe, navigates to crafting table if required, calls `bot.craft()`. Emits `craftComplete` event, then `sendBotStatus()` to refresh inventory |

### 7. FurnaceTool (SmeltItem)

| Property | Value |
|---|---|
| **Class** | `FurnaceTool` |
| **File** | `Agent.Tools/Tools/FurnaceTool.cs` |
| **Name** | `"SmeltItem"` |
| **Wire name** | `ActionProtocol.Smelt` → `"smelt"` |
| **Description** | "Smelt an item in a nearby furnace. Requires the item in inventory and a furnace within 16 blocks." |
| **Required args** | `item` (string) |
| **Optional args** | `count` (integer, default 1); `fuel` (string, default `"coal"`) |
| **Adapter handler** | `case 'smelt'` — finds nearest furnace (within `FURNACE_SEARCH_RADIUS`), opens it, adds fuel if empty, puts input, waits for output, takes it, closes |

### 8. FindFlatAreaTool

| Property | Value |
|---|---|
| **Class** | `FindFlatAreaTool` |
| **File** | `Agent.Tools/Tools/FindFlatAreaTool.cs` |
| **Name** | `"FindFlatArea"` |
| **Wire name** | `ActionProtocol.FindFlatArea` → `"findFlatArea"` |
| **Description** | "Scan a radius around the bot for a flat, buildable area." |
| **Required args** | None |
| **Optional args** | `radius` (integer, default 32); `minFlatArea` (integer, default 25) |
| **Schema caching** | ✅ Uses `private static readonly JsonDocument` — correct pattern |
| **Adapter handler** | `case 'findFlatArea'` — flood-fill height map, scores by area/compactness/flatness/proximity. Emits `flatAreaFound` event (always — even when no area found, sends area=0) |

### 9. WanderTool

| Property | Value |
|---|---|
| **Class** | `WanderTool` |
| **File** | `Agent.Tools/Tools/WanderTool.cs` |
| **Name** | `"Wander"` |
| **Wire name** | `ActionProtocol.Wander` → `"wander"` |
| **Description** | "Walk in a random nearby direction to explore the world." |
| **Required args** | None |
| **Optional args** | `radius` (integer, default 20); `maxDistanceFromSpawn` (integer, default 100) |
| **Adapter handler** | `case 'wander'` — picks random azimuth, computes target, clamps to spawn boundary. Emits `wanderComplete` or `wanderFailed` |

### 10. SearchMemoryTool

| Property | Value |
|---|---|
| **Class** | `SearchMemoryTool` |
| **File** | `Agent.Tools/Tools/SearchMemoryTool.cs` |
| **Name** | `"SearchMemory"` |
| **Wire name** | N/A (does not dispatch to adapter — uses `IMemoryGateway` directly) |
| **Description** | "Searches the world knowledge base for spatial observations, block data, biome notes, and in-world exploration history." |
| **Required args** | `query` (string) |
| **Optional args** | `limit` (integer, default 10) |
| **Context carry** | Emits `nearestX`/`nearestY`/`nearestZ` in result `Data` when coordinate patterns found in snippets |
| **Sprint 53 (TSK-0193)** | Wraps search in try/catch for transient failure resilience |
| **Regex patterns** | Two coordinate extraction regexes: parenthesized `at (x, y, z)` and labeled `x: n` patterns |

### 11. GetPageTool

| Property | Value |
|---|---|
| **Class** | `GetPageTool` |
| **File** | `Agent.Tools/Tools/GetPageTool.cs` |
| **Name** | `"GetPage"` |
| **Wire name** | N/A (uses `IMemoryGateway` directly — agent KB instance) |
| **Description** | "Read a wiki page from MemorySmith by slug (e.g. 'architecture', 'blueprints/gothic-cathedral')." |
| **Required args** | `pageId` (string) |
| **Schema** | Inline-parsed compact JSON — no caching |
| **Routes to** | Agent KB (`IMemoryGateway memory`), NOT the world KB |

### 12. CreatePageTool

| Property | Value |
|---|---|
| **Class** | `CreatePageTool` |
| **File** | `Agent.Tools/Tools/CreatePageTool.cs` |
| **Name** | `"CreatePage"` |
| **Wire name** | N/A (uses `IMemoryGateway` directly — world KB instance) |
| **Description** | "Creates or updates a page in the world knowledge base to record in-world observations..." |
| **Required args** | `title` (string) |
| **Optional args** | `body` (string), `content` (string — alias for body), `type` (string) |
| **Routes to** | World KB (`IMemoryGateway worldMemory`), NOT the agent KB |
| **Note** | Accepts both `body` and `content` as aliases; `content` is documented as an alias |

### 13. QueryBlocksTool

| Property | Value |
|---|---|
| **Class** | `QueryBlocksTool` |
| **File** | `Agent.Tools/Tools/QueryBlocksTool.cs` |
| **Name** | `"QueryBlocks"` |
| **Wire name** | `ActionProtocol.QueryBlocks` → `"queryBlocks"` |
| **Description** | "Query blocks at a position or in a region." |
| **Required args** | `x`, `y`, `z` (integer) |
| **Optional args** | `x2`, `y2`, `z2` (integers — for bounding box queries) |
| **Schema caching** | ✅ Uses `private static readonly JsonDocument` |
| **Adapter handler** | `case 'queryBlocks'` — single block or bounding box (capped at 4096 blocks). Result via `BlocksQueriedEvent` |
| **Sprint** | 55 Wave B |

### 14. QueryEntitiesTool

| Property | Value |
|---|---|
| **Class** | `QueryEntitiesTool` |
| **File** | `Agent.Tools/Tools/QueryEntitiesTool.cs` |
| **Name** | `"QueryEntities"` |
| **Wire name** | `ActionProtocol.QueryEntities` → `"queryEntities"` |
| **Description** | "Scan for entities near the bot." |
| **Required args** | None |
| **Optional args** | `radius` (integer, default 16, max 64); `entityType` (string — `"mob"`, `"player"`, `"object"`) |
| **Schema caching** | ✅ Uses `private static readonly JsonDocument` |
| **Adapter handler** | `case 'queryEntities'` — scans `bot.entities`, sorts by distance. Result via `EntitiesQueriedEvent` |
| **Sprint** | 55 Wave B |

---

## ActionProtocol — Wire Names

**File:** `Agent.Tools/ActionProtocol.cs`

```csharp
public static class ActionProtocol
{
    public const string Move            = "move";
    public const string Mine            = "mine";
    public const string Place           = "place";
    public const string Status          = "status";
    public const string Wander          = "wander";
    public const string Chat            = "chat";
    public const string Craft           = "craft";
    public const string Smelt           = "smelt";
    public const string FindFlatArea    = "findFlatArea";
    public const string FindReachableBlock = "findReachableBlock";  // Sprint 40 P0-B
    public const string QueryBlocks     = "queryBlocks";            // Sprint 55 Wave B
    public const string QueryEntities   = "queryEntities";          // Sprint 55 Wave B
}
```

All wire names are **lowercase** (ADR-010). Each tool sets `ActionData.Tool` to the appropriate constant; the `WebSocketBridge` forwards it as-is (no lowercasing). The `FindReachableBlock` wire name exists in `ActionProtocol` but no corresponding `ITool` implementation is registered — it is called directly from plan decomposition code.

---

## ToolDispatcher Pipeline

**File:** `Agent.Tools/ToolDispatcher.cs`

Registration is done in `WebUI.Blazor/Program.cs` (lines 280-299). The `ToolDispatcher`:

1. **Registration** — `Register(ITool)` stores by canonical name; `Register(string name, ITool)` stores under an alias. Alias registration logs a warning if overwriting an existing key.
2. **Name resolution** — `_tools` is a `ConcurrentDictionary<string, ITool>` with `OrdinalIgnoreCase` comparer.
3. **Schema validation** — `ValidateAgainstSchema(args, tool.InputSchema)` checks type, required, enum, min/max, minLength/maxLength.
4. **Execution** — `tool.ExecuteAsync(arguments, cts.Token)` wrapped in try/catch. Exceptions produce `ToolResult(false, ...)` with structured exception metadata.
5. **Outcome wrapping** — `CallWithOutcomeAsync` maps `ToolResult` to `ActionOutcome` via `MapResultToOutcome`, which preserves rich `OutcomeType`.

### Key pipeline behaviors

| Behavior | Detail |
|---|---|
| **Unregistered tool** | Returns `ToolResult(false, "Tool 'X' is not registered.")` and logs a journal entry |
| **Schema validation failure** | Returns `ToolResult(false, "Schema validation failed...")` and logs a journal entry |
| **Exception during execution** | Caught by outer try/catch; logs structured exception metadata (type, stack, inner); returns `ToolResult(false, ...)` |
| **Cancellation** | `OperationCanceledException` re-thrown — not caught by the exception handler |
| **Double-journal prevention** | Sprint 37 P0-B: `CallAsync` no longer emits success/failure journal entries; callers using `CallWithOutcomeAsync` call `_journal?.LogOutcome(outcome)` explicitly in the outer dispatch loop |

### RegisteredNames

`RegisteredNames` (Sprint 36 P1-C) returns all keys (including aliases) in sorted order for deterministic LLM prompt injection. The tool list is injected into the LLM prompt in `LlmChatInterpreter.BuildSystemPrompt` (Sprint 55: passes `ITool` objects, not just names, so the LLM sees descriptions and parameter names).

---

## Schema Validation

The `ToolDispatcher.ValidateAgainstSchema` method is a lightweight JSON Schema validator covering the subset used by tool schemas.

**Supported constraints:**

| Constraint | Sprint | Behavior |
|---|---|---|
| `type: "object"` | 5 | Root must declare `type: "object"` |
| `properties` | 5 | Property names, types, descriptions |
| `required` | 5 | Array of required property names |
| `type: "integer"` | 5 | Uses `TryGetInt32` — correctly rejects scientific notation (Sprint 25 P0-C) |
| `type: "number"` | 5 | Validates `ValueKind == JsonValueKind.Number` |
| `type: "string"` | 5 | Validates `ValueKind == JsonValueKind.String` |
| `type: "boolean"` | 5 | Validates `ValueKind` is True or False |
| `type: "object"` | 5 | Validates `ValueKind == JsonValueKind.Object` |
| `type: "array"` | 5 | Validates `ValueKind == JsonValueKind.Array` |
| `minimum` / `maximum` | 39 P3 | Numeric min/max bounds |
| `enum` | 39 P3 | Allowed values (compares raw JSON text) |
| `minLength` / `maxLength` | 39 P3 | String length bounds |

**Validation flow:**
1. Check root `type` is `"object"`
2. Check args is a JSON object
3. If no `properties` in schema, accept anything
4. For each arg property: check it's declared in schema, check type, check constraints
5. Check all `required` properties are present
6. Return null on success, error string on failure

**Limitations:** Does not support nested `$ref`, `allOf`/`anyOf`/`oneOf`, `not`, `pattern`, `format`, or `additionalProperties`. Unknown schema types are silently accepted (forward-compatible).

---

## ActionData Structure

**File:** `Agent.Core/Models/ActionData.cs`

```csharp
public record ActionData
{
    public string Tool { get; init; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; init; } = [];
    public Dictionary<string, object?> Context { get; init; } = [];
}
```

| Field | Type | Purpose |
|---|---|---|
| `Tool` | `string` | Wire name set by the tool to an `ActionProtocol` constant |
| `Arguments` | `Dictionary<string, object?>` | Tool-specific parameters (block, count, x, y, z, etc.) |
| `Context` | `Dictionary<string, object?>` | Mutable inter-action context bag. Shared across all actions in a single plan dispatch |

**Context carry (Phase 4):** Tools can write results into `Context` so subsequent actions in the same plan can read them. Example: `SearchMemoryTool` writes `nearestX`/`nearestY`/`nearestZ`, and `MoveToTool` reads them if explicit coordinates are absent.

**Factory:** `ActionFactory.Create(tool, params (key, value)[] args)` — creates a fresh dictionary per call to prevent accidental shared-mutation between decomposers (TSK-0135).

**Wire format (WebSocket):**
```json
{"action":"move","arguments":{"x":10,"y":64,"z":20}}
```

The `WebSocketBridge.SendAsync` serializes `ActionData` to this format. Type-specific handling: `int`, `long`, `double`, `float`, `bool`, `null`, and `ToString()` fallback for other types.

---

## ActionOutcome Structure

**File:** `Agent.Core/Models/ActionOutcome.cs`

### OutcomeType enum

```csharp
public enum OutcomeType
{
    Completed,    // Action completed successfully
    NoProgress,   // Call succeeded but no measurable progress
    Failed,       // Error or exception
    Blocked,      // Prerequisite not met
    Unreachable,  // Target not reachable via pathfinding
    TimedOut,     // Action timed out
}
```

### ActionOutcome record

```csharp
public record ActionOutcome(
    Guid GoalId,
    string ToolName,
    OutcomeType Outcome,
    string ObservationSummary,
    IReadOnlyList<StructuredEffect> Effects,
    DateTimeOffset Timestamp
) : IObservationSummary
{
    public bool Success => Outcome == OutcomeType.Completed;
}
```

### StructuredEffect record

```csharp
public record StructuredEffect(
    string Type,       // e.g. "ItemCollected", "BlockMined", "PositionChanged"
    string? Item = null,
    int? Count = null,
    string? Detail = null
);
```

**Effect type vocabulary:**
- `ItemCollected` — item landed in inventory
- `ItemConsumed` — item removed from inventory
- `ItemCrafted` — item crafted (Sprint 36)
- `PositionChanged` — bot moved
- `BlockPlaced` — block placed in world
- `BlockMined` — block removed from world
- `StatusRefreshed` — full bot status refreshed
- `MemorySearched` — MemorySmith search executed
- `MemoryPageCreated` — new MemorySmith page created

### Factory methods

| Method | OutcomeType | Use case |
|---|---|---|
| `ActionOutcome.Collected(goalId, tool, item, count)` | `Completed` | Item picked up |
| `ActionOutcome.Succeeded(goalId, tool, summary)` | `Completed` | Generic success |
| `ActionOutcome.Failed(goalId, tool, reason)` | `Failed` | Generic failure |
| `ActionOutcome.NoProgress(goalId, tool, detail)` | `NoProgress` | Tool ran but no progress |
| `ActionOutcome.Blocked(goalId, tool, reason)` | `Blocked` | Prerequisite missing |
| `ActionOutcome.Unreachable(goalId, tool, detail)` | `Unreachable` | Pathfinding failed |
| `ActionOutcome.TimedOut(goalId, tool, detail)` | `TimedOut` | Timeout expired |

### ToolResult record

```csharp
public record ToolResult(bool Success, string? Message = null,
    Dictionary<string, object?>? Data = null,
    OutcomeType Outcome = OutcomeType.Completed);
```

The `Outcome` field defaults to `Completed` for backward compatibility. Tools that need to report `Blocked`, `Unreachable`, `TimedOut`, or `NoProgress` should set this explicitly. `CallWithOutcomeAsync` maps this to the corresponding `ActionOutcome` factory method (TSK-0110).

---

## Tool Registration

**File:** `WebUI.Blazor/Program.cs` (lines 280-299)

```csharp
var d = new ToolDispatcher(journal, logger);
d.Register(new MoveToTool(world));
d.Register(new GetStatusTool(world));
d.Register("Status", new GetStatusTool(world));          // alias
d.Register(new MineBlockTool(world));
d.Register(new WanderTool(world));
d.Register(new PlaceBlockTool(world));
d.Register("place", new PlaceBlockTool(world));          // alias (BlueprintExecutor uses "place")
d.Register(new SearchMemoryTool(worldMemory));            // world KB
d.Register(new GetPageTool(memory));                     // agent KB
d.Register(new CreatePageTool(worldMemory));              // world KB
d.Register(new ChatTool(world));
d.Register(new CraftItemTool(world));
d.Register(new FurnaceTool(world));
d.Register(new FindFlatAreaTool(world));
d.Register(new QueryBlocksTool(world));
d.Register(new QueryEntitiesTool(world));
```

**Registration summary (17 entries):**

| Key | Tool class | Alias? |
|---|---|---|
| `"MoveTo"` | `MoveToTool` | No |
| `"GetStatus"` | `GetStatusTool` | Primary |
| `"Status"` | `GetStatusTool` | Alias (backward compat) |
| `"MineBlock"` | `MineBlockTool` | No |
| `"Wander"` | `WanderTool` | No |
| `"PlaceBlock"` | `PlaceBlockTool` | Primary |
| `"place"` | `PlaceBlockTool` | Alias (BlueprintExecutor) |
| `"SearchMemory"` | `SearchMemoryTool` | No |
| `"GetPage"` | `GetPageTool` | No |
| `"CreatePage"` | `CreatePageTool` | No |
| `"Chat"` | `ChatTool` | No |
| `"CraftItem"` | `CraftItemTool` | No |
| `"SmeltItem"` | `FurnaceTool` | No |
| `"FindFlatArea"` | `FindFlatAreaTool` | No |
| `"QueryBlocks"` | `QueryBlocksTool` | No |
| `"QueryEntities"` | `QueryEntitiesTool` | No |

**Note:** The concrete `ToolDispatcher` is also registered as a singleton for DI injection into `HtnPlanner` (Sprint 55 LLM fallback).

---

## Tool-to-Adapter Mapping

| Tool class | ActionProtocol | Adapter `case` | Events emitted by adapter |
|---|---|---|---|
| `MoveToTool` | `"move"` | `'move'` | `moveComplete` |
| `GetStatusTool` | `"status"` | `'status'` | `status` (pos, HP, food, inventory) |
| `MineBlockTool` | `"mine"` | `'mine'` | `blockMined`, `mineComplete`, `mineAborted`, `blockNotFound`, `actionProgress` |
| `PlaceBlockTool` | `"place"` | `'place'` | `blockPlaced`, `blockPlaceSkipped` |
| `ChatTool` | `"chat"` | `'chat'` | None (direct `bot.chat()`) |
| `CraftItemTool` | `"craft"` | `'craft'` | `craftComplete` + `sendBotStatus()` |
| `FurnaceTool` | `"smelt"` | `'smelt'` | `smeltComplete` |
| `FindFlatAreaTool` | `"findFlatArea"` | `'findFlatArea'` | `flatAreaFound` (always emitted) |
| `WanderTool` | `"wander"` | `'wander'` | `wanderComplete`, `wanderFailed` |
| `QueryBlocksTool` | `"queryBlocks"` | `'queryBlocks'` | `BlocksQueriedEvent` (async) |
| `QueryEntitiesTool` | `"queryEntities"` | `'queryEntities'` | `EntitiesQueriedEvent` (async) |
| `SearchMemoryTool` | N/A | N/A | Direct `IMemoryGateway.SearchAsync` call |
| `GetPageTool` | N/A | N/A | Direct `IMemoryGateway.GetPageAsync` call |
| `CreatePageTool` | N/A | N/A | Direct `IMemoryGateway.CreatePageAsync` call |

### Adapter error classification (TSK-0165)

The Mineflayer adapter classifies errors into machine-readable reason codes:

| Reason Code | Meaning |
|---|---|
| `path_no_path` | No path/route found |
| `path_timeout` | Pathfinding timed out |
| `path_blocked` | Path blocked/obstructed |
| `no_block_found` | Target block not found in range |
| `missing_item` | Required item not in inventory |
| `missing_recipe` | Recipe not found |
| `dig_failed` | Dig operation failed |
| `not_spawned` | Bot not spawned/connected |
| `furnace_not_found` | Furnace not found in range |
| `crafting_table_not_found` | Crafting table not found |
| `unknown_error` | Unclassifiable error |

---

## Known Issues

### PlaceBlock "block" Alias (TSK-0231)

The `PlaceBlockTool.InputSchema` declares both `"material"` and `"block"` as string properties, where `"block"` is documented as "Alias for material." The `ExecuteAsync` method accepts either:

```csharp
if (!arguments.TryGetProperty("material", out var matEl) &&
    !arguments.TryGetProperty("block", out matEl))
    return new ToolResult(false, "PlaceBlock requires material (or block).");
```

The schema is a single inline JSON string with both properties listed but no `oneOf` constraint. This means the validator accepts both, but LLMs may emit either — which is the intended flexibility.

**Relevant files:**
- `Agent.Tools/Tools/PlaceBlockTool.cs` (line 26 — schema; line 42 — dual-property fallback)
- `WebUI.Blazor/Program.cs` (line 289 — `"place"` alias for BlueprintExecutor)
- `Agent.Construction/BlueprintExecutor.cs` (line 16 — uses `"place"` wire name, `"material"` argument key)

### InputSchema Caching Inconsistency

Three caching patterns exist across tools:

| Pattern | Tools | Risk |
|---|---|---|
| `private static readonly JsonDocument` cached field | `FindFlatAreaTool`, `QueryBlocksTool`, `QueryEntitiesTool` | ✅ Safe — single allocation, active lifetime |
| `JsonDocument.Parse(...).RootElement` returned directly | `ChatTool`, `CraftItemTool`, `FurnaceTool`, `GetPageTool`, `GetStatusTool`, `MineBlockTool`, `MoveToTool`, `SearchMemoryTool`, `WanderTool` | ⚠️ The disposable `JsonDocument` goes out of scope. In practice the `RootElement` survives because the GC hasn't collected it yet, but this is **undefined behavior** per `JsonDocument` contract |
| Inline compact JSON string | `GetPageTool`, `PlaceBlockTool`, `WanderTool` | Mixed — same disposal risk as above |

**Recommendation:** Migrate all tools to the cached `private static readonly JsonDocument` pattern used by `FindFlatAreaTool`. The current behavior works in practice because `InputSchema` is called frequently enough to keep the document alive, but it is technically incorrect.

### No Timeout Overrides on Tools

There are **no tool-level timeout overrides** anywhere in `Agent.Tools/`. Timeout handling is entirely at the adapter level (the Node.js process has no configurable timeout per action type) and at the planner level (correlated action sweeping in `AgentBackgroundService`). Individual tools do not set timeouts.

### ConstructBlueprintTool Is Not Registered

The `ConstructBlueprint` tool is referenced in documentation and `BlueprintSchema.cs` (line 18: "The ConstructBlueprint tool reads this page and emits PlaceBlock actions") and `PlaceBlockTool.cs` (line 14: "For blueprint-scale construction... use ConstructBlueprint"). However, **no `ConstructBlueprint` ITool implementation exists.** The `BlueprintExecutor` emits `PlaceBlock` actions directly into the plan queue — it bypasses the tool system entirely. This is a documentation/capability gap: the referenced tool does not exist as a runtime component.

---

## ConstructBlueprintTool Status

| Aspect | Status |
|---|---|
| **Referenced in docs** | ✅ Yes — `BlueprintSchema.cs` and `PlaceBlockTool.cs` |
| **ITool implementation** | ❌ Does not exist |
| **Registered in dispatcher** | ❌ Not registered |
| **Actual implementation** | `BlueprintExecutor` in `Agent.Construction` — emits raw `ActionData` with `Tool = "place"` |
| **Blueprint parsing** | `BlueprintParser` reads wiki pages → `Blueprint` records → `PlacementBlock[]` |
| **Build sequence** | `DecomposeBuild` plan step → `IToolCaller` is NOT used; `IBlueprintExecutor.Execute()` produces `List<ActionData>` directly |

**Recommendation:** Either implement `ConstructBlueprintTool : ITool` that wraps `IBlueprintExecutor`, or update documentation to remove the reference to a non-existent `ITool`.

---

## Key File Paths

| File | Purpose |
|---|---|
| `Agent.Core/Interfaces/ITool.cs` | ITool interface definition |
| `Agent.Tools/Interfaces/IToolCaller.cs` | IToolCaller interface with CallAsync + CallWithOutcomeAsync |
| `Agent.Tools/ToolDispatcher.cs` | Central dispatcher: registration, validation, execution, outcome mapping |
| `Agent.Tools/ActionProtocol.cs` | Wire protocol name constants |
| `Agent.Core/Models/ActionData.cs` | ActionData record, ToolResult, SearchResult, GoalMeta, ActionFactory |
| `Agent.Core/Models/ActionOutcome.cs` | ActionOutcome record, OutcomeType enum, StructuredEffect, factory methods |
| `Agent.Core/ToolRequirements.cs` | Tool-requirement lookups for mining (pickaxe/axe/shovel per block) |
| `Agent.Tools/Tools/ChatTool.cs` | Chat tool |
| `Agent.Tools/Tools/CraftItemTool.cs` | Craft item tool |
| `Agent.Tools/Tools/CreatePageTool.cs` | Create wiki page tool |
| `Agent.Tools/Tools/FindFlatAreaTool.cs` | Find flat area tool (cached schema) |
| `Agent.Tools/Tools/FurnaceTool.cs` | Smelt item tool |
| `Agent.Tools/Tools/GetPageTool.cs` | Read wiki page tool |
| `Agent.Tools/Tools/GetStatusTool.cs` | Get bot status tool |
| `Agent.Tools/Tools/MineBlockTool.cs` | Mine block tool |
| `Agent.Tools/Tools/MoveToTool.cs` | Move to coordinates tool |
| `Agent.Tools/Tools/PlaceBlockTool.cs` | Place block tool (dual material/block alias) |
| `Agent.Tools/Tools/QueryBlocksTool.cs` | Query blocks tool (cached schema) |
| `Agent.Tools/Tools/QueryEntitiesTool.cs` | Query entities tool (cached schema) |
| `Agent.Tools/Tools/SearchMemoryTool.cs` | Search world KB tool (coordinate extraction) |
| `Agent.Tools/Tools/WanderTool.cs` | Wander tool |
| `WebUI.Blazor/Program.cs` | Tool registration (lines 280-299) |
| `Agent.Construction/BlueprintExecutor.cs` | Blueprint → PlaceBlock action emitter |
| `Agent.Construction/BlueprintSchema.cs` | Blueprint record schema + ConstructBlueprint docs reference |
| `MineflayerAdapter/index.js` | Node.js action dispatch handlers |
| `Agent.World.Minecraft/WebSocketBridge.cs` | WebSocket wire protocol serialization |
| `Agent.World.Minecraft/MinecraftAdapter.cs` | Adapter host process management + SendActionAsync |
| `Agent.Planning/LlmChatInterpreter.cs` | LLM prompt injection of tool names/descriptions |
| `Agent.Planning/HtnPlanner.cs` | HTN planner with ToolDispatcher dependency for LLM fallback |

---

*End of Tool System Reference — Agent 6 of 10, Memory Audit Sweep 2026-07-10*
