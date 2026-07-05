# MemorySmith Code Audit — Delta Report #3 (2026-07-02, continued)

**Scope of this document:** deltas only, on top of reports #1 and #2. This pass did a full line-by-line read of `MemorySmith.App/Services/SecurityServices.cs` (1,258 lines) — the authorization, authentication, and local-account-management module. Everything below is new unless marked as a correction.

---

## Headline deltas

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **The login rate limiter is global, not per-client.** `AddFixedWindowLimiter("login", ...)` shares one bucket across every request from every IP/user. Default config (5 attempts / 15 min) means **one person mistyping their password 5 times locks out the entire application — every user, including admins — for up to 15 minutes.** It's also a trivial, no-skill DoS: anyone can exhaust the whole app's login budget with 5 anonymous POSTs. | 🔴 New, major | **90%** |
| 2 | **"Progressive lockout" is a phantom feature.** `LockoutMinutes` and `MaxProgressiveLockoutMinutes` are defined, exposed in the Admin Settings UI with specific descriptive promises ("Initial lockout duration... before progressive lockout extends the delay"), and given dev-environment overrides — but **zero code anywhere in the repo reads or enforces them.** An operator configuring this believes they have per-account escalating lockout on top of the rate limiter. They don't. | 🔴 New, major | **95%** |
| 3 | **`Allows()` collapses 10 named permissions into 3 real tiers, and one config setting's description undersells what it grants.** `AutoEditorForAuthenticatedUsers`'s UI description says it "does not grant Admin privileges, settings access, audit access, or restore permissions" — true, but silent on the fact that it *does* grant `ReadSourceBundle` (raw source-file access) and `ApproveAgentWrites` (approving AI-agent-authored content changes) to every authenticated user, because `Allows()` treats "Editor" as "everything except the 5 explicitly admin-gated permissions." | 🟡 New | **88%** |
| 4 | **Correction/strengthening of Report #1's finding C-2 (ungated OAuth admin bootstrap):** the codebase already contains a **correctly-built gate for exactly this scenario** — `CreateFirstAdminAsync` (local password path) requires either a loopback request or a SHA-256-hashed, constant-time-compared bootstrap token (`ValidateBootstrapToken` + `FixedTimeEquals`) before granting the first Admin role. The OAuth callback path (`GitHubOAuthCallbackHandler.cs`) does not reuse this pattern at all — it grants Admin to whoever's first, unconditionally. The fix isn't hypothetical infrastructure work; it's "call the same gate the local-password path already calls." | ⚪ Correction (raises actionability, not confidence) | **92%** |

---

## 1. Global (non-partitioned) login rate limiter

**Evidence** — `MemorySmithSecuritySetup.cs`:
```csharp
builder.Services.AddRateLimiter(options =>
{
    var authLimits = builder.Configuration.GetSection("MemorySmith:Auth:RateLimits").Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = Math.Max(1, authLimits.LoginPermitLimit);
        limiter.Window = TimeSpan.FromMinutes(Math.Max(1, authLimits.LoginWindowMinutes));
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});
```
`AuthController.cs` applies it via `[EnableRateLimiting("login")]` on the login and setup-admin actions.

**Why this is the bug it looks like:** `RateLimiterOptions.AddFixedWindowLimiter(string policyName, Action<FixedWindowRateLimiterOptions>)` is the *unpartitioned* overload — it creates a single shared limiter instance for the named policy, with no per-client key. This is a well-documented ASP.NET Core gotcha distinct from `AddPolicy(name, httpContext => RateLimitPartition.GetFixedWindowLimiter(partitionKey: httpContext.Connection.RemoteIpAddress, ...))`, which is the partitioned form actually needed for "rate-limit each client separately." Nothing here partitions by IP, user, or any other key.

**Concrete consequence at the default config (`LoginPermitLimit=5`, `LoginWindowMinutes=15`):**
- Total login attempts allowed **application-wide, from any combination of clients, is 5 per 15 minutes** — not 5 per client.
- In any deployment with more than one active user, ordinary concurrent usage (a couple of people mistyping passwords, or just logging in around the same time) can exhaust the budget and lock everyone — including the admin trying to fix things — out of `/auth/login` for up to 15 minutes.
- An attacker (or a bored script) needs zero credentials and zero sophistication to trigger this: 5 anonymous POSTs to the login endpoint from anywhere.
- As a brute-force defense it's also *weaker* than the numbers suggest: if you were assuming "5 guesses per attacker per window" (the usual mental model for this kind of limiter), the real behavior is "5 guesses total, then everyone is locked out" — which an attacker can also use to their advantage by intentionally burning the shared budget to suppress a legitimate admin's ability to log in during an incident.

**Recommendation:** Switch to a partitioned limiter keyed by client IP (or by `UserNameOrEmail` from the request body, which is arguably the more correct key for a login-guessing defense specifically, combined with an IP-based one for the anonymous-DoS case):
```csharp
options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
    factory: _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = Math.Max(1, authLimits.LoginPermitLimit),
        Window = TimeSpan.FromMinutes(Math.Max(1, authLimits.LoginWindowMinutes)),
        QueueLimit = 0
    }));
```
This is a contained, single-file change with no schema/migration implications.

**Confidence: 90%** — the API semantics (`AddFixedWindowLimiter` = global, unpartitioned) are a well-established ASP.NET Core behavior I'm confident about, and the code itself is unambiguous; the 10-point discount is because I did not spin up the app and empirically trigger the 429 from two different simulated IPs to observe it firsthand — this is a static-analysis conclusion about documented framework behavior, not a reproduced-in-the-running-app conclusion.

---

## 2. Phantom "progressive lockout" feature

**Evidence — declared, described, and defaulted, but never consumed:**
```csharp
// MemorySmithOptions.cs
public int LockoutMinutes { get; set; } = 15;
public int MaxProgressiveLockoutMinutes { get; set; } = 60;
```
```csharp
// AdminSettingsService.cs — user-facing descriptions in the Settings UI
EditableSettingDescriptor.Integer("MemorySmith:Auth:RateLimits:LockoutMinutes", "Base lockout minutes", "Auth",
    settings => settings.Auth.RateLimits.LockoutMinutes, 1, 1440,
    "Initial lockout duration after repeated failed login attempts. This is the first step before progressive lockout extends the delay."),
EditableSettingDescriptor.Integer("MemorySmith:Auth:RateLimits:MaxProgressiveLockoutMinutes", "Max progressive lockout minutes", "Auth",
    settings => settings.Auth.RateLimits.MaxProgressiveLockoutMinutes, 1, 10080,
    "Upper bound for progressive lockouts after repeated local password failures. Keep high enough to slow attacks but low enough for recoverable local administration."),
```
Repo-wide search for any consumer of either field, or for any per-account lockout state (`IsLockedOut`, `LockedUntil`, `LockoutEnd`, or similar) anywhere in the codebase: **zero results.** The only place `SignInAsync` reacts to a failed attempt is writing a `LoginHistoryEntry` row and an audit log entry — there is no counting of consecutive failures per account and no resulting lockout state checked before the next attempt.

**Why this is worse than ordinary dead config:** this isn't leftover code from a refactor (like the `ChatServices.cs` cluster in Report #2) — it's a **security control described to the operator, in the product's own settings UI, in specific and confident language**, that doesn't exist. An operator who reads "Upper bound for progressive lockouts after repeated local password failures" and sets it to something restrictive has no reason to suspect the number does nothing. This is the kind of gap that matters most right when it's needed — during an actual credential-stuffing attempt — and won't be discovered until then unless someone reads the source or an audit like this one flags it.

**Relationship to Finding #1:** the *only* real protection today is the global, unpartitioned 5-per-15-minutes limiter above — which, per §1, protects the app less than intended and creates its own denial-of-service surface. So the net current state is: the two features an operator would reasonably believe exist (per-IP rate limiting, per-account progressive lockout) are respectively *broken* and *absent*, while what actually exists (a global all-clients-share-one-bucket limiter) is not what either setting's description implies.

**Recommendation:** Either implement per-account progressive lockout (track consecutive failures per `UserAccount`, likely a `FailedLoginCount`/`LockedUntilUtc` pair on the user record or a small side table, checked in `SignInAsync` before attempting password verification and incremented/reset around the existing verification branch) or remove the two settings from the Admin UI and options class until it's built, so the UI doesn't promise a control that isn't there. Given the existing `LoginHistory` table already records every attempt with timestamps, a lockout check could plausibly be built by querying recent failure counts from that table rather than adding new state — worth scoping as a real option during implementation.

**Confidence: 95%** — this is a pure "does anything reference this identifier" fact, checked repo-wide with no ambiguity in the result.

---

## 3. `AutoEditorForAuthenticatedUsers` grants more than its description says

**Evidence chain:**
1. Setting description (`AdminSettingsService.cs`): *"Treats authenticated users as editors for normal wiki editing flows. It does not grant Admin privileges, settings access, audit access, or restore permissions."*
2. Effect in code (`MemorySmithPermissionHandler.HandleRequirementAsync`): `if (auth.AutoEditorForAuthenticatedUsers) { roles.Add(MemorySmithRoles.Editor); }` — unconditionally, for any authenticated user, regardless of their actual assigned role in the database.
3. `Allows()`: `if (roles.Contains(MemorySmithRoles.Editor)) return true;` for **any** permission not in the explicit Admin-gated list (`Admin`, `ManageUsers`, `ManageSettings`, `ViewAudit`, `RestoreHistory`).
4. The permission enum includes `ReadSourceBundle` and `ApproveAgentWrites` — neither is in the Admin-gated list, so both fall under the Editor blanket grant.

**The gap:** the description names the 5 things it *doesn't* grant and stops there, implying (without saying) that what remains is just "normal wiki editing." In practice "what remains" also includes reading raw source-file bundles behind memory records (a `ChatToolRisk.SensitiveRead`-classified capability per `McpController.cs`) and approving AI-agent-authored writes — both meaningfully more sensitive than "can edit a wiki page," and both worth their own line in the description if the intent truly is to scope this toggle to ordinary editing.

**Is this a bug or a design choice?** I can't tell from the code alone — it's plausible this blanket-grant is intentional (a 3-tier model: Admin / Editor / Viewer, where "Editor" is deliberately defined as "everything short of account/settings/audit administration"). If that's the intent, the fix is just tightening the setting's description to be accurate. If the intent was actually finer-grained (a world where `ApproveAgentWrites` and `ReadSourceBundle` are meant to be separately grantable, which the existence of distinct enum values and distinct named policies suggests was *at least considered*), then `Allows()` needs a real per-permission role mapping instead of the current "Admin can do admin things, Editor can do everything else, Viewer can view+chat" three-tier collapse.

**Recommendation:** Flag this as an open design question rather than a fix-it bug (see §4 below) — but at minimum, update the `AutoEditorForAuthenticatedUsers` description to explicitly mention `ReadSourceBundle`/`ApproveAgentWrites` so operators aren't surprised. That part is unambiguous and cheap regardless of which direction the broader design question resolves.

**Confidence: 88%** (the mechanism is directly verified; whether this constitutes a "bug" vs. "correctly documented, just not documented very specifically" is a judgment call, hence not higher).

---

## 4. Correction to Report #1, Finding C-2 (OAuth admin bootstrap): the fix pattern already exists in this file

Report #1 flagged that `GitHubOAuthCallbackHandler.cs` grants Admin to the first successfully-authenticated OAuth user with no gate. This pass found that the **local password admin-setup path in this same `SecurityServices.cs` file already solves the identical problem correctly**:
```csharp
var isLoopback = MemorySmithRequestGuardMiddleware.IsLoopback(_httpContextAccessor.HttpContext?.Connection.RemoteIpAddress);
var tokenIsValid = ValidateBootstrapToken(request.BootstrapToken, auth.Setup.BootstrapTokenHash);
if (!isLoopback && !tokenIsValid) { return new AuthResult(false, "..."); }
if (isLoopback && !auth.Setup.AllowLoopbackBootstrap && !tokenIsValid) { return new AuthResult(false, "..."); }
```
`ValidateBootstrapToken` does a SHA-256 hash of the supplied token compared via the (already-flagged, see Report #1 §1.5) `FixedTimeEquals` helper against a pre-configured `BootstrapTokenHash`.

This doesn't change my confidence in the original finding — it changes the *recommendation's shape*. This isn't "design and build a bootstrap-gating mechanism from scratch"; it's "make `GitHubOAuthCallbackHandler`'s first-admin-promotion branch call the same `auth.Setup.BootstrapTokenHash`/loopback check `CreateFirstAdminAsync` already calls, instead of promoting unconditionally." That's a much smaller, lower-risk change than the original recommendation implied, and I'd suggest scoping it that way rather than as new infrastructure.

**Confidence: 92%** on the mechanism comparison; the fix being "just reuse this" rather than "build new" is a reasonably confident read of the existing code shape, though the actual OAuth callback flow may have call-site constraints (e.g., no natural place to prompt for a bootstrap token mid-OAuth-redirect) that a token-based approach would need to account for — a loopback-only gate might be the more OAuth-flow-compatible half of the existing pattern to reuse.

---

## 5. Coverage note

This pass completed a full line-by-line read of `SecurityServices.cs` (1,258 lines) — the first of the five ">1,200 line, not yet fully read" files flagged as outstanding in Report #2 §4. Remaining from that list: `MaintenanceAgentServices.cs`, `TaskDomainService.cs`, `MemoryApplicationService.cs`, `CodeSearchService.cs` (partially covered across reports #1/#2), and the Razor component layer — still outstanding for a future pass.
