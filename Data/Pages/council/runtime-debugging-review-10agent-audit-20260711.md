# Council Chair 2 — Runtime & Debugging Reviewer

**Date:** 2026-07-11  
**Audit:** 10-Agent Swarm Codebase Audit — MemorySmith.Agent (Sprint 60)  
**Role:** Verify P0/P1 findings against actual source code; identify logging gaps and runtime failure modes

---

## Methodology

Each finding was verified by reading the relevant source code files, tracing execution paths, and comparing claims against actual implementation. Where claims are inaccurate, corrected descriptions are provided. Where verification is not possible from code alone (e.g., race conditions that require runtime reproduction), the finding is annotated with confidence and reasoning.

---

## P0 Findings

### MSA-MF-001 (P0): mineComplete timestamp missing

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** `MineflayerAdapter/index.js` lines 1056–1065:
```js
sendEvent('mineComplete', {
    block: shortName, mined, targetCount: count,
    blockX: blockTargetPos.bx,
    blockY: blockTargetPos.by,
    blockZ: blockTargetPos.bz,
    correlationId,
});
```

No `timestamp` field is included. By contrast, the `actionProgress` event emitted at line 1039 in the SAME mining loop DOES include `timestamp: new Date().toISOString()`. The `mineComplete` event is consumed in the C# side as `MineCompleteEvent` (defined in `WorldEvents.cs` ~line 340) which does not expect a timestamp field.

**Impact:** C# side cannot compute latency between `mineComplete` emission and processing. If `mineComplete` is delayed or reordered relative to other events (e.g., `itemCollected`), there's no temporal anchor to detect the race.

**Additional concern:** The `blockTargetPos` variable `let`-declared at line 829 is set ONLY inside the while loop body. If the loop breaks early because no blocks are found (`blockNotFound` path at line 850), `blockTargetPos` remains `null` and the `mineComplete` event sends `blockX/Y/Z: undefined`. This is a secondary bug — `mineComplete` should not be emitted when nothing was mined.

**Recommendation:** Add `timestamp: new Date().toISOString()` to the `mineComplete` payload. Guard emission: only emit `mineComplete` when `mined > 0`.

---

### MSA-MF-002 (P0): connectBot resets _reconnectAttempts

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** `MineflayerAdapter/index.js` lines 311–317 and 412–428:

```js
// Line 311
let _reconnectAttempts = 0;

// Line 314 — connectBot() resets the counter
function connectBot() {
  _reconnectAttempts = 0;     // <-- THIS IS THE PROBLEM
  bot = mineflayer.createBot(botOpts);
  bot.loadPlugin(pathfinder);
  registerBotEventHandlers();
  return bot;
}

// Line 412-428 — reconnect timer handler
const delay = Math.min(
  RECONNECT_BASE_DELAY_MS * Math.pow(RECONNECT_BACKOFF_FACTOR, _reconnectAttempts),
  RECONNECT_MAX_DELAY_MS
);
_reconnectAttempts++;  // Now 1

console.log(`[mc] reconnecting in ... (attempt ${_reconnectAttempts})...`);

_reconnectTimer = setTimeout(() => {
  try {
    connectBot();   // <-- Resets _reconnectAttempts BACK TO 0
    logStructured('info', 'reconnect', 'reconnected', { attempt: _reconnectAttempts });
    // Logs attempt: 0 instead of actual attempt number!
```

**Impact (severe):** The exponential backoff is completely defeated. Every reconnect cycle starts at `2^0 * 2000ms = 2000ms` regardless of how many previous reconnection attempts have occurred. Under rapid disconnect/reconnect cycles (e.g., server instability), the bot DDoSs the server with aggressive reconnection instead of backing off.

Additionally, the reconnect event sent at line 428 reports `attempt: 0`, making post-hoc debugging impossible — all reconnection events look like "first attempt."

**Root cause:** `connectBot()` was designed for initial startup where resetting the counter is correct behavior. But it's reused from the reconnect path without separating the concerns.

**Recommendation:** Split into `createBot()` (no counter reset) and keep `connectBot()` for initial call only, or add a `resetCounter` parameter.

---

## P1 Findings

### WSB-001 (P1): Reconnect log never reached

**Status: ⚠️ PARTIALLY CONFIRMED — description is partially inaccurate**

**Evidence:** The claim "Reconnect log never reached" is ambiguous. There are two reconnect log paths:

1. **C# WebSocketBridge** (`WebSocketBridge.cs` line 234): `_logger.LogInformation("WebSocketBridge: Reconnect succeeded (attempt {Attempt})", ...)` — THIS IS REACHABLE when the receive loop retry successfully reconnects. However, if `CloseAsync()` cancels `_receiveCts` while the retry is in `Task.Delay()`, the `OperationCanceledException` is caught at line 236 and the method returns before reaching line 234. So in the shutdown path, the log is correctly skipped.

2. **Node.js index.js** (line 427): `logStructured('info', 'reconnect', 'reconnected', { attempt: _reconnectAttempts })` — This IS reached, but always reports `attempt: 0` due to the reset bug (MSA-MF-002). So the log fires but with wrong data.

**Correction:** The finding should be restated as: "The reconnection attempt counter is always zero in reconnect logs, making the reconnection history invisible." This is a symptom of MSA-MF-002, not a separate bug.

**Impact:** Lower than P1. The reconnect process works despite the logging inaccuracy. Merge with MSA-MF-002.

**Recommendation:** Reclassify as P2 or merge into MSA-MF-002.

---

### WSB-002 (P1): _ws race condition

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** `WebSocketBridge.cs` — `_ws` is a plain `private ClientWebSocket?` field (line 44) with zero synchronization:

| Access Point | Thread | Operation |
|---|---|---|
| `ConnectAsync` (line 66) | Caller thread | `_ws = new ClientWebSocket()` |
| `CloseAsync` (line 93) | Any thread | `_ws?.State`, `_ws.CloseAsync()` |
| `SendAsync` (line 111, 138) | Any thread | `_ws?.State` check, then `_ws.SendAsync()` |
| `ReceiveLoopAsync` (line 275, 296) | Background task thread | `_ws?.State` check, then `_ws.ReceiveAsync()` |
| `RunReceiveLoopWithRetryAsync` (line 219) | Background task thread | `_ws.Dispose()`, then `_ws = new ClientWebSocket()` |
| `IsOpen` (line 58) | Any thread | `_ws?.State` |

**Race windows:**
1. **TOCTOU in SendAsync:** Line 111 checks `_ws is { State: WebSocketState.Open }`. Between that check and line 138's `await _ws.SendAsync(...)`, the receive loop's retry could dispose and replace `_ws`. Sending on the old (disposed) socket throws `ObjectDisposedException`.
2. **Concurrent dispose:** `CloseAsync` disposes `_ws.CloseAsync()` while the retry loop is also disposing it at line 217.
3. **IsOpen stale read:** `IsOpen` returns a snapshot that could be stale by the time the caller acts on it.

**Impact:** Intermittent `ObjectDisposedException` or `WebSocketException` during disconnect/reconnect transitions. Hard to reproduce, hard to debug because the exception is caught generically.

**Recommendation:** Either:
- (a) Add `lock` around all `_ws` reads/writes, or
- (b) Use `Interlocked.Exchange` for reassignment and `Volatile.Read` for reads (lighter weight), or
- (c) Introduce an atomic "generation" counter so callers detect stale socket references.

**Correction to original finding:** The description did not identify the TOCTOU window in `SendAsync` specifically — this is the most impactful race. The receive loop retry creates a NEW `_ws` without notifying `SendAsync` callers.

---

### MA-001 (P1): Empty catch on kill

**Status: ✅ CONFIRMED — REAL BUG** (violates AGENTS.md Rule E-3)

**Evidence:** `MinecraftAdapter.cs` lines 56–62:
```csharp
catch
{
    // Swallow — process may have already exited or kill may not be available.
}
```

This is inside `DisconnectAsync()`. The `catch` block has no logging whatsoever. If the `kill -TERM` command fails for any reason other than "process already exited" (e.g., permission denied, kill not found, invalid PID), the failure is invisible.

Additionally, the same pattern appears at:
- Line 65: `catch (SystemException)` around `WaitForExit` — no logging
- Line 88: `catch (InvalidOperationException)` around `Kill` — no logging

**Impact:** Silent failures during graceful shutdown. If `kill -TERM` repeatedly fails, the `SIGKILL` fallback at line 83 runs, but if THAT also fails, the process becomes orphaned. No log trail exists to diagnose this.

**Recommendation:** Replace all three empty catches with logged catches per Rule E-3:
```csharp
catch (Exception ex) when (!OperatingSystem.IsWindows())
{
    _logger?.LogWarning(ex, "Failed to send SIGTERM to Node process PID {Pid}: {Message}", pid, ex.Message);
}
```

---

### MA-003 (P1): Double-connect

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** `MinecraftAdapter.cs` lines 37–43:
```csharp
public async Task ConnectAsync(CancellationToken cancellationToken = default)
{
    if (config.AutoStartNode && !string.IsNullOrWhiteSpace(config.NodeScriptPath))
        await StartNodeProcessAsync(cancellationToken);

    _bridge = new WebSocketBridge(config.WebSocketUrl);  // <-- always creates new bridge
    await _bridge.ConnectAsync(cancellationToken, config.AdapterSecret);
}
```

If `ConnectAsync` is called twice:
1. A NEW `WebSocketBridge` is created, disconnecting the OLD one (last reference lost — finalizer-based cleanup, no deterministic dispose)
2. A NEW WebSocket connection is opened
3. The old `_nodeProcess` reference is NOT updated — if AutoStartNode re-spawns the process, the old process reference is orphaned
4. The old `_bridge` is garbage collected eventually, but its background receive loop continues running until the old WebSocket is closed by the OS

**No guard check:** There is no `if (_bridge is not null) return;` or `if (IsConnected) return;` at the method start.

**Impact:** If agent reconnection logic calls `ConnectAsync` while already connected, two background receive loops run simultaneously, both writing to their own `_inbound` channels. Events are silently duplicated or lost (the old channel's events go nowhere since the consumer is reading from the new channel).

**Recommendation:** Add a guard at the top of `ConnectAsync`:
```csharp
if (IsConnected)
{
    _logger.LogWarning("ConnectAsync called while already connected — disconnecting first");
    await DisconnectAsync(cancellationToken);
}
```

---

### MSA-MF-003 (P1): goto() timeouts

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** Every `bot.pathfinder.goto()` call in `index.js` lacks a timeout wrapper. Examples:

- Line 652: `mine` handler — `await bot.pathfinder.goto(new pfGoals.GoalNear(...))`
- Lines 1152, 1199, 1217, 1222, 1241, 1362, 1454: `place`, `move`, `findFlatArea` handlers
- Lines 1797, 1828: `craft`, `smelt` handlers

None are wrapped with `Promise.race()` or a timeout mechanism. Mineflayer's `pathfinder.goto()` can hang indefinitely when:
- `"Took too long to decide path to goal"` internal error is swallowed
- The bot is trapped in a 1×1 hole (pathfinder finds no valid path but doesn't reject)
- The server lags during chunk loading

**Contrast:** The `smelt` handler (line 1860+) correctly uses a timeout for the furnace polling:
```js
const timeout = setTimeout(
    () => reject(new Error(`Smelting timed out after ${C.SMELT_TIMEOUT_MS}ms`)),
    C.SMELT_TIMEOUT_MS
);
```

This demonstrates the pattern exists in the codebase — it just isn't applied to `goto()`.

**Impact:** A stuck `goto()` causes the entire action dispatch to hang until the C# 30-second action timeout fires. During this period, the bot is unresponsive to stop commands (the event loop is occupied with the pending goto).

**Recommendation:** Create a `withTimeout(promise, ms)` helper and wrap every `goto()` call:
```js
const GOTO_TIMEOUT_MS = 15000; // 15 seconds
async function gotoWithTimeout(goal, timeoutMs = GOTO_TIMEOUT_MS) {
    await Promise.race([
        bot.pathfinder.goto(goal),
        new Promise((_, reject) =>
            setTimeout(() => reject(new Error(`goto() timed out after ${timeoutMs}ms`)), timeoutMs)
        )
    ]);
}
```

---

### MSA-MF-004 (P1): craft/smelt stop guard

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** The `'craft'` and `'smelt'` case handlers do not:
1. Reset `_stopRequested = false` at the start
2. Check `_stopRequested` before long operations (goto, craft, furnace operations)

Compare with the pattern used in `'mine'` (line 673), `'place'` (line 1073), `'wander'` (line 1443), and `'findFlatArea'` (line 1491) — ALL of these reset `_stopRequested = false` at the start.

**Impact:** If a previous action was stopped (e.g., mine was aborted via stop command), `_stopRequested` remains `true`. The next craft or smelt action proceeds without checking:
1. `goto()` to crafting table/furnace proceeds even though stop was requested
2. `bot.craft()` or `bot.openFurnace()` proceeds
3. The `handleStop()` pathfinder cancellation at line 96 (`bot.pathfinder.setGoal(null)`) may fire DURING the craft, corrupting Mineflayer's internal state

**Recommendation:** Add `_stopRequested = false` at the start of both `'craft'` and `'smelt'` cases, matching the established pattern. Add `if (_stopRequested) return;` checks before each long-running operation.

---

### MSA-MF-005 (P1): stopState.js unused

**Status: ✅ CONFIRMED — DEAD CODE**

**Evidence:** `stopState.js` exports `createStopState()` but is NEVER imported anywhere in the codebase.

```
grep "import.*stopState\|require.*stopState" **/*.js  →  No results (outside stopState.js itself)
```

The stop logic lives inline in `index.js`:
- `_stopRequested` (line 73) — module-level variable
- `handleStop()` (line 93) — inline function
- All stop checks reference the inline `_stopRequested` variable

The module appears to have been extracted during Sprint 52 modularization (TSK-0166) but never wired into the main adapter.

**Impact:** Maintenance trap. Anyone finding `stopState.js` would reasonably assume it's part of the system. Changes to the inline stop logic in `index.js` will silently diverge from the module.

**Recommendation:** Either:
- (a) Wire `stopState.js` into `index.js` and remove the inline state, OR
- (b) Delete `stopState.js` and add a comment in `index.js` noting the extraction was attempted but reverted

---

### MSA-MF-006 (P1): playerCollect fallback chain wrong

**Status: ✅ CONFIRMED — FALLBACK CHAIN IS INCORRECT**

**Evidence:** `index.js` line 349:
```js
const itemName = entity?.metadata?.name ?? entity?.displayName ?? 'unknown';
```

The **AGENTS.md** (Rule section `playerCollect guard`) specifies:
```js
entity?.metadata?.find(m => m?.value?.name)?.value?.name ?? entity?.name ?? 'unknown'
```

**Problems with the actual code:**

1. **`entity?.metadata?.name` is always undefined**: Mineflayer's `entity.metadata` is an **array** (of metadata entries indexed by entity type), not an object with a `.name` property. `entity?.metadata?.name` on an array returns `undefined` in JavaScript. The correct access pattern requires `.find()` to locate the metadata entry that has a `.value?.name`.

2. **`entity?.displayName` vs `entity?.name`**: The AGENTS.md specifies `entity?.name` as the second fallback. The code uses `entity?.displayName` instead. These are different properties — `entity.name` is the raw entity type name (e.g., "minecraft:stone"), while `entity.displayName` is the localized display name (e.g., "Stone"). Using `displayName` means the C# side receives localized strings instead of canonical IDs, breaking item mapping.

**Impact:** The `itemName` is always `'unknown'` because `entity?.metadata?.name` is never defined on an array. `entity?.displayName` may work for some items but provides localized strings that downstream C# code (`ApplyBlockMined`, inventory tracking) can't match to canonical item IDs.

**Recommendation:** Fix the fallback chain to match AGENTS.md:
```js
const itemName = entity?.metadata?.find(m => m?.value?.name)?.value?.name
    ?? entity?.name ?? 'unknown';
```

---

### MSA-MF-007 (P1): Event listener leak on reconnect

**Status: ⚠️ PARTIALLY CONFIRMED — lower severity than P1**

**Evidence:** `connectBot()` at line 314–317 calls `registerBotEventHandlers()` which registers event listeners on the NEW bot instance via `bot.on(...)`. There is no `bot.removeAllListeners()` or cleanup of the OLD bot's listeners.

However, in practice:
1. The old bot is disconnected (connection dropped), so its event handlers won't fire for new events.
2. The `bot` variable is reassigned, so the old instance is unreferenced and eligible for GC.
3. Mineflayer's `createBot()` creates a fresh TCP connection — the old bot's listeners can't interfere with the new connection.

**The real concern is subtler:** `_reconnectTimer` is a `let` variable. If bot A's 'end' handler fires, it schedules `_reconnectTimer = setTimeout(...)`. Before the timer fires, `connectBot()` is called (creating bot B). If bot A's 'end' handler somehow fires AGAIN (impossible in normal Mineflayer — 'end' fires once), it would overwrite the timer, but bot B is already running. In practice this can't happen.

**However, the `physicsTick` listeners (lines 356-357) deserve scrutiny:** If `registerBotEventHandlers()` is somehow called TWICE on the same bot (e.g., via a race in reconnect), `scanNearbyEntities` and `scanBlockBelow` would be registered twice, causing duplicate processing. But since a new bot is always created on reconnect, this is a new instance.

**Verdict:** The theoretical leak exists but has no practical impact in the current code. The real issue is that if someone calls `connectBot()` twice without recreating the bot (defensive issue), listeners accumulate.

**Recommendation:** Add `bot.removeAllListeners()` at the start of `registerBotEventHandlers()` for defense-in-depth. Reclassify as P2 — pure defensive hygiene.

---

### MSA-MF-008 (P1): Scan cooldowns not reset on reconnect

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** `index.js` lines 494-495:
```js
let _lastBlockScanAt = 0;
let _lastEntityScanAt = 0;
```

These are module-level variables initialized ONCE at module load. `connectBot()` does NOT reset them.

**Impact:** After a disconnect/reconnect cycle:
- If `_lastEntityScanAt` was set to, say, `Date.now() - 500` (500ms ago) just before disconnect
- And the bot reconnects after 1 second
- The cooldown check at line 501: `now - _lastEntityScanAt < C.ENTITY_SCAN_COOLDOWN_MS` would compute `1000 - 500 = 500ms < 5000ms` (assuming 5s cooldown)
- Result: First scan on the new connection is SKIPPED. The bot waits 4 more seconds before scanning.

This delay means entities that appeared during the disconnect (e.g., a creeper that wandered into range) go undetected for longer.

**Recommendation:** Reset both cooldowns at the start of `connectBot()`:
```js
_lastBlockScanAt = 0;
_lastEntityScanAt = 0;
```

---

### TOOL-012 (P1): FindReachableBlock no ITool

**Status: ✅ CONFIRMED — REAL GAP**

**Evidence:**
- `ActionProtocol.cs` declares `FindReachableBlock = "findReachableBlock"` 
- `index.js` has a complete `case 'findReachableBlock'` handler (line ~1906) with pathfinding, sorting, and event emission
- `AgentBackgroundService.cs` handles the response events (lines 1108, 1849)
- `WorldEvents.cs` defines `ReachableBlockFoundEvent` (line ~227)
- **No `ITool` implementation exists** — `file_search **/Tools/*Reachable*` returns empty
- The 14 existing `ITool` implementations (ChatTool, CraftItemTool, PlaceBlockTool, etc.) do not include FindReachableBlock

**Impact:** The adapter and event system support find-reachable-block end-to-end, but the planning layer cannot invoke it because no tool is registered. The planner would need to use a combination of `QueryBlocksTool` + implicit reachability assumptions, which is less accurate.

**Recommendation:** Create `FindReachableBlockTool : ITool` that dispatches `ActionProtocol.FindReachableBlock` and register it in `Program.cs` alongside the other tools.

---

### TOOL-014 (P1): GetInt32 vs TryGetInt32

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** `WanderTool.cs` lines 31-32:
```csharp
var radius  = arguments.TryGetProperty("radius",             out var r) ? r.GetInt32() : 20;
var maxDist = arguments.TryGetProperty("maxDistanceFromSpawn", out var m) ? m.GetInt32() : 100;
```

Uses `GetInt32()` after `TryGetProperty()`. If the LLM passes a non-integer value (e.g., `"radius": "20"` as string, or `"radius": 2e1` as scientific notation), `GetInt32()` throws a `JsonException`. The same pattern appears in:
- `CraftItemTool.cs` line 42: `countEl.GetInt32()`
- `FurnaceTool.cs` line 48: `countEl.GetInt32()`
- `MineBlockTool.cs` line 34: `countEl.GetInt32()`
- `MoveToTool.cs` lines 42, 48: `xEl.GetInt32()` etc.
- `PlaceBlockTool.cs` lines 42-44: `xEl.GetInt32()` etc.

Compare with the safe pattern in `FindFlatAreaTool.cs` and `QueryBlocksTool.cs` which use `TryGetInt32()`.

**Impact:** LLM-generated parameters with scientific notation or string-encoded numbers cause unhandled `JsonException` crashes.

**Recommendation:** Replace all `GetInt32()` calls with `TryGetInt32()` across all tool implementations. Create a helper extension method:
```csharp
public static int GetInt32Safe(this JsonElement el, int defaultValue = 0)
    => el.TryGetInt32(out var v) ? v : defaultValue;
```

---

### WBLZ-001 (P1): /api/about version hardcoded

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** `WebUI.Blazor/Program.cs` line 536:
```csharp
Version = "0.55.0",
```

Hardcoded to Sprint 55 version despite codebase being at Sprint 60. By contrast, the startup log at line 477 correctly derives version from assembly metadata:
```csharp
var agentVersion = System.Diagnostics.FileVersionInfo
    .GetVersionInfo(typeof(Program).Assembly.Location)
    .ProductVersion ?? "unknown";
```

The `/api/about` endpoint shows stale version to all consumers (dashboard, API clients, monitoring).

**Recommendation:** Replace literal with derived value:
```csharp
Version = System.Diagnostics.FileVersionInfo
    .GetVersionInfo(typeof(Program).Assembly.Location)
    .ProductVersion ?? "0.0.0",
```

---

### WBLZ-002 (P1): SignalR no auth

**Status: ✅ CONFIRMED — REAL BUG**

**Evidence:** `WebUI.Blazor/Program.cs`:
- Line 365: `builder.Services.AddSignalR();` — no authentication services configured
- Line 527: `app.MapHub<AgentHub>("/agent-hub");` — no `.RequireAuthorization()` call

Grep for `AddAuthentication`, `UseAuthentication`, `RequireAuthorization` in `Program.cs` returns **zero results**.

The API key middleware (`ApiKeyMiddleware`) is only applied to `/api` path branches (line ~468). The SignalR hub at `/agent-hub` is completely unprotected.

**Impact:** Any client that can reach the WebSocket endpoint can:
1. Connect to `/agent-hub` and receive all dashboard push events (agent position, health, inventory, active goal)
2. Potentially invoke hub methods if any are defined that accept external input

This is information disclosure — the same severity as the Gemini API key finding (MSA-LLM-005).

**Recommendation:** Add `.RequireAuthorization()` to the hub mapping and configure authentication services:
```csharp
builder.Services.AddAuthentication(...);
app.MapHub<AgentHub>("/agent-hub").RequireAuthorization();
```

---

### INFRA-002/003 (P1): CI vulnerability checks

**Status: ✅ CONFIRMED — REAL GAP**

**Evidence:** `.github/workflows/ci.yml` — the CI workflow runs:
1. `dotnet restore`
2. `dotnet build`
3. `dotnet test`
4. `pwsh ./Scripts/Test-TaskRecords.ps1`

There is NO `dotnet list package --vulnerable` step. The AGENTS.md Package Vetting Policy (P-3) states:
> Vulnerable packages are a P0 blocker — `dotnet list package --vulnerable` must return zero results.

But there's no CI enforcement of this policy. The `Directory.Build.props` has `<WarningsNotAsErrors>NU1903</WarningsNotAsErrors>` which prevents NU1903 from breaking the build, but this is described as a "policy exemption for transitive dependency advisories" — not a suppression. Without a CI step, vulnerable transitive deps can be introduced without detection.

**Recommendation:** Add to CI:
```yaml
- name: Check for vulnerable packages
  run: >
    dotnet list package --vulnerable --include-transitive 2>&1
    | Select-String -Pattern "has no vulnerable packages"
    | ForEach-Object { if (!$_) { throw "Vulnerable packages found" } }
```

---

### BUILD-001 (P1): NU1903 exemption

**Status: ✅ CONFIRMED — REAL OBSERVABILITY GAP**

**Evidence:** `Directory.Build.props`:
```xml
<WarningsNotAsErrors>NU1903</WarningsNotAsErrors>
```

The comment claims this "is NOT a suppression; it is a policy exemption for transitive dependency advisories that cannot be fixed at our layer." This is semantically a suppression — it prevents the build from failing on NU1903, which is the mechanism that would force transitive vulnerability remediation.

**Impact:** The exemption means vulnerable transitive dependencies can be introduced without breaking CI. The NU1903 warning still appears in build output (not suppressed via `<NoWarn>`), but there's no mechanism to track or flag it. Human operators must manually inspect every build log.

**Recommendation:** Either:
- (a) Remove the exemption and fix NU1903 violations (pin safe transitive versions), or
- (b) Add a separate CI step that explicitly checks for NU1903 and fails the build if found (same as INFRA-002/003)

---

### MSA-ABS-004/005 (P2 but critical-like): Race conditions in counters

**Status: ⚠️ CONFIRMED — REAL BUT CORRECTLY RATED AT P2**

**Evidence:** Multiple mutable fields in `AgentBackgroundService.cs` are accessed from both `DispatchActionsAsync` and `ProcessEventsAsync` without synchronization:

| Field | Written from | Read from |
|---|---|---|
| `_consecutiveFailures` (line 237) | DispatchActionsAsync (line 1124, 1910) + ProcessEventsAsync (line 329, 421) | DispatchActionsAsync (line 1914) |
| `_blocksPlacedThisCycle` (line 252) | ProcessEventsAsync (line 921: `++`) + DispatchActionsAsync (line 2107: `= 0`) | Logging |
| `_actionDispatchedThisCycle` (line 248) | DispatchActionsAsync (line 2104: `= false`) | DispatchActionsAsync (line 1866) |
| `_cycleOutcomes` (line 224) | ConcurrentQueue — safe | ConcurrentQueue — safe |

**Critical-like but actually P2 because:**
1. These counters are used for progress tracking and logging, not for correctness-critical decisions
2. `_consecutiveFailures` being briefly stale could cause one extra replan cycle, not data corruption
3. `_blocksPlacedThisCycle` being off by 1 only affects progress log messages
4. The `_pendingLock` protects `_pendingActions` correctly

**However, there's a subtle issue with `_consecutiveFailures`:** If `DispatchActionsAsync` reads `_consecutiveFailures >= maxFailures` (line 1914) as `false` (because `ProcessEventsAsync` just reset it to 0), then a genuine failure goes undetected, and the agent continues looping on a failed goal for another cycle. This delays failure detection but does not cause infinite loops (the `HasFailed()` check on the goal itself is the primary guard).

**Recommendation:** Change `_consecutiveFailures` to use `Interlocked.Increment` / `Interlocked.Exchange` for safety. Add a comment noting the race window is acceptable for logging counters but should be avoided for correctness-critical paths.

---

### INFRA-007 (P2): No secret scanning in CI

**Status: ✅ CONFIRMED — REAL GAP**

**Evidence:** `.github/workflows/ci.yml` has zero secret-scanning or credential-checking steps. No `trufflehog`, `gitleaks`, or GitHub secret scanning integration in the workflow. No pre-commit hook is configured.

Given that MSA-LLM-005 (Gemini API key in URL query string) was a real finding, the absence of automated secret scanning means similar leaks can persist undetected.

**Recommendation:** Add a `gitleaks` or `trufflehog` scan step to CI. Configure GitHub's built-in secret scanning for the repository.

---

## Cross-Cutting Observations

### 1. Logging consistency gap

Events emitted from `index.js` have inconsistent timestamp inclusion:
- `actionStarted` (line 631): ✅ has `timestamp`
- `actionProgress` (line 1039): ✅ has `timestamp`
- `mineComplete` (line 1056): ❌ NO timestamp
- `blockMined` (line 1003-1015): ❌ NO timestamp
- `mineAborted` (lines 839, 878, 1041): ❌ NO timestamp

**Recommendation:** Add timestamps to ALL events at emission time in `index.js` for consistency and debugging.

### 2. Multiple code paths for the same concern

The `DisconnectAsync` in `MinecraftAdapter.cs` has three separate try/catch blocks for the same shutdown sequence (SIGTERM → WaitForExit → SIGKILL). Each has a different exception handling style (empty catch, `SystemException` filter, `InvalidOperationException` filter). This makes it hard to reason about the shutdown reliability.

### 3. Adapter-side state vs C#-side state

Several findings (MSA-MF-002, MSA-MF-008, MSA-MF-007) involve module-level state in `index.js` that isn't reset on reconnect. There's no systematic reset mechanism — each state variable must be manually accounted for. A `resetState()` function called at the start of `connectBot()` would prevent future bugs of this class.

---

## Summary Table

| Finding | Original Severity | Verified? | Corrected Severity | Notes |
|---|---|---|---|---|
| MSA-MF-001 | P0 | ✅ Confirmed | P0 | mineComplete missing timestamp + null blockTargetPos race |
| MSA-MF-002 | P0 | ✅ Confirmed | P0 | Backoff completely defeated |
| WSB-001 | P1 | ⚠️ Partially | P2 | Symptom of MSA-MF-002; merge recommended |
| WSB-002 | P1 | ✅ Confirmed | P1 | TOCTOU in SendAsync is most impactful |
| MA-001 | P1 | ✅ Confirmed | P1 | Triple empty catch, violates Rule E-3 |
| MA-003 | P1 | ✅ Confirmed | P1 | No guard; leaks bridge + channel |
| MSA-MF-003 | P1 | ✅ Confirmed | P1 | Pattern exists (smelt) but not applied to goto |
| MSA-MF-004 | P1 | ✅ Confirmed | P1 | Missing reset + missing checks |
| MSA-MF-005 | P1 | ✅ Confirmed | P1 | Dead code, maintenance trap |
| MSA-MF-006 | P1 | ✅ Confirmed | P1 | wrong metadata access + wrong fallback |
| MSA-MF-007 | P1 | ⚠️ Partially | P2 | Theoretical only; recommend defense-in-depth |
| MSA-MF-008 | P1 | ✅ Confirmed | P1 | Delayed first scan after reconnect |
| TOOL-012 | P1 | ✅ Confirmed | P1 | ActionProtocol + adapter + ABS handler exist, no ITool |
| TOOL-014 | P1 | ✅ Confirmed | P1 | Affects 6 tools; pattern exists in others |
| WBLZ-001 | P1 | ✅ Confirmed | P1 | Hardcoded stale version |
| WBLZ-002 | P1 | ✅ Confirmed | P1 | SignalR hub completely unauthenticated |
| INFRA-002/003 | P1 | ✅ Confirmed | P1 | No CI enforcement of dep vuln policy |
| BUILD-001 | P1 | ✅ Confirmed | P1 | NU1903 exemption = de facto suppression |
| MSA-ABS-004/005 | P2 | ✅ Confirmed | P2 | Real races, logging-impact only |
| INFRA-007 | P2 | ✅ Confirmed | P2 | No secret scanning in CI |

**Confidence:** High (all findings verified against actual source code)
