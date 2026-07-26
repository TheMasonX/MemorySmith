# MemorySmith codebase audit

## Scope

Reviewed the commit at `62810376c17af1f7a782092d8d666bcc2148cc70` and the active surfaces most likely to carry current risk: admin/auth/settings, request guarding, source-link opening, memory state/scoring, file-backed storage, and the hosting/pipeline setup. The repository is a single-host ASP.NET Core app with Blazor, REST, MCP, SQLite-backed security/audit metadata, file-backed memory/page content, and a task/wiki documentation surface under `Data/Tasks`, `Data/Memories`, and `MemorySmith.Core/Docs/Plans`. fileciteturn2file0

I also cross-checked the repo for adjacent audit/sprint artifacts so the findings below do not duplicate obvious pre-existing planning surfaces. I found nearby codebase-health and security-hardening sprint/audit material, so I treated those as context rather than re-litigating them. fileciteturn7file1turn7file2turn7file3turn7file4

## Executive summary

The commit is directionally strong: it tightens admin setup, adds rate limiting, adds a global auth self-lockout guard, restores MemoryStateMachine demotion/re-promotion behavior, and adds audit logging for source-link opens. The main risk is not the new features themselves but the amount of policy logic now duplicated across controller, settings service, and auth service layers. That duplication increases the odds of future divergence, especially around sign-in availability and fallback behavior. fileciteturn11file0turn22file0turn31file0turn38file0

Most important technical issues found in this pass:
1. A concrete auth logging bug misclassifies disabled-account login failures as generic invalid credentials.
2. File-backed storage still hides corrupt-file failures too easily and uses a narrow filename sanitizer.
3. The new auth lockout safety checks are split across two code paths and hard-code today’s provider set, which will age poorly as the auth surface grows.
4. The memory scoring/state logic is still highly brittle and primitive-obsession-heavy, with unbounded influence from references and time-based scoring assumptions.
5. The request guard treats a missing remote IP as loopback, which is a permissive assumption that should be explicit if it is intentional. fileciteturn39file0turn17file0turn22file0turn14file0turn13file0turn29file0

Overall confidence in the high-level assessment: **82%**. Confidence is lower than ideal because this pass emphasized the currently in-progress areas and the supporting safety stack; it did not exhaustively validate every peripheral UI, benchmark, or historical doc path in the repo. The code evidence below is still strong enough to support the concrete findings. fileciteturn2file0turn26file0turn27file0

## Findings

| ID | Severity | Confidence | Finding | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| F-001 | High | 95% | `SignInAsync` computes a `"disabled"` failure code in a branch that can never run, so disabled users are logged as `"invalid_credentials"`. | This hides a meaningful auth state in logs and makes incident triage and lockout analysis less reliable. | fileciteturn39file0 |
| F-002 | High | 90% | `FileMemoryStore.LoadAll()` catches all exceptions and silently skips corrupt files unless diagnostics is configured, while `SanitizeId()` only strips a narrow set of filename hazards. | Corruption can disappear from normal operation, and the store can still accept IDs that are unsafe on some filesystems. | fileciteturn17file0 |
| F-003 | High | 88% | Auth self-lockout protection is duplicated across `AdminController` and `AdminSettingsService`, and both use a hard-coded provider list. | The guardrail is useful, but duplicated policy logic is a drift risk and will miss future provider types unless every location is updated. | fileciteturn11file0turn22file0turn25file0 |
| F-004 | Medium | 84% | `MemoryScorer` uses an unbounded `References.Count` term and raw recency math without clamping or normalization. | Score inflation becomes easy, small topology changes can dominate ranking, and future-dated/skewed timestamps can distort status transitions. | fileciteturn14file0 |
| F-005 | Medium | 80% | `MemoryRecord` still carries both legacy `References`/`Conflicts` and the newer `Relationships` collection. | This is a classic legacy bridge that invites drift and implicit contracts unless one representation is clearly canonicalized. | fileciteturn15file0turn2file0 |
| F-006 | Medium | 70% | `MemorySmithRequestGuardMiddleware.IsLoopback(null)` returns `true`, so a missing remote IP is treated as local traffic. | If any hosting path or proxy layer yields a null remote address, the guard is permissive by default. That may be intended, but it is an implicit security contract. | fileciteturn29file0 |
| F-007 | Medium | 83% | `MemoryStateMachine` now has sensible demotion/re-promotion behavior, but the thresholds are still hard-coded constants and the transition event is generic. | The logic is correct enough for now, but it is fragile to policy change and hard to analyze in telemetry because the event lacks an explicit reason code. | fileciteturn13file0 |

## Detailed findings and recommendations

### F-001 — Login failure classification bug
`MemorySmithLocalAuthService.SignInAsync()` sets `failureCode = user.IsDisabled ? "disabled" : "invalid_credentials"` inside a branch that already requires `user is not null && !user.IsDisabled ...`, so the disabled case is unreachable. The result is that disabled accounts are recorded and audited as generic invalid-credential failures instead of disabled-account failures. fileciteturn39file0

Recommended fix: compute the disabled-user branch before the `!user.IsDisabled` gate, or split the flow into explicit states (`not_found`, `disabled`, `invalid_credentials`, `success`). This is a small patch with high diagnostic value. fileciteturn39file0

### F-002 — File storage hides corruption and under-sanitizes IDs
`FileMemoryStore.LoadAll()` swallows any exception while deserializing file records and only reports the corrupt file through `_diagnostics` when that optional dependency exists. That means the default path can silently lose records from a load pass without surfacing a hard failure. In the same class, `SanitizeId()` only strips `/ \ : ? *` and `..`, which is narrower than the full invalid filename surface on common platforms. fileciteturn17file0

Recommended fix: replace the broad silent skip with a counted failure path that is visible in normal health/telemetry, and centralize filename normalization around `Path.GetInvalidFileNameChars()` plus a stable encoding/escape scheme for IDs. If a record cannot be parsed, preserve the file path, exception class, and a corrupt-file metric. fileciteturn17file0

### F-003 — Duplicate auth lockout policy should be centralized
The new “cannot disable the last sign-in method” rule appears in both `AdminController.SetProviderEnabled()` and `AdminSettingsService.TryValidateCrossSettingConstraints()`. The controller checks current provider rows plus `LocalPasswordEnabled`; the settings service checks a hard-coded set of providers (`GitHub`, `Google`, `Microsoft`) plus local password when editing JSON configuration. That is a useful guardrail, but it is also a split policy with two separate implementations and a provider list that will age out as soon as another external provider is introduced. fileciteturn11file0turn22file0turn25file0

Recommended fix: extract a single auth-safety service, then call it from both settings mutation and provider toggles. The check should enumerate configured provider types dynamically from the auth options model rather than baking in today’s providers. Also consider validating against the post-write state in a transaction-like sequence so two near-simultaneous edits cannot race the lockout guard. fileciteturn11file0turn22file0turn25file0

### F-004 — Memory scoring is still brittle and easy to game
`MemoryScorer.Score()` combines `Math.Log10(UsageCount + 1)`, raw `Confidence`, raw `References.Count`, and a recency factor derived from `DateTime.UtcNow - LastUpdated`. The references term is unbounded, so topology can dominate score; the time term can also become noisy if clocks drift or records are imported with future timestamps. That is workable for a prototype, but it is not robust enough for a greenfield system that wants to shed legacy behavior rather than accumulate it. fileciteturn14file0

Recommended fix: cap or normalize structural terms, clamp negative/positive recency outliers, and make the weighting policy explicit and configurable. If the score is supposed to drive status transitions, treat it like a real domain rule, not a convenience formula. fileciteturn14file0turn13file0

### F-005 — Legacy relationship model still has two sources of truth
`MemoryRecord` now carries `References`, `Conflicts`, and a newer `Relationships` collection, and the inline comment explicitly says the additive legacy arrays remain for backward compatibility while new code should prefer `Relationships`. That is a reasonable migration bridge, but it is also a classic place where divergence starts quietly and then leaks into search, ranking, or context-pack behavior. fileciteturn15file0

Recommended fix: define one canonical relationship abstraction and map legacy arrays into it at the edges only. If the legacy arrays must stay for compatibility, create a single translation layer and measure how often each shape is still used so the retirement plan can be data-driven. fileciteturn15file0turn2file0

### F-006 — Loopback default is permissive by assumption
`MemorySmithRequestGuardMiddleware.IsLoopback(null)` returns `true`, so a missing remote IP is treated as local. That might be fine for a local-only desktop or dev deployment, but it is an implicit security assumption that should be visible and defended in tests if the app can ever sit behind a proxy or special hosting stack that omits the remote address. The guard also blocks all remote requests when `AllowRemoteApi` is false, so this branch materially affects who gets through. fileciteturn29file0turn25file0

Recommended fix: decide whether null remote IP should be local, and encode that choice explicitly with a test. If the permissive branch stays, document it in the security settings and pipeline docs so it is not mistaken for an accidental bypass. fileciteturn29file0turn27file0

### F-007 — State machine improvement is good, but the policy is still hard-coded
`MemoryStateMachine.Evaluate()` now supports Core demotion and Deprecated re-promotion, which is a meaningful improvement over a one-way lifecycle. The remaining issue is that all thresholds are hard-coded and the emitted event is only labeled `"Transition"` with a text blob, so future policy tuning will require code edits and telemetry will be less queryable than it should be. fileciteturn13file0

Recommended fix: move thresholds into a named configuration object or domain policy, and emit structured transition metadata such as `from`, `to`, `reason`, and `score`. That keeps the lifecycle understandable as the system evolves. fileciteturn13file0

## Implementation guidance

The safest next refactor is to pull the auth safety checks out of controller/service code and into one policy service, then tighten the storage and scoring edges. That sequencing reduces the chance of inconsistent safety rules while also lowering the odds of hidden data corruption and ranking drift. The current hosting setup already has a clear composition root and modular pipeline, which makes this kind of consolidation relatively low-risk if done deliberately. fileciteturn26file0turn27file0turn28file0

A practical order:
1. Fix the login failure classification bug.
2. Centralize the auth lockout guard.
3. Replace silent file-load skipping with visible corruption reporting and broader ID normalization.
4. Normalize scoring inputs and move thresholds to a policy object.
5. Collapse the legacy relationship bridge into one canonical abstraction. fileciteturn39file0turn17file0turn22file0turn14file0turn15file0

## Assumptions and open questions

- Assumption: the commit’s task list is the current implementation target, and the repo docs around audits/plans are advisory context rather than authoritative backlog state. fileciteturn1file1turn2file0turn6file0
- Assumption: only GitHub/Google/Microsoft are currently intended external providers. If another provider is planned, the auth lockout checks need to stop hard-coding provider names. fileciteturn22file0turn25file0turn31file0
- Open question: should a missing remote IP be considered loopback, or should that be treated as an explicit test-only behavior? fileciteturn29file0
- Open question: is `Relationships` intended to fully replace `References`/`Conflicts`, or are those legacy fields still part of the supported contract for the long haul? fileciteturn15file0
- Open question: should corrupt file reads fail the load pass, or is silent skipping acceptable as long as a health signal exists? fileciteturn17file0

## Confidence notes

- F-001: 95% — direct branch-level logic bug.
- F-002: 90% — explicit broad catch plus narrow sanitizer.
- F-003: 84% — policy duplication and provider hard-coding are visible now, but future-provider impact depends on roadmap.
- F-004: 88% — scoring brittleness is mathematically evident.
- F-005: 83% — architecture drift risk is clear from the model shape and compatibility comment.
- F-006: 70% — depends on hosting/proxy behavior.
- F-007: 83% — state transitions are implemented correctly, but hard-coding is a maintainability risk. fileciteturn13file0turn14file0turn15file0turn17file0turn22file0turn29file0turn39file0
