# MemorySmith.Agent Sprint 45 Handoff

## What Was Done

### Wave A (cold-open, 4 tasks)
| Task | Description | Files Changed | Tests |
|------|-------------|---------------|-------|
| TSK-0087 | Fix OriginZ→OriginY typo + all-3-axes | `IntentManager.cs`, `Sprint44Tests.cs` | 3 new |
| TSK-0090 | Empty pageId guard in GetPageTool | `GetPageTool.cs` | — |
| TSK-0091 | Thread.Sleep(200)→await Task.Delay | `AgentBackgroundService.cs` | — |
| TSK-0088 | try/catch around gateway calls | `MemorySmithBlueprintRepository.cs`, `MemorySmithItemRegistry.cs`, `Program.cs` | — |

### Wave B (3 tasks)
| Task | Description | Files Changed | Tests |
|------|-------------|---------------|-------|
| TSK-0094 | Blueprint Id validation in GetAsync | `MemorySmithBlueprintRepository.cs`, `Sprint44Tests.cs` | 2 new |
| TSK-0092 | Null-specific cache TTL (5s default) | `RestMemoryGatewayOptions.cs`, `MemorySmithItemRegistry.cs`, `ItemRegistryTests.cs` | 1 new |
| TSK-0089 | Nav contract — clarified dead code, no prompt change needed | `IntentManager.cs` | — |

### Deferred to Sprint 46+
| Task | Rationale |
|------|-----------|
| TSK-0093 | Breaking API change (16 test callers), no production benefit |
| TSK-0096 | Documented tradeoff — need evidence of real-world harm first |
| TSK-0098 | Low impact, batch with BuildGoal refactoring |
| TSK-0099 | P2, batch with prompt cleanup |
| TSK-0083 | Partially done; complete remaining ~50% |
| TSK-0084 | Needs Wave B completion first |
| TSK-0085 | P2, impacts smelt/craft equally |

## Build & Test Results
- `dotnet build`: 0 warnings, 0 errors
- `dotnet test`: **644/644 passed** (up from 641 — 3 new TSK-0087 tests + 2 TSK-0094 tests + 1 TSK-0092 test)

## Key Architectural Discoveries

### TSK-0089 Navigate path
Navigate intents are handled **directly** in `HandleChatEventAsync`'s `case "navigate"` — they **never** reach `IntentManager.BuildGoalRequest`. The IntentManager navigate case is secondary/dead code. The LLM prompt instruction "set coords to null" is actually **functionally correct** — `HandleChatEventAsync` falls back to `chat.PlayerPos` when coords are null.

### TSK-0091 Thread.Sleep extraction
`SetGoal` is `public void`, not `async Task`. Creative provisioning was extracted to `ProvisionGoalIfCreativeAsync` (private async Task) and called fire-and-forget. The `ActionQueue` is thread-safe.

### TSK-0088 Gateway fallback
Transport exceptions (`HttpRequestException`, `TaskCanceledException`) are caught and logged, allowing the existing local file fallback chain to execute. `OperationCanceledException` from cancellation tokens is NOT swallowed.

## Council Records
- **Wave A+B council:** `Data/Pages/council/sprint45-waveb-council-20260623.md` (wiki)
- **Prioritization council:** `Data/Pages/council/audit-task-prioritization-council-2026-06-24.md`

## Open Items for Next Sprint
1. **TSK-0093**: Reconsider if a consumer appears that needs structured parse errors
2. **TSK-0096**: Gather log evidence of real inventory drift before implementing dedup
3. **TSK-0083**: Sprint44Tests covers ~50% of checkpoint scenarios — complete the rest
4. **TSK-0084**: Wire `ApplySmeltComplete` in `WorldStateProjector` now that smelt pipeline works

---

# MemorySmith.Agent Deep Dive Review

## Mineflayer Adapter, Observation Architecture, Build/Gather Reliability, and Next Sprint Guidance

**Repository Reviewed:** [MemorySmith.Agent](https://github.com/TheMasonX/MemorySmith.Agent?utm_source=chatgpt.com)
**Focus:** Mineflayer adapter, build/gather execution, observation loops, intent interpreter direction, Sprint 35 LLM-first architecture alignment.

---

# Executive Summary

The project is improving substantially.

Compared to earlier audits, I no longer believe the biggest risk is the planner.

I believe the biggest risk is now:

> **The agent can execute actions, but it still struggles to prove that those actions actually changed the world in the way the plan expected.**

This distinction matters.

Most agent systems fail because:

1. Planner makes bad plans
2. Executor fails to execute

MemorySmith is increasingly reaching a third category:

3. Executor acts, but the system cannot reliably determine whether reality matches expectations afterward.

That is exactly the class of issue you're seeing:

* placing into dirt
* placing against invalid faces
* inventory assumptions becoming stale
* build plans continuing despite missing blocks
* structures ending incomplete
* gather plans mining wrong things
* action succeeded technically but not semantically

The architecture is evolving toward:

```
Intent
  ↓
Plan
  ↓
Execute
  ↓
Observe
  ↓
Evaluate
  ↓
Replan
```

But today it is still closer to:

```
Intent
  ↓
Plan
  ↓
Execute
  ↓
Assume Success
```

The next sprint should primarily close that gap.

---

# Current Architectural State

I would describe MemorySmith today as:

### Planning Layer

Status: Good

The HTN decomposition system is becoming increasingly reasonable.

It has:

* gather decomposition
* build decomposition
* inventory checks
* plan fingerprints
* stall recovery
* task validation

The planner is no longer the largest source of brittleness.

Confidence: 82%

---

### Intent Layer

Status: Improving

The LLM-first direction is correct.

The current regex-heavy interpretation system is increasingly becoming a bottleneck.

The future architecture should likely become:

```
User Input
  ↓
Intent Draft
  ↓
Goal Selection
  ↓
Planning
```

instead of:

```
User Input
  ↓
Regex
  ↓
Goal
```

Current confidence: 75%

Direction confidence: 93%

---

### Execution Layer

Status: Good

Mineflayer adapter has matured significantly.

Compared to prior reviews:

* mining improved
* pathfinding improved
* stop commands improved
* flat area scanning improved
* inventory refresh improved

The adapter is no longer obviously broken.

Confidence: 85%

---

### Observation Layer

Status: Weak

This is where most future work belongs.

Confidence: 95%

---

# Mineflayer Adapter Deep Dive

## What The Adapter Does Well

The adapter has become a fairly capable action runtime.

Current responsibilities include:

* websocket transport
* action queue
* movement
* mining
* placing
* crafting
* smelting
* wandering
* status refresh
* chat forwarding
* event emission

This is good separation.

The adapter should remain:

> "A world interaction layer"

not

> "An AI decision layer"

The adapter should not become smarter.

It should become more observable.

---

# Major Finding #1

## PlaceBlock Is Still Optimistic

Current behavior:

```
Move
↓
Equip
↓
PlaceBlock
↓
Emit blockPlaced
```

The issue:

```
blockPlaced
≠
block actually exists there
```

These are fundamentally different facts.

Possible situations:

### Case A

Mineflayer succeeds

Block exists

Everything fine

---

### Case B

Mineflayer succeeds

Chunk updates late

Block not actually present

Agent thinks build progressed

---

### Case C

Neighbor update destroys placement

Agent thinks build progressed

Blueprint now wrong

---

### Case D

Player breaks block

Agent never notices

Blueprint becomes invalid

---

### Case E

Wrong block selected

Placement succeeds

Wrong structure produced

Agent believes success

---

This is currently the single highest-value improvement opportunity.

Confidence: 96%

---

# Major Finding #2

## Build Verification Does Not Exist

The system verifies:

```
Did we execute placement?
```

What it does NOT verify:

```
Does the world match the blueprint?
```

These are completely different questions.

The build system needs:

### Blueprint Reconciliation

Input:

```
Expected:
(0,0,0) oak_planks
(1,0,0) oak_planks
(2,0,0) oak_planks
```

Observed:

```
(0,0,0) oak_planks
(1,0,0) air
(2,0,0) oak_planks
```

Result:

```
Missing block detected
Build incomplete
```

Today that capability largely does not exist.

Confidence: 95%

---

# Major Finding #3

## Observation Is Event-Based Instead Of State-Based

Current architecture:

```
Event happened
→ Assume world changed
```

Preferred architecture:

```
Event happened
→ Observe world
→ Compare expectation
→ Update state
```

This distinction becomes critical as complexity grows.

---

# Example

Current:

```
PlaceBlock
↓
blockPlaced
↓
advance plan
```

Future:

```
PlaceBlock
↓
blockPlaced
↓
observe target coordinate
↓
compare expected block
↓
verified
↓
advance plan
```

This single change would eliminate many silent failures.

Confidence: 94%

---

# Major Finding #4

## The Agent Still Has No Real Failure Diagnosis

Current failures are mostly:

```
Path failed
Place failed
Mine failed
```

The system doesn't understand:

```
WHY
```

Example:

Agent tries to place.

Reality:

```
Target occupied by dirt
```

Current result:

```
Place failed
```

Desired result:

```
Place failed

Observed:
Target occupied by dirt

Recovery:
Dig dirt
Retry placement
```

This is where the observation layer should evolve.

Confidence: 92%

---

# Major Finding #5

## Gathering Is Still Inventory-Centric

Gathering currently reasons mostly through inventory deltas.

Example:

```
Need 10 logs

Inventory:
8 logs

Mine 2 more
```

That's good.

But future gathering should additionally reason about:

### Resource Availability

Example:

```
Need oak logs

Nearby:
0

Known location:
forest 200 blocks east
```

The system currently lacks persistent world resource memory.

This becomes important for longer sessions.

Confidence: 88%

---

# Architectural Recommendation

## Introduce ActionOutcome

This may become one of the most valuable abstractions in the codebase.

Instead of:

```csharp
ToolResult
```

Use:

```csharp
ActionOutcome
```

Containing:

```csharp
RequestedAction

ObservedEffects

ExpectedEffects

Success

Confidence

RecoverySuggestions
```

Example:

```json
{
  "action":"placeBlock",

  "expected":
  {
      "oak_planks": "(10,64,10)"
  },

  "observed":
  {
      "actualBlock":"dirt"
  },

  "success":false,

  "recovery":
  [
      "mine dirt",
      "retry place"
  ]
}
```

This becomes the bridge between execution and planning.

Confidence: 94%

---

# Architectural Recommendation

## Introduce Build Verification Service

New subsystem:

```csharp
BuildVerifier
```

Responsibilities:

### Verify Blueprint

Compare:

```
Blueprint
vs
Observed World
```

---

### Generate Diffs

Output:

```json
{
  "missing": [...]
  "wrong": [...]
  "extra": [...]
}
```

---

### Support Repair

Planner can consume diff.

Example:

```
Missing block
↓
Place block
```

or

```
Wrong block
↓
Replace block
```

This creates self-healing builds.

Confidence: 97%

---

# Architectural Recommendation

## Introduce World Assertions

Planner should be able to express:

```csharp
AssertBlockAt(...)
AssertInventoryContains(...)
AssertStandingAt(...)
```

Actions should satisfy assertions.

Not just execute commands.

Example:

```csharp
AssertBlockAt(
  oak_planks,
  x,y,z)
```

Action succeeds only when assertion becomes true.

This is a major reliability improvement.

Confidence: 95%

---

# Mineflayer Adapter Specific Improvements

## P0

Post-placement verification

Add:

```javascript
world.getBlock(...)
```

after placement.

Verify actual block.

Emit verification result.

---

## P0

Block read API

Add adapter command:

```javascript
getBlockAt
```

Returns:

```json
{
  block:"oak_planks",
  x:10,
  y:64,
  z:10
}
```

This unlocks blueprint reconciliation.

---

## P0

Placement diagnostics

Emit:

```json
{
  reason:
    "occupied_by_dirt"
}
```

instead of generic failure.

---

## P1

Build scan API

Input:

```json
{
  center,
  radius
}
```

Output:

Observed blocks.

Useful for verification.

---

## P1

Resource scan API

Allows gather tasks to reason about nearby resources.

---

# Things I Would NOT Do Next Sprint

Avoid:

### Rewriting planner

Not biggest issue anymore.

---

### Rewriting HTN

Current HTN is sufficient.

---

### Replacing Mineflayer

Not warranted.

---

### More regex work

Diminishing returns.

---

### More prompt engineering

Observation problems cannot be solved with prompts.

This is a systems problem.

---

# Suggested Sprint Priority Order

### Sprint Priority #1

World verification

### Sprint Priority #2

Placement diagnostics

### Sprint Priority #3

Blueprint reconciliation

### Sprint Priority #4

ActionOutcome architecture

### Sprint Priority #5

IntentDraft architecture

### Sprint Priority #6

Persistent world knowledge

---

# Final Assessment

If I were handing this sprint to another engineer, I would summarize it as:

> MemorySmith's planner is becoming good enough. The Mineflayer adapter is becoming good enough. The next bottleneck is epistemology: how the agent knows whether reality matches the plan.

The project's biggest weakness is no longer action generation.

It is **ground truth acquisition**.

The next sprint should focus on transforming:

```
Action → Assume Success
```

into:

```
Action
  ↓
Observe
  ↓
Verify
  ↓
Update World Model
  ↓
Continue/Replan
```

That single architectural shift will likely eliminate a large percentage of the "brittle and silent failure" behavior you're still seeing.

### Confidence Summary

| Finding                                                         | Confidence |
| --------------------------------------------------------------- | ---------- |
| Placement verification is the highest-value improvement         | 96%        |
| Build completion is currently too optimistic                    | 95%        |
| Observation layer is now the primary bottleneck                 | 95%        |
| Planner is no longer the largest issue                          | 82%        |
| HTN architecture should remain in place                         | 88%        |
| ActionOutcome abstraction would materially improve architecture | 94%        |
| Blueprint reconciliation should be a P0 feature                 | 97%        |
| LLM-first direction remains correct                             | 93%        |
