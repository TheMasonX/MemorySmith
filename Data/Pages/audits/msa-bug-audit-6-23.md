# MemorySmith.Agent Follow-Up Audit

## Hidden Runtime Semantics, Reliability Risks, and Sprint Guidance

**Repository:** MemorySmith.Agent
**Review Scope:** Post-SmeltGoal implementation, SearchMemory removal, placement verification work, Mineflayer adapter execution semantics, event processing, and runtime recovery behavior.

---

# Executive Summary

The architecture continues moving in the correct direction.

The project is no longer primarily suffering from poor decomposition.

Instead, it is increasingly suffering from **semantic drift between planning and execution**.

The planner often believes something happened because:

* an action completed
* an event arrived
* a tool reported success

while the world itself may not yet satisfy the intended outcome.

This review uncovered several concrete issues that could contribute to the remaining brittleness users are observing:

1. Smelting appears capable of reporting completion before the requested quantity is actually produced.
2. Placement skips are treated too similarly to successful placements.
3. Creative-mode provisioning uses blocking sleeps inside asynchronous execution paths.
4. Smelting still depends heavily on later status reconciliation rather than immediate inventory truth.
5. Several new code paths rely on optimistic event semantics rather than verified world state.

The codebase is increasingly capable.

The remaining work is mostly about **trustworthiness**.

---

# New Finding 1

## Smelt Completion Appears Optimistic

### Observed Pattern

The smelt flow appears to:

1. Locate furnace
2. Insert fuel
3. Insert input
4. Wait for furnace output
5. Take output
6. Emit completion

The issue is that completion appears tied to the first successful output appearance rather than proving the requested quantity was actually produced.

---

### Example

Request:

```text
Smelt 32 iron ore
```

Potential actual behavior:

```text
8 iron ore smelted
output appears
completion event emitted
```

Planner view:

```text
32 iron ingots complete
```

World reality:

```text
8 iron ingots complete
24 remaining
```

---

### Risk

This can create:

* phantom completion
* repeated gather loops
* inconsistent inventory reasoning
* confusing debugging sessions

---

### Recommendation

Replace:

```text
First output observed
→ Complete
```

With:

```text
Requested quantity satisfied
→ Complete
```

Add:

```text
PartialCompletion
```

as an explicit result type.

---

### Confidence

95%

---

# New Finding 2

## Smelt Fuel Logic Appears Single-Batch Oriented

### Observed Pattern

Current smelt setup appears to insert fuel once.

The implementation appears optimized around:

```text
one setup
one wait
one completion
```

rather than:

```text
arbitrary batch size
```

---

### Consequences

Large smelt requests may:

* partially complete
* stall
* timeout
* require repeated retries

depending on inventory state.

---

### Recommendation

Introduce:

```csharp
SmeltExecutionSession
```

which tracks:

* requested quantity
* produced quantity
* fuel remaining
* furnace status

until complete.

---

### Confidence

93%

---

# New Finding 3

## Smelt Input Validation Is Too Permissive

### Observed Pattern

Current architecture appears willing to construct:

```text
SmeltItem:{anything}
```

goals.

Fallback behavior generally attempts to continue.

---

### Problem

Invalid requests should fail immediately.

Examples:

```text
Smelt cobblestone
```

valid

```text
Smelt oak_log
```

valid

```text
Smelt crafting_table
```

should fail

---

Current behavior appears closer to:

```text
Try anyway
```

than:

```text
Reject invalid goal
```

---

### Recommendation

Add:

```csharp
ISmeltRecipeRegistry
```

or equivalent validation layer.

---

### Confidence

90%

---

# New Finding 4

## Placement Skip Semantics Are Weak

### Observed Pattern

The new:

```text
blockPlaceSkipped
```

path is a good addition.

However it still behaves too much like success.

---

### Current Reality

The action lifecycle may effectively become:

```text
PlaceBlock
↓
Skipped
↓
Action Complete
```

rather than:

```text
PlaceBlock
↓
Skipped
↓
Recoverable Failure
```

---

### Why This Matters

The planner loses useful information.

A skipped placement is valuable evidence.

It should drive:

* excavation
* relocation
* replanning

---

### Recommendation

Create:

```csharp
PlacementFailureReason
```

Examples:

```text
OccupiedByTerrain
OccupiedByPlayerBuild
NoLineOfSight
NoSupportBlock
WrongDimension
```

---

### Confidence

92%

---

# New Finding 5

## Async Blocking Exists In Goal Provisioning

### Observed Pattern

Creative-mode provisioning currently uses blocking waits.

Equivalent behavior:

```csharp
Thread.Sleep(...)
```

inside async workflow.

---

### Why This Matters

Blocking:

* reduces responsiveness
* delays event processing
* wastes worker threads

This is a reliability concern rather than merely a style concern.

---

### Recommendation

Replace with:

```csharp
await Task.Delay(...)
```

throughout provisioning code.

---

### Confidence

94%

---

# New Finding 6

## Smelt Success Depends Too Heavily On Status Refresh

### Observed Pattern

Smelt completion appears to rely on:

```text
SmeltCompleteEvent
↓
Status Refresh
↓
Inventory Truth
```

rather than:

```text
SmeltCompleteEvent
+
Inventory Delta
```

---

### Consequences

If status refresh:

* arrives late
* fails
* races

then inventory reasoning becomes uncertain.

---

### Recommendation

Emit inventory deltas directly from smelt completion.

Example:

```json
{
  "event":"smeltComplete",
  "input":"iron_ore",
  "output":"iron_ingot",
  "count":8
}
```

Then update world state immediately.

---

### Confidence

86%

---

# New Finding 7

## Runtime Is Still Event-Driven Rather Than Truth-Driven

This is the most important new architectural finding.

The system increasingly trusts:

```text
events
```

more than:

```text
verified world state
```

Examples:

### Mining

Current:

```text
mineComplete
```

Preferred:

```text
inventory increased
```

---

### Placement

Current:

```text
blockPlaced
```

Preferred:

```text
target coordinate contains expected block
```

---

### Smelting

Current:

```text
smeltComplete
```

Preferred:

```text
requested output exists
```

---

### Building

Current:

```text
build task completed
```

Preferred:

```text
blueprint matches world
```

---

### Recommendation

Introduce:

```csharp
VerifiedOutcome
```

layer.

```text
Action
↓
Event
↓
Verification
↓
Outcome
```

---

### Confidence

96%

---

# Hidden Long-Term Risk

## Planner Confidence Inflation

A pattern is emerging.

The planner receives increasingly positive signals:

```text
Action Complete
Smelt Complete
Place Complete
Mine Complete
```

but these signals are not always tied to world truth.

Over time this creates:

```text
planner confidence inflation
```

where the planner becomes increasingly convinced progress occurred even when reality partially diverged.

This is exactly the type of issue that produces:

> "The agent feels like it should work but keeps getting stuck."

because every subsystem individually appears successful.

---

# Recommendations For Next Sprint

## P0

Introduce explicit:

```csharp
ActionResult
```

hierarchy:

```csharp
Succeeded
PartiallySucceeded
Failed
Verified
```

---

## P0

Implement:

```csharp
BlueprintVerifier
```

for build completion.

---

## P0

Fix smelt quantity verification.

---

## P1

Add structured placement failure reasons.

---

## P1

Replace blocking sleeps.

---

## P1

Add inventory delta events for smelting.

---

## P2

Add recipe validation layer.

---

# Final Assessment

The codebase is improving.

The next bottleneck is no longer planning quality.

The next bottleneck is **proving that execution produced the intended world state**.

The most valuable architectural shift for Sprint 36+ is:

```text
Event Driven
        ↓
Verification Driven
```

Every major capability—mining, building, crafting, smelting, gathering—should eventually answer a single question:

> "What evidence do we have that the world now matches the plan?"

Once that question can be answered consistently, a large percentage of the remaining brittleness should disappear.