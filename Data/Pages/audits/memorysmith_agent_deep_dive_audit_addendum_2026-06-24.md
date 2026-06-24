# MemorySmith.Agent Audit Addendum
**Date:** 2026-06-24  
**Scope:** Supplemental findings from a second review pass on the `sprint-35-llm-first` branch.

## New findings

### 1) Partial build origins are accepted as “explicit”
`BuildGoal` considers an origin explicit if **any** of `OriginX`, `OriginY`, or `OriginZ` is present, not all three. `GoalFactory` passes each axis independently, so malformed or partial coordinates can be normalized into a real build origin with missing axes effectively treated as zero downstream. This is brittle because `0,0,0` is also used as the “auto-detect” sentinel.

Confidence: 95%

### 2) Build-origin policy is duplicated across layers
`BuildGoalDecomposer` implements one origin-resolution policy, while `HtnPlanner` still contains a separate build path that reads origin facts directly. That means direct planner calls and routed calls can disagree on how a build origin is chosen. The architecture would be stronger with a single `BuildOrigin` resolver used everywhere.

Confidence: 88%

### 3) Malformed origin facts can be treated as valid state
`ReadOriginFact` marks `found = true` before parsing the value, so an unparseable fact still counts as “present.” The decomposer can then log that it is using stored origin coordinates even when one or more axes have fallen back to zero. That makes broken world-state facts look legitimate.

Confidence: 86%

### 4) Best-effort logging and chat handlers still swallow failures
The Mineflayer adapter uses several `catch { /* best-effort */ }` and `catch {}` paths around logging, queue setup, and long-running handlers. That is fine for keeping the bot alive, but it also means file I/O and event-processing failures can disappear without a durable diagnostic trail.

Confidence: 82%

### 5) Runtime metadata is stale
`/api/about` still reports version `0.28.0` and “Sprint 33” even though the roadmap and sprint docs describe `v0.35.0` / Sprint 35. That is a visible correctness issue in the runtime’s own introspection endpoint.

Confidence: 99%

### 6) Auth remains fail-open when the key is omitted
`ApiKeyMiddleware` intentionally allows all `/api/*` requests when `Agent:ApiKey` is blank. That is convenient for local development, but it is a deployment footgun because an omitted secret becomes an open endpoint instead of a hard failure.

Confidence: 97%

## Architecture improvements worth prioritizing next

1. Introduce a `BuildOrigin` value object with `source + coordinates + validation`.
2. Make API auth explicit-by-environment, not implicit-by-missing-config.
3. Convert silent fallback paths into structured diagnostics so failures can be observed without breaking runtime recovery.
4. Centralize runtime version/phase metadata so `/api/about`, README, and roadmap cannot drift apart.
