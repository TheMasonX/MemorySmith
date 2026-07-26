# MemorySmith Deep-Dive Audit — Sprint 60 Wave C In-Progress Focus
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` · **Commit:** `6281037` ("Sprint 1: Wave of high-ROI safe fixes from ready task queue", 2026-07-11 08:18:23Z)
**Scope method:** GitHub tarball snapshot (bypasses API rate limiting) + full-file reads of every file touched by the current sprint wave and every file named in the Wave C plan; cross-checked against `Data/Tasks/*.json` status fields to avoid duplicating already-tracked or already-closed findings.
**Report generated:** 2026-07-11

---

## 0. Coverage Statement (read this first)

The repo is ~1,220 tracked files / ~52k lines of C# (per prior audit history) plus a large `Data/` KB corpus that is content, not code. A true every-line pass over the whole tree in one sitting is not what happened here, and claiming otherwise would be a bigger integrity problem than any bug below. What actually happened:

- **Full-file, line-by-line review**: `MemoryStateMachine.cs`, `MemoryIndex.cs`, `OAuthBridgeController.cs`, `SourceLinksController.cs`, `AdminController.cs` (diff + surrounding methods), `AdminSettingsService.cs` (diff + surrounding methods), `MemoryMaintenanceTasks.cs`, `StateTransitionTests.cs`, `MemorySmithSecuritySetup.cs`, relevant sections of `MemoryApplicationService.cs`, `ChatServices.cs` (catch-block and SplitThinking regions), `harness.py` (hyperparameter resolution).
- **Targeted grep/structural sweep**: full repo for `FixedTimeEquals`, `.ById`/`.ByTag`/`.ByReference`, `.Evaluate(`, `warmup`, dead search-tool doc references, `JsonPropertyName` coverage across `MemorySmith.Core/Models/*.cs`.
- **Not independently re-audited**: `SqliteMemorySmithDatabase.cs` (1,455 lines), `MemoryApplicationService.cs` in full, `ChatServices.cs` in full (3,591 lines) — these are already the subject of open, correctly-scoped tasks (TSK-0381, TSK-0042, TSK-0346/0371) and re-deriving the same findings from scratch would burn effort without adding evidence. Where I did look, I cite specifics below rather than re-stating the task description.

**Why this scope:** the request specifically asked to prioritize "currently in progress areas." Sprint 60 Wave C (`Data/Pages/Handoffs/handoff-sprint60-wave-c-20260711.md`) is the explicit current sprint. Commit `6281037` just landed five of its items minutes before this audit began, so that diff got first-priority scrutiny — new code is where regressions live, and "tests pass" is not the same as "the feature is reachable in production," which is the load-bearing pattern in three of the findings below.

---

## 1. Executive Summary

| # | Finding | Confidence | Severity | Status vs. backlog |
|---|---|---|---|---|
| F1 | `MemoryStateMachine`: Deprecated records that recover to a *middling* score (between 0.16 and 0.81) can never leave Deprecated | 90% | Medium (data-quality/architectural gap) | **New** — not covered by TSK-0379/TSK-3087, which just shipped |
| F2 | `MemoryIndex`: exposed as raw mutable public dictionaries; only `Add`/`Remove`/`Rebuild` are lock-protected — no read-side locking exists anywhere, so the "thread safety" fix is partial | 85% | Medium (latent race, `InvalidOperationException` under concurrent mutation) | **New nuance** on top of Done task TSK-3080 |
| F3 | `MemoryIndex.ByReference` is fully populated but has **zero production readers**; reverse-reference/backlink lookups are independently reimplemented as O(N) full-table scans in two places | 95% | Medium (perf debt, duplication) | Matches open TSK-0377/TSK-3077 (Ready) and TSK-0316; confirms scope, adds file:line evidence |
| F4 | `OAuthBridgeController`: anonymous, unauthenticated, un-rate-limited passthrough proxy to GitHub's OAuth token endpoint with no `state` validation, no CSRF token, and no server-side `client_secret` injection visible anywhere in the codebase | 80% | High (security) | Matches open TSK-0384 (Ready); confirms exact gap with full-file citation |
| F5 | TSK-0296 ("consolidate 3 `FixedTimeEquals` copies") and TSK-0294 ("scrub dead search-tool refs") are marked Done and **verified correct** — no duplicate implementations, no stale doc references found | 90% | Info | Confirms Done status; no action needed |
| F6 | `ChatServices.cs` still contains 9+ bare `catch { }` blocks with no logging (file cleanup + path-trust paths) | 85% | Low-Medium (observability gap) | Overlaps existing TSK-0346/TSK-0371 — evidence only, not a new task |
| F7 | `harness.py` docstring claims `warmup_steps` defaults to 10; the actual resolver defaults to 0 | 95% | Low (doc/behavior mismatch) | Matches open TSK-0298 (Ready); pinpoints exact line |
| F8 | `AdminController.SetupFallback()` catch-all returns `BadRequest` for any unrecognized Content-Type on `/api/admin/setup`, including a **missing** Content-Type header on a legitimately empty/GET-like probe — verified as intentional per commit message, no bug found | 70% | Info | Verifies TSK-3086 fix is sound |
| F9 | `Data/Tasks/*.json` — two files (TSK-0294, TSK-0296) are UTF-8 **BOM-prefixed**, breaking naive `json.load()`; all other task JSONs sampled were clean | 60% | Low | New — minor tooling risk, not previously logged |

**Net read:** the Sprint 60 Wave C landing (`6281037`) is a clean, well-tested, low-risk commit — the state-machine, admin, and source-link changes do what they say. The gaps found are *adjacent* to what just shipped: the state machine's new demotion/re-promotion logic has an untested boundary condition (F1), and the "wire the index into search" work (TSK-3077/0377) that the same wave depends on is confirmed still not started, with concrete before/after evidence for when it does land (F2, F3).

---

## 2. Detailed Findings

### F1 — Deprecated→Working re-promotion has a dead zone (Medium, 90%)

**File:** `MemorySmith.Core/StateMachine/MemoryStateMachine.cs`, lines 11–39.

```csharp
if (allowDeprecation && score < DeprecationThreshold && original != MemoryStatus.Deprecated)
    newStatus = MemoryStatus.Deprecated;
else if (original == MemoryStatus.Unconsolidated && score >= WorkingThreshold)
    newStatus = MemoryStatus.Working;
else if (original == MemoryStatus.Working && score >= CoreThreshold)
    newStatus = MemoryStatus.Core;
else if (original == MemoryStatus.Core && score < CoreThreshold)
    newStatus = MemoryStatus.Working;
else if (original == MemoryStatus.Deprecated && score >= WorkingThreshold)
    newStatus = MemoryStatus.Working;
```
Thresholds: `DeprecationThreshold = 0.16`, `WorkingThreshold = 0.81`, `CoreThreshold = 1.62`.

A record sitting in `Deprecated` whose score recovers to, say, 0.40 (above deprecation, below working) matches **no branch** — it stays `Deprecated` indefinitely. The only exit from `Deprecated` requires jumping straight past `WorkingThreshold` (0.81), a 5x recovery from the deprecation floor. Contrast this with `Core`, which demotes as soon as it merely drops below `CoreThreshold` (no lower bound) — the two ends of the lifecycle are asymmetric by design or by oversight; the commit message and PR description for TSK-3087 only mention "recovers above WorkingThreshold," so this reads as an intentional simplification rather than a typo, but it is not documented as a known limitation anywhere I found.

**Reachability confirmed real** (not dead code): `MemoryMaintenanceTasks.RunTriageAsync` (line 33-36) calls `Evaluate()` over *every* stored record regardless of status, and `RunTriageAsync` is wired into the always-on background loop in `MemoryMaintenanceService.cs` (`RunTrackedAsync("TriageService", ...)`). So this dead zone will actually bite in production, not just in unit tests.

**Recommendation:** add an explicit `else if (original == MemoryStatus.Deprecated && score >= DeprecationThreshold)` → `Unconsolidated` (not `Working`) reconsolidation branch, so a recovering record re-enters the front of the pipeline instead of being stuck. Add a `StateTransitionTests` case for score ∈ (0.16, 0.81) starting from `Deprecated` to lock in whichever behavior is chosen — right now no test exercises this band (the four new tests cover exactly-at-threshold and full-recovery cases, not the gap itself).

**Open question:** is the "must fully clear WorkingThreshold" behavior deliberate (a hysteresis guard against churn, per the code comment) or an oversight? If deliberate, it should be documented as a design decision in `Data/Pages/architecture.md` and the gap band should have an explicit test proving it's intentional, not just an absence of a test.

---

### F2 — `MemoryIndex` thread-safety fix protects writes only (Medium, 85%)

**File:** `MemorySmith.Core/Indexing/MemoryIndex.cs`, full file (84 lines).

```csharp
public Dictionary<string, MemoryRecord> ById { get; } = new();
public Dictionary<string, HashSet<string>> ByTag { get; } = new();
public Dictionary<string, HashSet<string>> ByReference { get; } = new();
```
`Add`, `Remove`, and `Rebuild` each take `_lock.EnterWriteLock()`. There is no `EnterReadLock()` anywhere in the file, and the three dictionaries are exposed as public mutable references (not `IReadOnlyDictionary` or a locked-accessor method). Two consequences:

1. Any caller that enumerates `index.ByTag["x"]` while a background `Rebuild()` (from the always-on `IndexingService`/`ConsolidationService` maintenance loop) is mutating that same `HashSet<string>` can hit `InvalidOperationException: Collection was modified` or silently observe a torn read — the lock does nothing for readers.
2. Because the properties are public gettable fields backing live mutable collections, any consumer can also **mutate** them directly (`index.ById.Remove(...)`), bypassing the lock entirely — this is confirmed by `MemorySmith.Tests/MemoryMaintenanceTasksTests.cs` lines 56-58, which read `_index.ById`, `_index.ByTag[...]`, `_index.ByReference[...]` directly with no lock.

The handoff doc lists TSK-3080 as ✅ Done with the summary "ReaderWriterLockSlim with lock-free AddCore/RemoveCore to prevent recursion" — that's an accurate description of what was built, but "thread safety" as a headline slightly overstates it: it's write/write and write/rebuild safety, not reader/writer safety, because there is currently exactly one code path (tests) that reads the collections, so the gap hasn't been exercised yet. This will matter the moment TSK-3077 wires the index into the live search path (see F3), because search reads will then run concurrently with the background rebuild/consolidation writes.

**Recommendation:** either (a) add `EnterReadLock()`/`ExitReadLock()` wrapping any future read methods and change the public surface to expose read-only snapshots or a `Lookup(...)` method rather than raw dictionaries, or (b) if a lock-free reader pattern is preferred, swap to `ConcurrentDictionary<string, ...>` for all three collections instead of hand-rolling reader/writer locking around plain `Dictionary`. Flag this as a blocking pre-requisite for TSK-3077, not a follow-on — wiring live reads onto unlocked dictionaries first and adding locking later is the harder order to do safely.

---

### F3 — Index built but never read; two independent O(N) backlink scans exist instead (Medium, 95%)

**Evidence:**
- `MemoryApplicationService.cs` calls `_index.Add`/`_index.Remove` on every create/update/delete (lines 563, 602-603, 638, 691-692) — write-only.
- Repo-wide grep for `.ById`, `.ByTag`, `.ByReference` outside `MemoryIndex.cs` itself returns **only** the three lines in `MemoryMaintenanceTasksTests.cs` cited above. No production code reads any of the three lookup structures.
- Meanwhile, `MemoryApplicationService.cs` independently computes reverse references twice:
  - `EnumerateLinks` (local function, lines 424-456) does `allRecords.Where(...)` + nested `.Any(...)` over every other record's `References`/`Conflicts` to find backlinks for a context pack.
  - `GetReverseReferencesAsync` (lines 512-529) does the identical `LoadAllSynced()` (full store read + full relationship sync) + `.Where(...).Any(...)` scan for the "Incoming References" panel.

Both are O(N) per lookup where `MemoryIndex.ByReference` already provides O(1) lookup for exactly this relationship, sitting unused. This matches and gives file:line evidence for the already-open, correctly-scoped tasks **TSK-0377/TSK-3077** ("Wire MemoryIndex into search query path," Ready, High priority) and **TSK-0316** ("de-duplicate reverse reference computation"). No new task needed — this section exists to hand the implementing engineer exact call sites rather than have them re-discover the duplication.

**Implementation guidance for TSK-3077:**
1. Fix F2 first (locking/collection type), or the wiring itself introduces the race.
2. Replace `EnumerateLinks`'s backlink loop and `GetReverseReferencesAsync`'s scan with `_index.ByReference.TryGetValue(id, out var referencingIds)` — this collapses two near-duplicate implementations into one code path reading the index, closing TSK-0316 as a byproduct.
3. Note `ByReference` today only indexes `record.References`, not `record.Conflicts` (see `AddCore`, `MemoryIndex.cs` line 67) — conflicts are walked separately in `EnumerateLinks`. Either extend `MemoryIndex` with a parallel `ByConflict` dictionary (the handoff and TSK-0318/0319 language already anticipates a "ByConflict" index) or keep a conflicts-only linear scan; don't assume `ByReference` alone is a complete replacement.

---

### F4 — `OAuthBridgeController` is an unauthenticated, un-rate-limited OAuth relay (High, 80%)

**File:** `MemorySmith.App/Controllers/OAuthBridgeController.cs`, full file (64 lines).

```csharp
[ApiController]
[Route("")]
[IgnoreAntiforgeryToken]
public class OAuthBridgeController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("authorize")]
    public IActionResult Authorize() => Redirect($"{GitHubAuthorizeEndpoint}{Request.QueryString}");

    [AllowAnonymous]
    [HttpPost("token")]
    public async Task<IActionResult> ExchangeCode(...) { /* forwards raw POST body to GitHub token endpoint, returns response verbatim */ }
}
```

Observations, in order of concern:
1. **No `state` parameter validation.** `Authorize()` passes the incoming query string straight through to GitHub; `ExchangeCode` never inspects or validates a `state` value against anything server-side. This is exactly the gap TSK-3084/TSK-0384 already targets — CSRF/replay protection on the OAuth flow depends on `state` round-tripping correctly, and nothing here checks it.
2. **No client secret handling visible.** Grepped `GitHubOAuthCallbackHandler.cs` and `wwwroot/memorysmith.js` for `client_secret`/`ClientSecret` — zero matches anywhere in the codebase. If the secret is injected via a config binder not caught by this search, that's fine, but if the browser-side JS is expected to supply it in the POST body that this controller blindly forwards, the secret would be client-exposed. **This needs a direct answer from whoever wrote this controller before Wave C ships TSK-3084** — I could not find where (or whether) the secret is added server-side, and I'm flagging this rather than asserting it's missing, because a config-driven `HttpClient` default header set up elsewhere in DI would not show up in a text grep of these two files alone.
3. **No rate limiting**, unlike every other public/anonymous auth-adjacent endpoint touched in this same sprint wave (`AdminController` setup endpoints and `SourceLinksController.Open` both got `[EnableRateLimiting("login")]` in commit `6281037`). `OAuthBridgeController` was not touched by that commit and remains unlimited — an attacker can hammer `/token` to relay arbitrary POST bodies to GitHub with no throttling, which is at minimum a griefing/quota-exhaustion vector against the app's own GitHub OAuth app.
4. `[IgnoreAntiforgeryToken]` on the whole controller is likely necessary (this is a pre-auth OAuth bounce point, standard antiforgery cookies won't exist yet) but combine with points 1 and 3 and the entire controller has no protection layer at all beyond "the destination happens to be a fixed GitHub URL."

**Recommendation (for TSK-3084):** add `state` generation server-side (encrypted/signed, e.g. via `IDataProtector` — already used elsewhere in this codebase for keys, see `MemorySmithSecuritySetup.cs` line 92's `AddDataProtection()`), validate it on the token exchange, and add `[EnableRateLimiting("login")]` to both actions to bring this controller to parity with the rest of this sprint's hardening pass.

---

### F5 — Two Wave C cleanup tasks are already correctly closed (Info, 90%)

- **TSK-0296** ("consolidate `FixedTimeEquals` into shared helper, 3 copies → 1"): repo-wide grep found exactly **one** real implementation, `SecurityCompare.FixedTimeEquals` (`MemorySmith.Core/Security/SecurityCompare.cs`), called from `SecurityServices.cs:335`, `MemorySmithRequestGuardMiddleware.cs:94`, and indirectly via `FixedTimeEqualsOrdinalIgnoreCase` from `BootstrapGate.cs:55`. No duplicate raw `CryptographicOperations.FixedTimeEquals` calls exist elsewhere. Status field confirms `Done`. **No action needed; verified correct.**
- **TSK-0294** ("scrub dead search-tool refs `unified_search`/`semantic_search` from README/guides"): grepped `README.md` and `Data/Pages/guides/*.md` for both dead tool names — zero matches. Status confirms `Done`. **No action needed; verified correct.**

Both files' task JSONs (`tsk-0294-*.json`, `tsk-0296-*.json`) load with a UTF-8 BOM that breaks a plain `json.load()` (see F9) — worth knowing if anyone scripts around the task backlog.

---

### F6 — Remaining silent `catch { }` blocks in `ChatServices.cs` (Low-Medium, 85%)

Not a new task — this is supporting evidence for the already-open **TSK-0346** ("fix silent-catch blocks across codebase") and **TSK-0371** ("replace silent catches with structured logging"), scoped here to save the implementer a grep pass.

Bare `catch { }` / `catch (Exception ex)` with no logging, confirmed present at (line numbers from this commit's `ChatServices.cs`): **348, 369, 394, 423, 439, 811, 1429, 1445, 1565, 1583, 1601, 1619, 2375, 3315**. Sampled in detail: lines 340-443 (chat-attachment temp-file cleanup + trusted-path checks) — every failure path returns a counted result (`ChatAttachmentCleanupResult` with a `Failed`/`Skipped` bucket) so the *caller* isn't blind, but there is zero `ILogger` output, so an operator debugging "why didn't my attachment get cleaned up" has no diagnostic trail beyond an aggregate count. This is a genuine but modest observability gap, not a correctness bug — the counting pattern already prevents the worst failure mode (silently losing track of state).

**Recommendation:** when TSK-0371 lands, prioritize the cleanup-path catches (340-443) last (they're already counted/safe) and the ones without any counting/fallback signal first — I did not have budget this pass to classify all 14 by risk; that classification is the actual deliverable of TSK-0371 and shouldn't be pre-empted here.

---

### F7 — `harness.py` docstring/behavior mismatch on `warmup_steps` default (Low, 95%)

**File:** `MemorySmith.Training/harness.py`, lines 144-164.

```python
def resolve_hyperparameters(self) -> dict[str, Any]:
    """...
    - warmup_steps default is 10 (not 0)
    ...
    """
    ...
    warmup_steps = max(0, min(int(hp.get("warmupSteps") or 0), 100000))
```

The docstring asserts a default of 10; the code's `or 0` fallback makes the actual default **0**. This is exactly TSK-0298's scope ("Ready," Medium priority, flagged as Python-only tooling). Fix is a one-line choice: either change the fallback to `or 10` to match the documented intent, or fix the docstring to say `0` — needs a decision on which is the *intended* default (10 implies "always warm up a little," 0 implies "off unless requested"), which I can't infer from this file alone since no other reference to a "10-step default" appears elsewhere in the training harness.

---

### F8 — `AdminController.SetupFallback()` verified sound (Info, 70%)

The new catch-all `[HttpPost("setup")]` action (no `[Consumes]` filter, added in `6281037`) exists specifically to avoid the `AmbiguousMatchException` (500) that ASP.NET Core throws when a request arrives with no `Content-Type` header and two `[Consumes]`-disambiguated siblings both decline to match. It returns a clear `400 BadRequest` with an explanatory message rather than a raw 500. I checked for a routing-precedence issue (does the fallback ever shadow the two typed actions for requests that *do* have a valid Content-Type?) — ASP.NET Core's endpoint selection scores `[Consumes]`-qualified actions higher than an unqualified one for matching requests, so the two original actions still win when Content-Type is present. This looks correct; flagged at Info/70% only because I did not spin up the app to integration-test the routing behavior directly, only reasoned about it from the attribute semantics.

---

### F9 — BOM-prefixed task JSON files (Low, 60%)

`Data/Tasks/tsk-0294-*.json` and `Data/Tasks/tsk-0296-*.json` both begin with a UTF-8 BOM (`\ufeff`), which breaks `json.load()` (works fine with `encoding="utf-8-sig"`). A small sample of other `Data/Tasks/*.json` files loaded cleanly without BOM. This is likely an artifact of whatever editor/tool last touched those two specific files. Low severity, but if the wiki/task tooling (`Test-TaskRecords.ps1`, task backlog validators referenced in TSK-0114/TSK-0281) doesn't already tolerate BOM, this could cause a silent parse failure in CI. **Not independently verified against the actual PowerShell validator scripts** — flagging as a low-confidence "worth a 30-second grep" item rather than a confirmed CI risk.

---

## 3. Assumptions

- The tarball fetched via `codeload`-equivalent archive URL for `dev/sprint-1` is authoritative HEAD; confirmed via the branch's Atom commit feed, whose newest entry (`6281037...`, same SHA the person linked, timestamp 2026-07-11T08:18:23Z) matches the tarball contents exactly.
- "Currently in progress areas" was interpreted as: the current sprint plan (`architectural-stability-cleanup-sprint-20260710.md`) + the live Wave C handoff (`handoff-sprint60-wave-c-20260711.md`) + the diff of the most recent commit. If a different in-progress area was meant (e.g., the training harness or code-search subsystem specifically), this report under-covers it.
- Findings assume the task-status JSON files in `Data/Tasks/` are kept current relative to actual code state; F1/F2/F3 were verified by reading code directly, not by trusting task status alone, specifically because the historical pattern in this project (per prior audits) is "task marked Done, feature still unreachable."

## 4. Open Questions for the Implementing Engineer

1. Is the Deprecated→Working "must fully clear WorkingThreshold" behavior (F1) intentional hysteresis, or should there be a Deprecated→Unconsolidated path for partial recovery?
2. Where (if anywhere) is the GitHub OAuth `client_secret` actually injected into the token exchange (F4)? If nowhere, is this bridge intentionally designed for a GitHub OAuth App type that doesn't require a secret (e.g., a public/PKCE flow), or is this a gap that predates this audit?
3. For F2/F3: should `MemoryIndex` move to `ConcurrentDictionary`-backed storage before or as part of TSK-3077, given that wiring live reads onto the current lock model would be the wrong order of operations?
