# MemorySmith Audit — `AgentSessionService`: Concurrent-Session-Limit TOCTOU (New Finding)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-18
**Method:** full read of `AgentSession.cs` (145 lines) and `AgentSessionService.cs` (691 lines, read through `ComputeEffectiveScopeAsync`), then verified the finding below against **both** concrete `IAgentSessionStore` implementations (`InMemoryAgentSessionStore.cs`, `SqliteAgentSessionStore.cs`) rather than assuming from the interface alone — this is what confirmed the race exists at the storage layer too, not just in the orchestrating service.

**Also worth recording:** this is one of the best-documented, most carefully-reasoned files read in this entire engagement — inline comments explicitly flag known spec deviations ("F13 spec-deviation note"), phase-deferral rationale, lock-discipline invariants, and even note when a design decision was "council-reviewed." `AgentSession.cs`'s embedded-lock design and its doc comments explaining *why* the lock is embedded (to guarantee the same lock object regardless of how a caller obtained the session reference) reflect real engineering care. The finding below is a genuine gap, but it exists in spite of, not because of, sloppy work — worth stating plainly given how much of this engagement has necessarily focused on problems.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F48 | `AgentSessionService.CreateSessionAsync`'s concurrent-session-limit check (`GetActiveCountForPrincipalAsync` → compare against cap → `SaveAsync`) is a classic check-then-act race with no lock, transaction, or atomic counter spanning the two steps — confirmed unguarded in **both** the in-memory and SQLite-backed store implementations. N simultaneous session-creation requests from the same principal near the cap can all pass the check and all get created, exceeding the configured limit | 90% | Medium (resource-management/DoS-adjacent — not an authorization bypass, but a real limit that can be silently exceeded) | **New** — no existing task covers this; same root-cause *pattern* as F36 (OAuth first-admin race) but in a different subsystem and a materially lower-severity consequence |

---

## F48 — Concurrent-session cap is enforceable only against sequential requests (Medium, 90%)

**File:** `MemorySmith.App/Services/AgentSessions/AgentSessionService.cs`, `CreateSessionAsync`, lines 183-188 (the check) and line 239 (the act, with substantial work — scope computation, permission checks, the addendum gate — in between):
```csharp
var cap = GetMaxConcurrentSessions(profile, opts);
var activeCount = await _store.GetActiveCountForPrincipalAsync(principalId, ct);   // ← CHECK
if (activeCount >= cap)
    return CreateSessionResult.TooManyRequests(...);
...
// ~50 lines later, after awaited scope/permission computation:
await _store.SaveAsync(session, ct);                                               // ← ACT
```

**Verified the gap is real at the storage layer, in both implementations that exist:**
- `InMemoryAgentSessionStore.GetActiveCountForPrincipalAsync` (lines 41-46) enumerates a `ConcurrentDictionary`'s current values and counts matches — thread-safe as an individual read, but with zero coordination against a concurrent `SaveAsync` adding a new entry for the same principal between this read and that write.
- `SqliteAgentSessionStore.GetActiveCountForPrincipalAsync` (lines 203-214) runs a plain `SELECT COUNT(*) FROM AgentSessions WHERE PrincipalId = $principalId AND Status IN ('Active', 'Idle');` with no surrounding transaction, no `BEGIN IMMEDIATE`, and no unique-constraint-based enforcement at the schema level that would make an over-cap insert fail.

**No lock spans the check and the act in `AgentSessionService` either** — the only locking primitive introduced anywhere in this file or `AgentSession.cs` is the *per-session* `_lock` embedded in `AgentSession` itself (explicitly documented as covering post-creation mutation of an *existing* session's `TurnCount`/`History`/`Status` — see `AgentSession.cs`'s own doc comment, lines 21-24). That lock doesn't exist yet at the point `CreateSessionAsync` needs protection, since the session it would protect hasn't been created. There is no equivalent per-principal lock or semaphore guarding the create-time capacity check.

**Concrete scenario:** a caller at exactly `cap - 1` active sessions fires several `memorysmith_agent_invoke` (session-creation) calls in quick succession — plausible from an automated client, a retry-with-backoff implementation that retries slightly too eagerly, or simply a caller issuing a batch of parallel requests without realizing they share a session budget. Each request's `GetActiveCountForPrincipalAsync` call can observe the same pre-race count (all reading before any of them writes), all pass the `activeCount >= cap` check, and all proceed to create and save a session — landing at `cap - 1 + N` active sessions for that principal instead of the intended `cap`.

**Why this is Medium, not High, unlike the structurally similar F36:** the consequence here is resource overconsumption (extra GPU-slot-scheduler queue pressure, extra concurrent `IChatProvider` calls, extra memory/DB rows) for a caller who is *already authorized* to create sessions at all — this is a soft capacity guard being exceeded, not an authorization boundary being bypassed. It doesn't grant any principal capability they weren't otherwise entitled to; it just lets them temporarily hold more concurrent sessions than the cap intends, which matters most as a defense against runaway resource usage (accidental or intentional) rather than as a security boundary in the authorization sense. This is a meaningfully different risk profile from F36 (where the race decided *who becomes Admin*), even though the underlying code pattern — check, then act, with awaited work in between and no atomic primitive spanning the two — is the same shape of bug this engagement has now found at least twice.

**Recommendation:** the cleanest fix given this project's existing patterns: make the capacity check-and-reserve atomic at the store level, mirroring the "atomic claim" recommendation already made for F36. For the SQLite-backed store specifically, a single conditional insert (e.g., inserting the new session row only if a `COUNT(*)` subquery within the same statement/transaction confirms the cap isn't already met — SQLite supports this via a trigger-based check constraint or an `INSERT ... WHERE NOT EXISTS (SELECT ... HAVING COUNT(*) >= cap)`-style guard) closes the race at the data layer regardless of how many concurrent service-layer calls arrive. For the in-memory store, a `SemaphoreSlim` keyed per-principal (a `ConcurrentDictionary<string, SemaphoreSlim>` acquired around the check-and-save pair) would achieve the same effect more simply, given it's a single-process store anyway. Given `IAgentSessionStore` is an interface with two implementations, the fix likely needs to be applied to each independently rather than centralized in `AgentSessionService`, since the atomicity primitive available differs by backing store (SQLite transaction vs. in-process semaphore).
**Effort estimate:** 3-4 hours per store implementation (6-8 hours total for both) including a concurrency test — spin up N simultaneous `CreateSessionAsync` calls for the same principal at `cap - 1` and assert exactly `cap` sessions exist afterward, for each store backend. This mirrors the test-first emphasis already established for F36 and F40 in this engagement — the test proving the fix closes the window matters more than the code change itself for this class of bug.
**Confidence (90%):** the code-level race is unambiguous and verified against both real store implementations, not inferred from the interface alone. The 10% held back is the same caveat applied to F36 — I traced this by reading the code and reasoning about the await points and lock scope, consistent with this engagement's established rigor for concurrency findings, but could not empirically reproduce the race with actual concurrent requests given this sandbox's inability to build/run the app.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- This finding's severity calibration (Medium, resource-management rather than authorization) assumes `GetMaxConcurrentSessions`'s cap exists purely to bound resource consumption per principal, not as a security control gating access to a scarcer/more sensitive resource than "another concurrent chat session" — read the surrounding code and found nothing suggesting a higher-stakes interpretation, but flagging the assumption explicitly since it's the basis for rating this Medium rather than High.
- Did not continue reading past `ComputeEffectiveScopeAsync` (line ~560 of 691) in this pass — the remainder of `AgentSessionService.cs` (the rest of scope computation, `GetMaxConcurrentSessions`, `GetIdleTimeoutMinutes`, `IsMcpToolEnabled`, `RequirePrincipalId`) remains open scope for a subsequent pass if a complete line-by-line review of this specific file is wanted next.
