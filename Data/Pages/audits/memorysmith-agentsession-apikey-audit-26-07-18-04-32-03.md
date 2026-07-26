# MemorySmith Audit — API-Key-Only Callers Cannot Use Agent Sessions (New Finding)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-18
**Method:** finished the full read of `AgentSessionService.cs` (the remaining ~130 lines from the prior report's stated open scope), then traced `RequirePrincipalId`'s claim-fallback logic against every real place a `ClaimsPrincipal` gets constructed in this codebase — which is what turned a one-line helper into a cross-cutting functional gap finding.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F49 | A caller authenticated purely via the shared `ApiKey` header (this project's own documented mechanism for automation/headless MCP clients) can never successfully call `memorysmith_agent_invoke` or `memorysmith_agent_session_end` — API-key authentication only ever feeds an *authorization*-time shortcut (`HasConfiguredApiKeyAccess` → `context.Succeed`), it never constructs a `ClaimsPrincipal` with any claims, so `RequirePrincipalId` always returns `null` for such callers and every session-creation/resume/end call is rejected | 85% | Medium-High (a fully-authorized, intended access method is silently incompatible with one specific MCP tool family — not a security hole, but a real broken-by-design gap for whatever automation this feature was built to serve) | **New** — TSK-0040 (security regression matrix, Backlog) is adjacent but scoped to test coverage/organization, not this specific functional incompatibility |

**Also completed this pass (closing prior open scope):** read the remainder of `AgentSessionService.cs` (`ComputeEffectiveScopeAsync`'s risk/scope/security-profile filtering, `IsMcpToolEnabled`, `GetMaxConcurrentSessions`, `GetIdleTimeoutMinutes`) — all correct and consistent with the file's established quality bar; no further findings in that remaining section beyond F49, which originates in the small `RequirePrincipalId` helper at the very end of the file.

---

## F49 — API-key callers have no `ClaimsPrincipal` claims, so `RequirePrincipalId` always fails for them (Medium-High, 85%)

**File:** `MemorySmith.App/Services/AgentSessions/AgentSessionService.cs`, `RequirePrincipalId`, lines 660-662:
```csharp
private static string? RequirePrincipalId(ClaimsPrincipal caller) =>
    caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
    ?? caller.Identity?.Name;
```

**Traced every real place a `ClaimsPrincipal` gets its claims populated in this codebase, to determine when the `?? caller.Identity?.Name` fallback is actually reachable and whether `NameIdentifier` is reliably present:**
- **Cookie/OAuth-authenticated users** (`SecurityServices.cs:824`, `GitHubOAuthCallbackHandler.cs:176`): both paths explicitly add `ClaimTypes.NameIdentifier` set to the stable internal user ID, *and separately* add `ClaimTypes.Name`/`ClaimTypes.Email` as distinct claims. For any normally-authenticated human user, `NameIdentifier` is always present — the `?? Identity?.Name` fallback is dead code for this population of callers.
- **API-key-authenticated callers** (`SecurityServices.HasConfiguredApiKeyAccess`, lines 325-335, called from within an `IAuthorizationHandler`-style method at line 270): this only ever calls `context.Succeed(requirement)` — an **authorization** outcome. Confirmed via repo-wide grep that **nothing anywhere in the codebase ever assigns `HttpContext.User`** for an API-key-matched request — there is no corresponding `IAuthenticationHandler`/`AddScheme` registration for the API key, only this authorization-time shortcut. This means a request that authenticates purely via a correct `X-Api-Key`-style header (confirmed via `MemorySmithRequestGuardMiddleware.ApiKeyHeaderName`/`RequiresApiKey`) still carries the **default anonymous `ClaimsPrincipal`** — no `NameIdentifier`, and in the standard ASP.NET Core anonymous-request case, no `Identity.Name` either.

**Traced the consumption path to confirm this is reachable, not hypothetical:** `McpController.cs` line 144 passes the controller's own `User` (i.e., `HttpContext.User`) directly into `HandleAgentInvokeAsync(argumentsElement, User, cancellationToken)`, which flows into `AgentSessionService.CreateSessionAsync(..., caller: User, ...)`. For an MCP client authenticated only via the shared API key — exactly the scenario `HasConfiguredApiKeyAccess` exists to support, and a completely ordinary way to reach this endpoint given this project's MCP-server design — `RequirePrincipalId(User)` evaluates both `NameIdentifier` and `Identity.Name` as null, and `CreateSessionAsync` immediately returns:
```
"Agent sessions require an authenticated caller with a NameIdentifier claim."
```
**Every other MCP tool this caller has permission for continues to work** (since `HasConfiguredApiKeyAccess` grants the underlying authorization requirement regardless of identity) — it's specifically and only the two session-based tools (`memorysmith_agent_invoke`, `memorysmith_agent_session_end`) that silently, permanently fail for this entire class of caller, with an error message that doesn't hint at the real cause (an API-key user reading "requires an authenticated caller" would reasonably assume their API key *is* their authentication, since it works for everything else).

**Why this matters:** API-key access is this project's own documented mechanism for exactly the kind of caller — automation, headless clients, CI, non-interactive MCP consumers — most likely to want a stateful multi-turn sub-agent session in the first place (a human clicking around a chat UI has a cookie session and hits none of this; a script driving MCP tool calls is the more natural user of session-based sub-agent delegation). This isn't a security vulnerability — if anything it's the opposite, an overly-restrictive gap — but it's a real, silent functional dead end for what's likely this feature's most natural intended consumer, discoverable only by reading this specific interaction between two independently-reasonable pieces of code (an authorization shortcut that doesn't build an identity, and a feature that requires one).

**Recommendation, in order of how much it changes the security model (least to most):**
1. **Cheapest, no design change:** improve the error message when `RequirePrincipalId` returns null under a request that otherwise passed authorization (i.e., detect "this request was let through via API key but has no identity" specifically) so the caller gets an actionable message ("Agent sessions require per-principal identity; the shared API key alone cannot be used to scope a session — use a per-user credential.") instead of a generic claim-missing error that looks like a bug on their end.
2. **Moderate:** if API-key callers are meant to be able to use agent sessions, assign a stable synthetic principal ID for API-key-authenticated requests (e.g., derive a deterministic ID from a hash of the API key itself, or introduce a lightweight `IAuthenticationHandler` for the API-key scheme that sets `NameIdentifier` to a fixed value like `"api-key"` or a per-configured-key identifier if multiple keys are ever supported). This would let API-key callers use sessions, sharing one principal bucket (and thus one concurrent-session cap, and one set of active-session records) across all API-key traffic — a reasonable model if there's realistically only one shared key per deployment, which appears to be this project's current design (`_options.CurrentValue.ApiKey` is a single string, not a keyed collection).
3. **Most involved:** treat this as a signal that API-key auth should get its own lightweight authentication handler (rather than the current authorization-only shortcut) regardless of this specific bug — that would fix this gap as a side effect while also making API-key-authenticated requests show up correctly in any future audit-log/identity-based feature that (like this one) assumes `HttpContext.User` is meaningfully populated whenever a request is authorized. Worth considering given this is now the second feature (after the fixed lockout logic in F11, though that was a different mechanism) that assumed a more fully-populated identity model than API-key auth actually provides.
**Recommend option 1 as an immediate cheap fix, and option 2 or 3 as a design decision for whoever owns the agent-session feature's intended audience** — I don't have visibility into whether API-key clients using agent sessions was ever an actual design goal versus this being correctly scoped to cookie/OAuth users only, so this is presented as a design question with options, not a single mandated fix.
**Effort:** 1 hour for option 1; half a day for option 2; a day or more for option 3 given it touches the broader auth pipeline.
**Confidence (85%):** the mechanical claim — API-key auth never populates `HttpContext.User`, confirmed via exhaustive grep for any `HttpContext.User =`/`ClaimsPrincipal` construction tied to the API-key check — is solid. The 15% held back is for not having empirically run a live API-key-authenticated MCP request against this app (not achievable in this sandbox) to observe the exact rejection message and confirm no other code path I didn't find compensates for this (e.g., a middleware ordering subtlety that attaches claims I didn't locate via static search).

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Assumes `_options.CurrentValue.ApiKey` is genuinely a single shared secret (not a per-user keyed credential store) based on its `string` type in `MemorySmithOptions` — if a future change makes API keys per-user, the "shared bucket" concern in recommendation option 2 would need re-evaluation, though the core bug (no identity is ever attached) would remain the same regardless.
- This finding is scored Medium-High rather than a hard "must fix immediately" because it's a functionality gap, not a security or data-integrity issue — nothing is corrupted or exposed, a specific capability is simply unusable for a specific caller type. Whoever owns this feature's roadmap should confirm whether API-key-driven agent sessions were ever an intended use case before treating this as urgent.
