# MemorySmith delta audit — code review style pass

## Executive summary

This pass adds two maintainability deltas that are still live after the earlier rounds: a duplicated nested-bool resolution pattern in `AdminSettingsService`, and a brittle, implementation-coupled test suite for the state-transition change. The first is a straightforward extraction candidate; the second is a test-design smell that will make future threshold tuning expensive. fileciteturn134file0turn133file0

The current commit is otherwise still dominated by the previously recorded provider, dependency-surface, and lockout hardening work. I am not repeating those here except where they intersect the new deltas.

## Findings

| ID | Severity | Confidence | Smell / issue | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-018 | Medium | 90% | **Duplicated Code / Data Clump** — `ResolveAuthBool()` and `ResolveProviderBool()` in `AdminSettingsService` are nearly identical wrappers around `TryGetJsonValue(...)` plus bool parsing. | This is a clear extraction target. If the nested auth JSON path changes, there are two places to update the same shape of code. | fileciteturn134file0 |
| D-019 | Medium | 87% | **Brittle tests / implementation coupling** — `StateTransitionTests` encode exact record shapes, exact aging values, and exact threshold-adjacent scores to force transitions. | These tests verify today’s formula rather than the business behavior. Any scoring refactor will require rewriting a lot of test setup, even if the user-visible behavior stays the same. | fileciteturn133file0turn132file0 |
| D-020 | Low | 83% | **Shotgun surgery / repeated action metadata** — `AdminController` now has three separate `POST /api/admin/setup` actions that all need the same anonymous/rate-limit policy and one fallback path. | This is intentional for content-type disambiguation, but it creates a maintenance edge: any future policy or attribute change must be mirrored in three places. | fileciteturn131file0turn135file0 |

## Detailed findings

### D-018 — Extract the nested-bool resolver
`AdminSettingsService` now has one helper for `MemorySmith:Auth:LocalPasswordEnabled` and another for `MemorySmith:Auth:Providers:{Provider}:Enabled`. Both helpers call the same `TryGetJsonValue(...)` path walk, then do the same `JsonValue.TryGetValue<bool>` / string-parsed-bool fallback. The only thing that changes is the path segments. fileciteturn134file0

**Why this is a smell:** it is duplicated logic hiding behind different path literals. It is not just cosmetic; it is an example of “same logic shape appears in more than one hunk.”  
**Fix:** replace both helpers with one generic `ResolveNestedBool(root, pathSegments, currentValue)` helper, then pass the path as a small value object or `string[]`. That removes the duplication and makes future auth-setting validations easier to extend.  
**Confidence:** 90%

### D-019 — State-transition tests are too tightly coupled to the implementation
The new state-transition tests build records with hard-coded `UsageCount`, `Confidence`, `LastUpdated`, and `References` values to hit the exact thresholds in `MemoryStateMachine`. They also use `DateTime.UtcNow` directly in the setup. That makes the tests highly sensitive to the current scoring formula and to wall-clock dependence. fileciteturn133file0turn132file0

**Why this matters:** the tests currently mirror the implementation instead of pinning behavior. That is useful for a first safety net, but it becomes brittle as soon as scoring weights or thresholds evolve.  
**Fix:** introduce a test-builder helper that produces named scenarios like “core demotion”, “deprecated repromotion”, and “deprecation disabled,” and isolate timestamp choice behind a fixed clock or a stable constant. Parameterized tests would remove some of the repetition and make the intent clearer.  
**Confidence:** 87%

### D-020 — Three setup endpoints share one policy surface
`AdminController` now uses two `[HttpPost("setup")]` actions for JSON and form data plus a third fallback action for missing content-type. All three share `AllowAnonymous` and `EnableRateLimiting("login")`. The fallback exists for a real ASP.NET Core ambiguity case, so the split is justified, but the repeated policy metadata means future changes are easy to miss in one branch. fileciteturn131file0turn135file0

**Why this matters:** it is a small shotgun-surgery hotspot. If the setup policy changes again, there are three separate methods to keep in sync.  
**Fix:** centralize shared setup metadata with a shared helper/attribute combination, or at least a private local policy wrapper so the route split remains but the shared policy does not drift.  
**Confidence:** 83%

## Task mapping and backlog fit

`TSK-0300` remains the right home for the auth self-lockout work already in progress, but this new duplication note belongs as an extension inside that task, not as a separate task. The extraction here is within the same auth-settings policy surface. fileciteturn134file0

`TSK-3087` is the right home for the state-machine behavior change, but the test brittleness should be called out explicitly as a follow-on acceptance criterion: the tests should verify durable behavior, not just the current threshold constants. fileciteturn132file0turn133file0

`TSK-3086` is still the right slot for the setup endpoint split, but it should probably note the shared policy metadata so future edits do not accidentally diverge. fileciteturn131file0turn135file0

## Implementation guidance

1. Replace the two nested-bool helpers with one generic resolver.
2. Refactor state-transition tests around named scenarios and a fixed clock.
3. Wrap the three setup endpoints’ common metadata in one helper or attribute path. fileciteturn134file0turn133file0turn131file0

## Assumptions and open questions

- Assumption: the auth settings JSON schema will continue to evolve; otherwise the nested-bool duplication is lower risk but still unnecessary. fileciteturn134file0
- Assumption: the state machine thresholds are expected to change over time as more data comes in, which makes implementation-coupled tests a real maintenance cost. fileciteturn132file0turn133file0
- Open question: should the setup endpoint split be kept as three actions, or should it be collapsed behind a single manual body parser to remove the policy duplication? The current split is cleaner for ASP.NET binding but noisier for maintenance. fileciteturn131file0turn135file0

## Confidence notes

- D-018: 90%
- D-019: 87%
- D-020: 83% fileciteturn134file0turn133file0turn132file0turn131file0turn135file0
