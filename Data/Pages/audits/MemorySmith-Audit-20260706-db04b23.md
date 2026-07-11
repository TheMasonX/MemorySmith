# MemorySmith Deep-Dive Code Audit — 2026-07-06

**Repo:** `TheMasonX/MemorySmith` · **Commit audited:** `db04b23a25e3930b424f3ef9eb0a0af3efcb9c27` (2026-07-05, "mark TSK-0288–0292 Done")
**Prior context reviewed:** 17 audit files + `council/audit-synthesis-council-20260705.md` (28 findings, tasks TSK-0288–0301) + full `f448503` implementation diff.
**Method:** GitHub raw/API pulls via `bash_tool` (no local checkout). All 15 MVC controllers, all auth/security files, the schema-migration engine, the tag-policy loader, and `MemoryChatAgent`'s config-consumption paths were read in full. The remaining ~150 App-project `.cs` files (incl. `ChatServices.cs`, `ChatToolCatalog.cs`, `CodeSearchService.cs`, `MaintenanceAgentServices.cs`, `AdminSettingsService.cs`, `TaskDomainService.cs`, `AgentSessionService.cs`, `PageService.cs`, `SemanticEmbeddingSearchService.cs`) were pattern-swept (catch-block, `IOptions`/`IOptionsMonitor`, `async void` greps across all 95 files) but not exhaustively line-read this session — see **Coverage & Scope** at the end.

---

## Executive Summary

The July 5 council synthesis correctly diagnosed the last audit cycle's top 5 findings and tasks TSK-0288–0292 were implemented and merged (`f448503`). **Two of the five "Done" Critical fixes have verified implementation defects that reopen the original vulnerability class**, and one has an unrelated but adjacent gap that surfaces under a common deployment shape. These are new findings — not restatements of TSK-0288–0301 or the council doc's S-/N-/DQ- backlog, which remains accurate and is not duplicated here except where a status changed.

| # | Finding | Status | Confidence | Severity |
|---|---|---|---|---|
| F1 | **TSK-0291 antiforgery fix is a no-op**: all 15 MVC controllers carry `[IgnoreAntiforgeryToken]`, including the two originally-cited CSRF-vulnerable ones | New regression | 92% | High |
| F2 | **TSK-0289 OAuth bootstrap gate fails open** when `RemoteIpAddress` is null (`IsLoopback(null) == true`, `AllowLoopbackBootstrap` defaults `true`) | New gap, adjacent to closed task | 78% | Medium-High |
| F3 | **TSK-0290 IP-partitioned rate limiter collapses to a single global bucket behind any reverse proxy** (no `UseForwardedHeaders` anywhere in the codebase) | New gap, adjacent to closed task | 65% (deployment-dependent) | Medium |
| F4 | **`MemoryChatAgent` reads safety-relevant toggles (`AgentWritesEnabled`, `ToolCallsEnabled`, `Auth`, `Pages.DefaultMinimumRole`) from a frozen `IOptions<MemorySmithOptions>` snapshot** while sibling chat providers in the same file correctly use `IOptionsMonitor`; Admin Settings changes to these controls silently do not take effect until restart | Confirmed instance of previously-known systemic pattern | 90% | High |
| F5 | **Tag-policy default loader still has an empty `catch {}` and an undocumented 8-directory ancestor walk** (`MemoryGovernanceServices.cs: TryLoadFileBackedDefault`) | Confirmed still-open (= council N-10, no task exists yet) | 95% | Low-Medium |
| F6 | **Schema migration runner has no per-migration transaction**; DDL, seed, and the `SchemaMigrations` INSERT are three separate un-transacted statements | New, forward-looking | 85% | Medium (High once ALTER TABLE migrations land, which is the framework's stated purpose) |

**Bottom line:** the pattern the council itself named — "fix exists on path A, forgot to apply to path B" — reproduced *inside the fixes themselves*. TSK-0291 and TSK-0289 were marked Done and tested (517 passing tests), but zero new tests exercise the negative case each task's own acceptance criteria specified (no antiforgery-rejection test, no non-loopback-no-token OAuth test). That's the actual root cause: acceptance criteria written, never encoded as tests, so "tests pass" gave false confidence.

---

## F1 — Global antiforgery filter is fully bypassed (all controllers exempted)

**Confidence: 92%** | **Severity: High** (reopens the exact CSRF gap TSK-0291 closed)

TSK-0291's scope explicitly named `AdminController.AssignRole/RemoveRole` and `SourceLinksController.Open` as CSRF-vulnerable and specified adding `[IgnoreAntiforgeryToken]` only to "MCP API endpoints (already have API key auth)."

Verified by direct read of all 15 controller files at `db04b23`:

```
$ grep -l IgnoreAntiforgeryToken Controllers/*.cs | wc -l
15
$ ls Controllers/*.cs | wc -l
15
```

Every single controller — including `AdminController.cs` and `SourceLinksController.cs`, the two the task called out by name — carries `[IgnoreAntiforgeryToken]`:

```csharp
// AdminController.cs
[ApiController]
[Route("api/admin")]
[IgnoreAntiforgeryToken]
public class AdminController : ControllerBase

// SourceLinksController.cs
[ApiController]
[Route("api/source-links")]
[Authorize(Policy = MemorySmithPolicies.CanReadSourceBundle)]
[IgnoreAntiforgeryToken]
public class SourceLinksController : ControllerBase
```

The custom `AutoValidateAntiforgeryTokenFilter` (new in `f448503`) is correctly registered as a global MVC filter and correctly implements the ignore-attribute check — the *filter* is not buggy. The bug is in the commit's own scope: it applied the blanket exemption to every controller instead of only the MCP/API-key-authenticated ones, functionally reverting the fix to its pre-TSK-0291 state while leaving the machinery in place to look fixed.

**Mitigating factor (unchanged from original finding):** `Cookie.SameSite = SameSiteMode.Lax` (`MemorySmithSecuritySetup.cs:29`) blocks most cross-site POST-with-cookie CSRF in modern browsers. This is real defense-in-depth and is why severity is High, not Critical — but it was not evaluated as a substitute for antiforgery tokens in the original task or council doc, and relying on it exclusively was not an explicit design decision.

**No test coverage added.** `MemorySmith.Tests/SecurityAndSourceLinkTests.cs` (755 lines, the file most likely to hold this) has zero references to `Antiforgery`, `Csrf`, or `IgnoreAntiforgeryToken`. TSK-0291's own acceptance criterion — "POST to `AdminController.AssignRole` without antiforgery token → 400" — was never encoded as a test, which is exactly why the blanket exemption shipped unnoticed.

### Recommended fix
1. Remove `[IgnoreAntiforgeryToken]` from all controllers **except** ones with independent auth that doesn't rely on cookies: `McpController`, `OAuthBridgeController` (pre-auth callback), and any controller solely reachable via API-key (`X-Api-Key`) auth — audit each controller's actual auth mechanism rather than blanket-exempting.
2. For controllers serving both a Blazor UI (cookie auth) and a machine/API-key path, either split the endpoints or check `Request.Headers.ContainsKey("X-Api-Key")` inside the filter to decide whether to require the antiforgery token.
3. Add the literal test TSK-0291 specified: POST `api/admin/assign-role` without an antiforgery token → expect 400. Add the equivalent for `SourceLinksController.Open`. This is the regression test that would have caught F1 before merge.
4. Add a CI/test-suite invariant: assert that the number of controllers with `[IgnoreAntiforgeryToken]` is bounded and named explicitly (e.g. a `[Fact]` that hardcodes the expected exempt-controller list and fails if it grows), so a future blanket-add is caught immediately rather than requiring a manual repo audit like this one.

---

## F2 — OAuth bootstrap gate fails open when `RemoteIpAddress` is null

**Confidence: 78%** (mechanism is 100% confirmed in code; likelihood of real-world trigger depends on deployment topology, hence the discount) | **Severity: Medium-High**

TSK-0289 is genuinely implemented — `BootstrapGate.Authorize()` exists, is called from both `SecurityServices.CreateFirstAdminAsync` (`SecurityServices.cs:458`) and `GitHubOAuthCallbackHandler.OnCreatingTicketAsync` (`GitHubOAuthCallbackHandler.cs:138`). This is correct. But the shared gate itself has a fail-open branch that predates TSK-0289 and was carried into the new call site unexamined:

```csharp
// BootstrapGate.cs
public static (bool IsAuthorized, string? ErrorMessage) Authorize(
    HttpContext? httpContext, AuthSetupOptions setup, string? suppliedToken = null)
{
    var isLoopback = MemorySmithRequestGuardMiddleware.IsLoopback(
        httpContext?.Connection.RemoteIpAddress);
    ...
}

// MemorySmithRequestGuardMiddleware.cs
public static bool IsLoopback(IPAddress? address)
{
    if (address is null)
    {
        return true;   // <-- fail-open
    }
    ...
}
```

```csharp
// MemorySmithOptions.cs
public class AuthSetupOptions
{
    public bool AllowLoopbackBootstrap { get; set; } = true;   // <-- default true
    ...
}
```

Chain: `RemoteIpAddress == null` → treated as loopback → `AllowLoopbackBootstrap` defaults `true` → bootstrap authorized with **no token, from any client whose connection doesn't populate `RemoteIpAddress`**.

`Connection.RemoteIpAddress` is null in several non-exotic hosting configurations: Kestrel behind a Unix domain socket, certain named-pipe/IIS in-process hosting setups, and — most relevant here — some reverse-proxy configurations where the proxy's forwarded headers aren't wired up and the underlying transport doesn't populate the property the way a direct TCP/IP connection would. It is also the exact value the existing `GitHubOAuthCallbackHandlerTests.cs` test harness uses (`DefaultHttpContext` with no `RemoteIpAddress` set), meaning **the one test written for this code path exercises the fail-open branch without knowing it**: `FirstSignIn_CreatesAccount_AssignsFirstAdmin_AndRecordsDurableSuccess` passes today for the "correct" reason (should be loopback-equivalent in a test) but would also pass under the exploitable interpretation, so the test cannot distinguish the two.

### Recommended fix
1. Change `IsLoopback(null)` to return `false` (fail-closed). A null address should never be treated as more trustworthy than an unrecognized one.
2. Add a test: `BootstrapGate.Authorize` with `httpContext: null` or a context with `RemoteIpAddress: null` → expect `IsAuthorized == false` unless a valid token is supplied.
3. Cross-reference with the open question already logged in the council doc ("Does the operator's actual deployment use Kestrel-direct or always a reverse proxy?") — this finding sharpens that question: if `RemoteIpAddress` can ever be null in the actual deployment shape, this is exploitable today, not hypothetically.

---

## F3 — IP-partitioned rate limiter degrades to a global bucket behind a reverse proxy

**Confidence: 65%** (mechanism confirmed; real-world impact fully depends on deployment topology, which I cannot verify from source) | **Severity: Medium**

```csharp
// MemorySmithSecuritySetup.cs (post-TSK-0290)
options.AddPolicy("login", httpContext =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        ...));
```

This correctly fixes the original bug *for direct Kestrel connections*. But there is no `UseForwardedHeaders()` middleware anywhere in the codebase (`grep -rn "ForwardedHeaders" *.cs` across all 95 App files returns zero hits) and no `X-Forwarded-For` handling. If MemorySmith is ever deployed behind nginx, IIS-as-reverse-proxy, Docker port-forwarding through a shared host, or any TLS-terminating proxy, `RemoteIpAddress` for every client becomes the proxy's loopback/internal address. All clients collapse into partition key `"127.0.0.1"` (or similar) — the exact "one mistyped password locks out the whole app" bug TSK-0290 was created to fix, silently reintroduced.

### Recommended fix
- If the deployment is Kestrel-direct only (no reverse proxy), this is a non-issue — confirm and document that assumption explicitly in `MemorySmithSecuritySetup.cs` as a code comment, since the current code has no signal either way.
- If a reverse proxy is or might be used, add `app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor })` early in the pipeline (before rate limiting/auth), configured with `KnownProxies`/`KnownNetworks` restricted to the actual proxy, and partition on the resulting `RemoteIpAddress` post-middleware.
- Add an integration test that fakes an `X-Forwarded-For` header from two different simulated clients through the same proxy IP and asserts independent rate-limit buckets, to lock in whichever answer is chosen.

---

## F4 — `MemoryChatAgent` reads safety-relevant settings from a frozen `IOptions<T>` snapshot

**Confidence: 90%** | **Severity: High** — this is the most concrete, most safety-relevant instance of the systemic `IOptions<T>` vs `IOptionsMonitor<T>` pattern already known from the July 4 audit cycle. It had not previously been pinned to a specific class + specific settings.

`ChatServices.cs` contains three provider/agent classes. Two correctly use the live pattern; the orchestrator does not:

```csharp
// ChatServices.cs:452, 455 — OllamaChatProvider
private readonly IOptionsMonitor<MemorySmithOptions> _options;   // live

// ChatServices.cs:970, 973 — GitHubCopilotChatProvider
private readonly IOptionsMonitor<MemorySmithOptions> _options;   // live

// ChatServices.cs:1705, 1735 — MemoryChatAgent (the orchestrator)
private readonly IOptions<MemorySmithOptions> _options;          // frozen snapshot
```

`IOptions<T>.Value` is bound once by the options infrastructure and never changes for the lifetime of the process, regardless of the consuming class's own DI lifetime (`MemoryChatAgent` is registered `Scoped` in `MemorySmithChatSetup.cs:20`, which does not help — a fresh scope still resolves the same frozen `IOptions<T>` singleton). `IOptionsMonitor<T>.CurrentValue` reflects the latest bound configuration on every access.

Settings `MemoryChatAgent` reads through this frozen snapshot (non-exhaustive, from direct grep of `_options.Value.*` call sites):

| Setting | Effect if stale |
|---|---|
| `Chat.AgentWritesEnabled` | An admin disabling autonomous agent writes via the Settings UI as a safety kill-switch has **no effect** until the process restarts |
| `Chat.ToolCallsEnabled` / `MaxToolIterations` / `MaxToolResultCharacters` | Tool-loop safety bounds don't tighten (or loosen) live |
| `Auth` (the whole sub-tree) | Any auth-adjacent decision `MemoryChatAgent` makes off this object reflects boot-time config |
| `Pages.DefaultMinimumRole` | Access-control default doesn't update live |
| `Training.ChatTranscriptEnabled` / `StoreChatContent` | Toggling transcript capture off doesn't take effect live |

The most concerning of these is `AgentWritesEnabled`: it is presented in the Admin Settings UI as an immediately-effective control, and for every other consumer of live options in this codebase it would be. For this one it silently is not, until restart.

### Recommended fix
1. Change `MemoryChatAgent`'s constructor to accept `IOptionsMonitor<MemorySmithOptions>` and read `.CurrentValue` at each use site instead of caching `IOptions<T>.Value` in a field, matching the pattern already used correctly by its two sibling classes in the same file.
2. This is a good candidate to batch with the TSK-0288-adjacent cleanup work, and a good target for the "one file to rule them all" recommendation the council doc already made about `ChatServices.cs`.
3. Broader recommendation (ties into council DQ/DQ-adjacent items): grep the full 15-file list already surfaced (`AgentSessionService.cs`, `CodeSearchService.cs`, `MaintenanceAgentServices.cs`, `MemoryApplicationService.cs`, `MemoryGovernanceServices.cs`, `MemoryMaintenanceService.cs`, `MemoryMaintenanceTasks.cs`, `MemorySmithChatSetup.cs`, `MemorySmithRequestGuardMiddleware.cs`, `MemorySmithStorageSetup.cs`, `OperationalDiagnosticsService.cs`, `SemanticEmbeddingPrewarmService.cs`, `SemanticEmbeddingSearchService.cs`, `VarResolver.cs`, plus `MemoryChatAgent` above) for whether each `IOptions<T>` injection is intentional (constructor-only read is fine for things like connection strings/paths that shouldn't hot-swap) or an oversight (behavior toggles that should hot-swap). This audit did not have time to classify all 15+1; that classification is the actual remaining work.

---

## F5 — Tag-policy loader: confirmed still-open, still untracked

**Confidence: 95%** | **Severity: Low-Medium** — this matches a finding already known from the July 4 audit cycle (council item **N-10**) and referenced in prior session memory. Re-verified directly against current code; still present, still has no TSK number.

```csharp
// MemoryGovernanceServices.cs — TagPolicy.TryLoadFileBackedDefault()
private static TagPolicy? TryLoadFileBackedDefault()
{
    foreach (var path in EnumerateDefaultPolicyPaths())
    {
        if (!File.Exists(path)) continue;
        try
        {
            var policy = JsonSerializer.Deserialize<TagPolicy>(File.ReadAllText(path), ...);
            if (policy is not null) { ...; return policy; }
        }
        catch { }   // <-- fully silent, no logging, no status signal
    }
    return null;
}

private static IEnumerable<string> EnumerateDefaultPolicyPaths()
{
    if (env var set) yield return env var;
    yield return Path.Combine(AppContext.BaseDirectory, "Data", "Policies", "tag-policy.json");
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    for (var level = 0; level < 8 && current is not null; level++)   // <-- 8-level ancestor walk
    {
        yield return Path.Combine(current.FullName, "Data", "Policies", "tag-policy.json");
        current = current.Parent;
    }
}
```

Two distinct issues, both already correctly diagnosed by the prior audit round:
1. The `catch { }` around the JSON deserialize is genuinely empty — a malformed policy file at any of the up-to-10 candidate paths is silently skipped with zero diagnostic trail, and the loader moves on to the next ancestor directory as if the file didn't exist.
2. The 8-level ancestor walk from `AppContext.BaseDirectory` means a misconfigured or missing `Data/Policies/tag-policy.json` in the expected location can silently pick up an unrelated file up to 8 directories up the filesystem tree (e.g., a build artifact directory, a parent monorepo checkout, or in the worst case a stale file left over from a previous deployment layout) with no indication in diagnostics of which file actually won.

Interestingly, the sibling method `LoadPolicy(string path)` in the same file (used for the *configured*, non-default path) does this correctly — it returns a `TagPolicyLoadResult` with a populated `TagPolicyLoadStatus` distinguishing loaded/missing/failed, and it does not swallow the exception. The default-loader path (`TryLoadFileBackedDefault`) is the one place this discipline wasn't applied.

### Recommended fix (matches council N-10, restated for actionability since no task exists yet)
1. Replace `catch { }` with `catch (Exception ex) { /* log at Warning */ }`.
2. Have `TryLoadFileBackedDefault` return a status tuple (or reuse `TagPolicyLoadStatus`) so `CreateDefault()`'s caller/diagnostics endpoint can report which of the ≤10 candidate paths was actually used, rather than only "loaded" vs "using built-in defaults" with no path.
3. Consider whether the 8-level walk is still needed given `MEMORYSMITH_DEFAULT_TAG_POLICY_PATH` already exists as an explicit override — if the walk exists only to support development-time repo layouts, scope it behind a `DOTNET_ENVIRONMENT == Development` check so it can't silently activate in a production deployment with an unexpected directory structure.
4. **Recommend filing this as a new task** (it has no TSK number despite being independently re-confirmed twice now, once in the July 4 cycle and once here) — low effort (~20 min), non-duplicative of anything in 288–301.

---

## F6 — Schema migration runner lacks per-migration transactional atomicity

**Confidence: 85%** | **Severity: Medium today, High once the first `ALTER TABLE` migration ships** (which is the explicit reason TSK-0292 exists — TSK-0201/0202 are blocked on this framework specifically to add columns/tables)

```csharp
// SqliteMemorySmithDatabase.cs — ApplyPendingMigrationsAsync
foreach (var migration in MigrationsLazy.Value)
{
    if (applied.Contains(migration.Id)) continue;

    await ExecuteNonQueryAsync(connection, migration.SchemaSql, cancellationToken);      // step 1
    if (!string.IsNullOrWhiteSpace(migration.SeedSql))
        await ExecuteNonQueryAsync(connection, migration.SeedSql, cancellationToken);    // step 2
    // ... separate command ...
    await recordCmd.ExecuteNonQueryAsync(cancellationToken);                             // step 3
}
```

Steps 1–3 are three independent round-trips with no `BeginTransactionAsync`/`CommitAsync` wrapping them (the codebase does use transactions elsewhere — see `SqliteMemorySmithDatabase.cs:528` — so the pattern is known and simply wasn't applied here). If the process is killed (crash, `kill -9`, host eviction, power loss) between step 1 and step 3, the migration's schema change is applied but not recorded as applied. On the next startup, the loop reaches the same migration again and re-runs `migration.SchemaSql`.

The current single migration (`20260517_auth_rbac_audit_history_v1`) uses only `CREATE TABLE IF NOT EXISTS`, which is idempotent — re-running it is harmless, which is why this hasn't caused a visible failure yet. But TSK-0292's own task description states the framework exists specifically to unblock **TSK-0201/TSK-0202, which need new tables/columns** — and SQLite's `ALTER TABLE ... ADD COLUMN` has no `IF NOT EXISTS` equivalent. The first migration that adds a column via `ALTER TABLE` and then crashes before recording itself will fail permanently on every subsequent startup with a "duplicate column name" error, with no automatic recovery path — exactly the failure mode a migration framework exists to prevent.

### Recommended fix
1. Wrap each migration's schema + seed + record-insert in a single `SqliteTransaction`, committed only after all three succeed; roll back and surface a clear startup-fatal error on any failure mid-migration (fail loud, not fail silent-and-retry-broken).
2. Add a test that simulates a crash mid-migration (e.g., inject a second migration whose `SeedSql` throws) and asserts the schema change from `SchemaSql` was rolled back, not partially applied.
3. If any future migration must use non-transactional DDL (SQLite has some DDL statements that implicitly commit), document that migration's SQL as idempotent explicitly (`ALTER TABLE ... ADD COLUMN IF NOT EXISTS` isn't valid SQLite syntax, but `PRAGMA table_info` can be checked first to make the whole migration function idempotent even without a wrapping transaction).

---

## Verification Summary — TSK-0288–0292 ("Done" Critical tasks)

| Task | Claimed | Verified against code | Verdict |
|---|---|---|---|
| TSK-0288 (secrets/gitignore) | `.vscode/mcp.json` gitignored, tracked copies removed, pre-commit hook extended | Not independently re-verified this session (out of scope of `db04b23`'s file diff; would require checking `.gitignore` + `.githooks/pre-commit` content directly) | **Not re-verified** — recommend a follow-up: confirm `git log --all -- artifacts/` history purge was actually run (task comment explicitly flags this as a separate manual step not yet done) |
| TSK-0289 (OAuth bootstrap gate) | Shared `BootstrapGate.Authorize()`, applied to both paths | **Correctly wired** in both `SecurityServices.CreateFirstAdminAsync` and `GitHubOAuthCallbackHandler.OnCreatingTicketAsync` | **Genuinely done**, but see **F2** (adjacent fail-open gap in a helper it calls) |
| TSK-0290 (rate limiter partitioning) | IP-partitioned `AddPolicy`, phantom lockout settings removed | **Correctly implemented** for direct connections; phantom settings cleanly removed from `MemorySmithOptions`, `AdminSettingsService`, `appsettings.json`, `MemorySmithLocalDevelopmentPostConfigure` (verified full removal, no orphaned references found) | **Genuinely done**, but see **F3** (proxy-dependent gap) |
| TSK-0291 (global antiforgery filter) | Global filter + selective `[IgnoreAntiforgeryToken]` on MCP/API endpoints only | Filter correctly built and registered; **exemption applied to all 15/15 controllers, including the two originally cited as vulnerable** | **Regression — see F1** |
| TSK-0292 (schema migration framework) | Ordered migration runner replacing single hardcoded migration | Correctly ordered, correctly avoids static-init issues via `Lazy<T>`, correctly backward-compatible with the existing `SchemaMigrations` table | **Correctly implemented** for the stated scope, but see **F6** (atomicity gap not covered by original scope) |

None of the four security-critical tasks (289–292) added a negative-path or crash-path test despite each one's own description explicitly asking for one ("Add test: first OAuth login from non-loopback with no token…", "Verify: POST … without antiforgery token → 400", "Add test: new migration added and auto-applied successfully"). `517 passed, 0 failed, 9 skipped` is accurate but describes the *pre-existing* suite still passing, not new coverage of the acceptance criteria the tasks themselves defined. This is the single highest-leverage process fix available: **treat each task's stated acceptance criteria as the literal test to add, not as a manual verification note** — F1 in particular would not have shipped if "POST to AdminController.AssignRole without antiforgery token → 400" had been a `[Test]` instead of a task-description sentence.

---

## Confirmed-Still-Open Prior Findings (not duplicated here)

The council's `audit-synthesis-council-20260705.md` already catalogs 38 findings across four tiers (I-1…I-7 tasked as 288/291/293/294/297/298; S-1…S-13 partially tasked as 289/290/292/295/296/299/300/301; N-1…N-17 and DQ-1…DQ-7 **not yet tasked**). Spot-verification this session:
- **N-10** (tag policy loader) — re-confirmed as **F5** above, still no task.
- **S-12** (`OllamaGpuSlotScheduler` `IOptionsMonitor` constructor-only-read no-op) — not independently re-verified this session; flagging that F4 found a *different*, more severe instance of the same underlying pattern class in `MemoryChatAgent`, suggesting the `IOptions`/`IOptionsMonitor` audit needs a full sweep rather than one-off fixes (see F4 recommendation #3).
- **DQ-2 / S-3** (`AllowRawHtml` × `AutoEditorForAuthenticatedUsers` stored-XSS composition) — not re-verified this session; still listed as a design question awaiting a council decision, not a task. Given F1 (CSRF exemption regression) landed in the same commit family, this composition risk deserves a fresh look once F1 is fixed, since CSRF-protected admin/editor actions are one of the mitigating factors that make the XSS chain harder to trigger remotely.
- Everything else in N-1…N-17/DQ-1…DQ-7 is presumed still accurate per the council doc; not re-verified here to avoid duplicating that document's own (already thorough) analysis.

---

## Assumptions

1. `db04b23` is genuinely `master`/the active branch tip as of 2026-07-05 20:31 UTC; no newer commits exist that this audit missed (GitHub API was rate-limited mid-session on unauthenticated requests — raw file fetches were unaffected, but a couple of directory-listing calls for `MemorySmith.Tests/` had to be worked around via the pre-fetched recursive tree instead of live API calls).
2. `MemoryChatAgent` (F4) is registered exactly once via `AddScoped<IChatAgent, MemoryChatAgent>()` in `MemorySmithChatSetup.cs:20` with no secondary registration path that might inject a different options type elsewhere.
3. F2's exploitability assumes a deployment shape where `HttpContext.Connection.RemoteIpAddress` can be null in production traffic. I cannot confirm this from source alone — it depends on hosting configuration (Kestrel-direct vs. IIS in-process vs. behind a proxy without forwarded-headers wiring). This is the same open question the council doc already flagged and couldn't resolve from source either.
4. F3's severity assumes MemorySmith may be deployed behind a reverse proxy at some point. If the operator's deployment is confirmed Kestrel-direct-only and will remain so, F3's risk is theoretical.
5. TSK-0288 was not re-verified against current `.gitignore`/`.githooks/pre-commit` content this session; its "Done" status is taken at face value except for the task's own caveat that git-history purge remains a manual follow-up step.

## Open Questions

1. Is MemorySmith's production deployment Kestrel-direct or behind a reverse proxy? This single answer resolves both F2's and F3's real-world severity.
2. Was `git filter-repo` (or equivalent history purge) ever run for the TSK-0288 secrets? The task's own "Done" comment says this was left as "a separate manual step" — recommend confirming this explicitly rather than assuming it happened.
3. Which of the 15 `IOptions<MemorySmithOptions>`-consuming files (listed in F4 recommendation #3) are intentionally reading boot-time-only config (e.g., paths, connection strings) vs. accidentally missing live-reload for behavior toggles? This needs a per-file classification pass this audit didn't have scope to complete.
4. Does any controller genuinely need `[IgnoreAntiforgeryToken]` for a reason other than "already has API-key auth" (F1)? A per-controller audit of actual auth mechanism is needed before removing the blanket exemption, to avoid breaking legitimately-exempt endpoints.

## Coverage & Scope

Read in full: all 15 controllers, `SecurityServices.cs`, `BootstrapGate.cs`, `GitHubOAuthCallbackHandler.cs` (+ its test file), `MemorySmithRequestGuardMiddleware.cs`, `MemorySmithSecuritySetup.cs`, `MemorySmithOptions.cs`, `AdminSettingsService.cs` header/DI section, `MemoryGovernanceServices.cs` (tag policy section), `SqliteMemorySmithDatabase.cs` (full, 1455 lines), the `f448503` implementation diff (full), and `council/audit-synthesis-council-20260705.md` (full, 391 lines).

Pattern-swept (grep-based, not line-by-line) across all 95 `MemorySmith.App/*.cs` files: `catch` blocks without adjacent logging/rethrow (54 hits triaged, all but the ones cited above resolved as legitimate `Try*`-pattern error propagation), `async void` (zero hits), `IOptions<MemorySmithOptions>` vs `IOptionsMonitor<MemorySmithOptions>` injection sites (full list in F4).

**Not read this session, sampled only in prior audit rounds per the council doc's own admission (55% confidence no additional Critical items hide there):** `ChatServices.cs` full body beyond the option-injection lines (~3,900 lines), `ChatToolCatalog.cs` (~1,900 lines), `CodeSearchService.cs` (~3,100 lines), `MaintenanceAgentServices.cs` (~2,400 lines), `TaskDomainService.cs`, `AgentSessionService.cs` + `SqliteAgentSessionStore.cs`, `PageService.cs`, `SemanticEmbeddingSearchService.cs` beyond the four cited catch blocks, Blazor `.razor`/`.razor.cs` component code, `MemorySmith.Bridge/Program.cs` (34KB), `MemorySmith.Training` Python harness, and all PowerShell scripts. This audit's incremental value was concentrated on (a) verifying the five just-closed Critical tasks against actual diffs rather than trusting task-status labels, and (b) the specific files those tasks touched. A genuine "every line of every file" pass across the ~1,100 tracked blobs in this repo is multiple orders of magnitude beyond what one review session can respons­ibly claim — the honest scope claim is what's listed above, not full-repo coverage.

**Recommended next-session targets, in priority order:** `ChatServices.cs` full read (largest, most-cross-referenced-by-findings file per the council doc's own observation that ~60% of prior findings cluster in 4 files), `CodeSearchService.cs` (explicitly flagged by the council as never read end-to-end), `AgentSessionService.cs`/`SqliteAgentSessionStore.cs` (session lifecycle + SQLite concurrency, a pattern class that already produced real bugs in the sibling MemorySmith.Agent repo per session memory).
