# MemorySmith Audit — Residual Phantom-Lockout Gap in TSK-0290 (Done, Critical)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-18
**Method:** full read of the two remaining un-examined `Hosting/*.cs` modules from the prior report's stated open scope (`MemorySmithPipelineSetup.cs`, `MemorySmithSecuritySetup.cs`). The finding below came from checking a rate-limiter partition-key expression's null-fallback behavior against the exact task that wrote it, rather than reading it in isolation.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F52 | The login rate-limiter's partition key (`MemorySmithSecuritySetup.cs:81`) falls back to the literal string `"unknown"` when `RemoteIpAddress` is null — reproducing, for exactly the population of callers with a null address, the identical "one shared bucket, one person's mistake locks out everyone in it" mechanism that **TSK-0290 (Done, Critical)** was specifically written to eliminate | 90% | High (an incomplete fix for a task explicitly filed as P0/Critical, with a real, unmitigated trigger condition already confirmed present in this deployment) | **Corrects TSK-0290** — the fix it shipped is real and closes the global case, but leaves a scoped recurrence of the exact bug it was written to remove |

---

## F52 — The rate-limiter's null-address fallback reintroduces a scoped version of TSK-0290's own bug (High, 90%)

**File:** `MemorySmith.App/Hosting/MemorySmithSecuritySetup.cs`, lines 79-88:
```csharp
options.AddPolicy("login", httpContext =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, authLimits.LoginPermitLimit),
            Window = TimeSpan.FromMinutes(Math.Max(1, authLimits.LoginWindowMinutes)),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }));
```

**The exact bug this code was written to fix, per `TSK-0290`'s own problem statement (status: Done, priority: Critical):** *"`AddFixedWindowLimiter(\"login\", ...)`... creates a single unpartitioned global bucket. Default 5 attempts/15 minutes means one person mistyping a password locks out the entire application — every user, including admins."* The prescribed fix, exactly as implemented above, was to partition by `RemoteIpAddress` so each caller gets their own budget instead of sharing one global bucket.

**Why the `?? "unknown"` fallback reintroduces a narrower copy of the same bug:** every request whose `RemoteIpAddress` is null gets partitioned into the **same** `"unknown"` bucket as every other null-address request — which is exactly the "shared bucket, one mistake exhausts everyone's budget" mechanism TSK-0290 exists to eliminate, just scoped to a subset of callers instead of the whole application. And that subset is not a rare, theoretical edge case in this specific deployment: **confirmed via repo-wide grep that `UseForwardedHeaders`/`ForwardedHeadersOptions` is never configured anywhere in this codebase, and no proxy-trust settings (`KnownProxies`/`KnownNetworks`) exist in `appsettings.json` either** — meaning any deployment of this app behind a reverse proxy (a completely ordinary way to expose a self-hosted tool like this, and the same condition already identified as realistic in this engagement's F43 finding on `IsLoopback`) will commonly see a null `RemoteIpAddress` for **every** request, not an occasional one. Under that condition, this "per-IP partitioning" fix degrades to functionally the same single-shared-bucket behavior TSK-0290 was filed to remove — one legitimate user mistyping a password behind the proxy exhausts the `"unknown"` partition's budget, and every other real user behind that same proxy gets locked out of login for the rest of the window, exactly the scenario TSK-0290's own acceptance criteria describe as unacceptable.

**Why TSK-0290's own verification step wouldn't have caught this:** its stated validation — *"5 rapid attempts from IP A return 429; 5 rapid attempts from IP B succeed concurrently"* — exercises two concrete, non-null IP addresses, which is the right test for the bug as originally described (global bucket) but never exercises the null-`RemoteIpAddress` path at all. A test environment's HTTP test client almost always presents a concrete loopback or test-double address, so this gap would not surface under the task's own acceptance test, which is presumably why it shipped as Done without anyone noticing the fallback still shares a bucket.

**Relationship to F43 (same root cause, different subsystem, worth cross-referencing rather than merging):** F43 (an earlier report in this engagement) identified that `MemorySmithRequestGuardMiddleware.IsLoopback` treats a null address as trusted/loopback — a fail-open authorization consequence. This finding shares the identical root cause (null `RemoteIpAddress` under an unconfigured reverse-proxy deployment, confirmed via the same `ForwardedHeaders` absence check) but manifests as a completely different failure mode: not a fail-open bypass, but a fail-*locked* shared-bucket lockout risk — closer to an availability/DoS concern than an authorization one. Both findings point at the same underlying gap (this project has no `ForwardedHeaders` configuration story for reverse-proxy deployments) surfacing through two independent pieces of code that both key off the same nullable property without a shared, agreed-upon "what does a null address mean here" policy.

**Recommendation:**
1. **Most foundational fix, addresses both F43 and this finding at once:** configure `ForwardedHeadersOptions` properly (with an explicit, documented `KnownProxies`/`KnownNetworks` allowlist rather than the notoriously risky `ForwardAll` shortcut) so `RemoteIpAddress` reliably reflects the real client IP in any supported reverse-proxy deployment topology, eliminating the null-address condition at its source rather than needing every downstream consumer to guess at a safe fallback.
2. **Narrower, faster fix specific to this file, if (1) is a larger undertaking than fits the current sprint:** don't fall back to a shared literal string — fall back to something that still isolates callers from each other, e.g. partition by a per-connection identifier (`httpContext.Connection.Id`, which is unique per TCP connection even when the IP is unknown) when the address is null, so a null-address caller still gets their own bucket rather than sharing one with every other null-address caller. This doesn't fix the underlying "we don't know the real client IP" problem, but it does specifically close the shared-bucket lockout mechanism that's the actual harm here.
3. Either way, **add the missing test case** TSK-0290's own acceptance criteria should have included: simulate a null `RemoteIpAddress` request exhausting its bucket, then confirm a *different* null-`RemoteIpAddress` request in the same window is unaffected (post-fix) or *is* incorrectly blocked (as a regression-proving test against the current code, to make the gap concrete before fixing it).
**Effort:** option 2 alone is a small, low-risk change (~1-2 hours including the test); option 1 is a larger, more foundational piece of hosting configuration worth scoping separately (likely half a day to a day, including deciding on and documenting the trusted-proxy allowlist for this project's supported deployment topologies) — recommend doing option 2 now as the immediate mitigation and opening option 1 as its own tracked task given it has broader implications than just this one rate limiter.
**Confidence (90%):** the code-level mechanism (shared partition key for all null-address requests) and its match to TSK-0290's own described bug pattern are both directly verified from the code and the task's own text, not inferred. The `ForwardedHeaders`-absence check (confirming the trigger condition is real, not theoretical, for this specific codebase) was independently re-verified in this pass via grep, not assumed from the earlier F43 report. The 10% held back is the same standing caveat applied to every concurrency/production-conditions finding in this engagement: I cannot run this app in this sandbox to empirically confirm a null `RemoteIpAddress` actually occurs under a specific real proxy configuration, only that nothing in the code prevents or mitigates it.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Independently re-verified (rather than assumed from the earlier F43 report) that `ForwardedHeaders` configuration is absent — this finding stands on its own evidence even if F43's original claim were ever revisited.
- Verified the *other* half of TSK-0290's scope (removing the phantom `LockoutMinutes`/`MaxProgressiveLockoutMinutes` settings) was completed cleanly — zero references to either remain in `MemorySmithOptions.cs` or `AdminSettingsService.cs`. This confirms TSK-0290's implementer did the harder, more tedious part of the task correctly; the remaining gap is narrowly the fallback-value choice in the partition-key expression, not a sign of broadly careless work on that task.
- Did not re-examine `MemorySmithPipelineSetup.cs` beyond a full read for this pass's purposes — one minor, low-confidence observation from that file (the exception handler returns a generic response in all environments, including Development, with no `UseDeveloperExceptionPage`-style local debugging aid) was considered but not elevated to a finding, since it's a developer-experience choice rather than a bug and I don't have evidence it's an unintended regression versus a deliberate consistency choice carried over faithfully from the pre-TSK-0282 inline version.
