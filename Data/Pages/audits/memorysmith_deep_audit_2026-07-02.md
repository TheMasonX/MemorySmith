# MemorySmith deep code audit

Audited tip: `e300fb4` on `master` (Jun 17, 2026).

## Scope and confidence

This pass focused on the high-risk surfaces that drive the app’s behavior end-to-end: host composition, security/auth, configuration layering, page/task/content storage, chat/agent orchestration, maintenance loops, and the repo’s current task/wiki surface. I reviewed the host wiring and the main services that decide whether the app fails closed or silently falls back.

I was **not** able to enumerate every task record and sprint-plan file individually from the repo tree with the tooling available, so I treat task/plan contents as an open question rather than pretending full coverage. The repo map and recent history clearly show active task-driven work, including recent host refactors and chat-loop consolidation, so I avoided recommending anything that would obviously duplicate those themes.

## Executive summary

### What is in good shape
- The host has already been split into named setup modules instead of a single monolithic `Program.cs`, which is a real maintainability win.
- The page and attachment path resolvers are generally defensive: they normalize, percent-decode safely, and block traversal before file access.
- The chat tool loop has been unified into a shared driver instead of duplicating streaming and non-streaming logic.
- The task storage layer is intentionally resilient to malformed files and preserves data on writes with temp-file swap patterns.

### Highest-risk issues
1. **Remote-hardened mode still enables remote API access by default.**  
   In `MemorySmithLocalDevelopmentPostConfigure.ApplySecurityProfile`, the `RemoteHardened` preset applies `AllowRemoteApi = true`. That is a surprising default for a profile named “remote-hardened,” and it weakens the trust boundary unless an operator notices and overrides it.  
   Confidence: **95%**.

2. **Settings override corruption can be silently ignored.**  
   `LoadOverrideKeys()` swallows `JsonException`, `IOException`, and `UnauthorizedAccessException`, then returns an empty key set. That means a damaged or inaccessible override file can cause profile defaults to be reapplied without a visible failure.  
   Confidence: **92%**.

3. **Request metadata hashing can silently rotate to a random key on file-system problems.**  
   `RequestMetadata.LoadOrCreateHmacKey()` returns a new random HMAC key on any exception, which breaks correlation continuity and can make audit trails harder to analyze.  
   Confidence: **93%**.

4. **Loopback detection is fail-open when the remote IP is missing.**  
   `MemorySmithRequestGuardMiddleware.IsLoopback(null)` returns `true`. That may be acceptable for some local-only hosting cases, but it is a brittle implicit contract and should be justified or tightened.  
   Confidence: **80%**.

5. **API-key authorization is broader than the name suggests.**  
   `MemorySmithPermissionHandler.HasConfiguredApiKeyAccess()` grants permission success whenever a valid API key is present on a path that requires it, which bypasses role checks entirely for those paths. That may be intentional for automation, but it is a very wide grant and deserves an explicit design decision.  
   Confidence: **88%**.

### Architectural read
The codebase is actively moving from ad hoc, implicit fallback behavior toward explicit host modules and tool seams. The main risk is that several legacy “helpful” fallbacks still remain in place, and a greenfield project like this cannot afford those to become permanent policy. The core recommendation is to convert silent recovery paths into visible, testable states: either fail closed, or log and surface a degraded mode clearly.

## Findings

### 1) Remote-hardened profile is not actually hardened
**Evidence:** `MemorySmith.App/Services/MemorySmithLocalDevelopmentPostConfigure.cs` in `ApplySecurityProfile()`, `case MemorySmithSecurityProfiles.RemoteHardened:` applies `AllowRemoteApi = true`.  
**Why it matters:** This flips the most dangerous network surface on by default in the one profile whose name implies the opposite. Because the request guard and permission handler both branch on `AllowRemoteApi`, this is a trust-boundary decision, not a cosmetic default.  
**Recommendation:** Make the hardened preset deny remote API access unless the operator explicitly opts in. If the intended meaning is “remote-hostable but authenticated,” rename the profile so it cannot be misread.  
**Confidence:** **95%**.

### 2) Override-file parsing fails silently and can re-enable defaults
**Evidence:** `MemorySmith.App/Services/MemorySmithLocalDevelopmentPostConfigure.cs`, `LoadOverrideKeys()` catches `JsonException`, `IOException`, and `UnauthorizedAccessException` and returns an empty set.  
**Why it matters:** `ApplyIfMissing()` uses the key set as the only signal for whether a setting was intentionally persisted. If parsing fails, the app treats every setting as missing and re-applies profile defaults. That is a hidden configuration rollback.  
**Recommendation:** Record a structured warning, surface an admin-visible health signal, and distinguish “file missing” from “file unreadable/corrupt.”  
**Confidence:** **92%**.

### 3) Request metadata key failures are invisible
**Evidence:** `MemorySmith.App/Services/RequestMetadata.cs`, `LoadOrCreateHmacKey()` returns a freshly generated random key from a broad `catch`.  
**Why it matters:** Metadata hashes for IP/user agent are used for audit and login history. If the key flips silently, the same user and device stop correlating over time, and the operator gets no clue why.  
**Recommendation:** Fail with a logged degraded mode, or persist a fixed key in a location with explicit startup validation.  
**Confidence:** **93%**.

### 4) Loopback detection is an implicit fail-open contract
**Evidence:** `MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs`, `IsLoopback(IPAddress? address)` returns `true` when `address is null`.  
**Why it matters:** This means an absent remote IP is treated as local. That may be convenient in tests, but it is a sharp edge in proxy/container/service scenarios where the remote IP is missing or rewritten.  
**Recommendation:** Separate “unknown” from “loopback.” If tests need the shortcut, inject a test-only abstraction instead of baking it into the production helper.  
**Confidence:** **80%**.

### 5) API-key presence bypasses role checks too broadly
**Evidence:** `MemorySmith.App/Services/SecurityServices.cs`, `MemorySmithPermissionHandler.HasConfiguredApiKeyAccess()` returns `true` for any path that `RequiresApiKey()`.  
**Why it matters:** This creates a second authorization axis that can override RBAC on broad API surfaces. It is easy to accidentally grant more than intended, especially as new `/api/*` endpoints are added.  
**Recommendation:** Limit this to explicit machine-to-machine routes, not all `/api` surfaces. Treat the API key as an authentication mechanism that still maps to a least-privilege principal.  
**Confidence:** **88%**.

### 6) Content endpoint authorization is conservative, but the policy boundary is easy to misread
**Evidence:** `MemorySmith.App/Hosting/MemorySmithContentEndpoints.cs`, `CanViewPageAssetAsync()` allows unreferenced assets only to `CanEditMemorySmith`.  
**Why it matters:** This is safer than open static hosting, but it couples “unreferenced asset” to “editor access.” That is a policy decision, not a technical necessity. In a greenfield system, policy should be explicit and named.  
**Recommendation:** Split “asset exists,” “asset is referenced,” and “asset is visible” into explicit policy states.  
**Confidence:** **72%**.

### 7) Task loading masks corruption by converting bad files into fallback records
**Evidence:** `MemorySmith.App/Services/TaskDomainService.cs`, `LoadAll()` catches `Exception`, logs a warning, and creates a malformed-task fallback record.  
**Why it matters:** This is resilient, but it can also hide systemic data corruption behind apparently valid task rows. That makes technical debt linger because the system keeps limping along instead of forcing repair.  
**Recommendation:** Keep the fallback record, but add a visible “degraded data” count and an operator-facing repair workflow. Make malformed records uneditable until repaired, which this code already partly does.  
**Confidence:** **87%**.

### 8) Settings discovery can accidentally bind to stale local files
**Evidence:** `MemorySmith.App/Services/MemorySmithConfigurationPaths.cs` searches ancestors for `appsettings.LocalOverrides.json` and also checks an `artifacts/MemorySmith.App/` path.  
**Why it matters:** Ancestor discovery is convenient for local dev, but it can also pick up stale or unintended files when the app is launched from nested working directories or copied artifacts.  
**Recommendation:** Make the discovery mode explicit, or require a single configured path outside local development.  
**Confidence:** **81%**.

### 9) The maintenance loop is intentionally defensive, but the current design still depends on hidden timing assumptions
**Evidence:** `MemorySmith.App/Services/MemoryMaintenanceService.cs` delays, then runs triage/index/consolidation on fixed intervals and logs failures.  
**Why it matters:** The service is robust enough not to crash on one task failure, but it still assumes the loop cadence is good enough for all workloads. That can be brittle for indexing backlogs or slow IO.  
**Recommendation:** Add per-task backoff and persistent run-state checkpoints, especially for indexing and consolidation.  
**Confidence:** **77%**.

## Positive refactor opportunities to keep

### Preserve these patterns
- The named host modules in `MemorySmith.App/Hosting/*` are the right direction. Keep pushing composition out of `Program.cs`.
- The path normalization in page and attachment serving is worth preserving; it is the kind of logic that should stay isolated and unit-tested.
- The unified chat tool loop is a good consolidation point. Do not reintroduce separate streaming and non-streaming execution paths.

### Consolidate next
- Unify all “configuration file discovery” into one policy layer with explicit startup diagnostics.
- Replace silent catches with explicit degraded-state recording in settings, metadata, and any file-backed fallback path.
- Centralize network trust decisions: loopback, remote API, API key, and admin bootstrap should all be driven from one least-privilege matrix.
- Split policy from mechanism in content access. File presence, references, and visibility should be distinct concepts.

## Planning guidance for the next implementation pass

1. Make the security profile semantics explicit and rename or rebase `RemoteHardened` if its current behavior is intentional.
2. Add startup validation for settings override files and request-metadata key files.
3. Convert “silent fallback” paths into “visible degraded mode” paths with metrics and admin diagnostics.
4. Write tests around:
   - `RemoteHardened` network behavior
   - malformed override file handling
   - request-metadata key loss
   - null remote IP handling
   - API-key-gated route privilege boundaries
5. Only after that, prune legacy fallbacks that are no longer needed.

## Assumptions and open questions

- I assumed the latest commit on `master` is the correct audit target, not an older branch tip.
- I could not enumerate the individual `Data/Tasks` records or sprint-plan files directly with the available traversal tools, so I treated them as an open question rather than a source of truth.
- I assumed the intent behind `RemoteHardened` is “production exposed host.” If it instead means “remote host that still allows remote API access behind another boundary,” the profile name should still change.
- I did not inspect every generated or binary artifact file, only the source and configuration surfaces that drive runtime behavior.
- Confidence values reflect code evidence strength and how much behavior depends on surrounding, unseen contracts.

## Bottom line

The codebase is directionally improving, but there are still several legacy-style fail-open and silent-recovery behaviors that should be treated as debt to remove, not features to preserve. The most urgent fixes are the hardening profile default, the silent configuration fallback, and the metadata-key failover path.
