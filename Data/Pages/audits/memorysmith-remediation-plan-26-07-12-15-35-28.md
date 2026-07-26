# MemorySmith — Consolidated Remediation Plan
**Scope:** resolves all findings F1–F22 from the four audit reports (`memorysmith-sprint60-wavec-audit-*`, `memorysmith-delta2/3/4-audit-*`) plus the follow-up "biggest red flags" review, against `TheMasonX/MemorySmith`, branch `dev/sprint-1`, as of commit `e8a3065`.
**Author's framing:** this is not "22 tickets." Clustered by root cause, it's **6 workstreams**. Fixing the root cause in each cluster closes multiple findings at once and — more importantly — stops the next instance of the same failure mode from being findable by the next audit.
**Two open questions from prior reports resolved during this planning pass** (see §7 for detail): `BusyTimeoutSeconds` defaults to 30 (non-zero), so F13's downgrade is confirmed with no caveat. `OAuthBridgeController` is a same-origin CORS-relay to GitHub's fixed token endpoint with no server-side secret injection — it's a public-client proxy, not a secret-leak vector — which narrows F4 to CSRF/state/rate-limit hardening only, not credential exposure.

---

## 0. Executive Summary

| Workstream | Findings closed | Priority | Est. effort | Why this order |
|---|---|---|---|---|
| **W1 — Auth/security consolidation** | F4, F11, F19 | P0 | 3–5 eng-days | Only cluster with a real exploit path; everything else is maintainability cost, not risk |
| **W2 — Config-surface trust sweep** | F21, F22 | P1 | 2–3 eng-days + 1 day/future-finding | Cheapest fix-per-finding; actively makes the admin UI/config lie today |
| **W3 — Wire `MemoryIndex` into search (finish TSK-3077)** | F2, F3 | P1 | 3–4 eng-days | Highest-leverage unfinished thread; already fully scoped, this is execution not discovery |
| **W4 — Scoring & state-machine correctness** | F1, F10, F17 | P2 | 1–2 eng-days | Small, isolated, high-confidence fixes; low risk, quick wins |
| **W5 — God-class decomposition** | F12, F15 (+ prior TSK-0042/0191/0192/0285/0043) | P2 | 8–12 eng-days across 4 files | Execution of already-approved plans; sequence by size/risk, not urgency |
| **W6 — Process hardening** | F18, F9, F16, F5-verification habits | P3 | 1–2 eng-days + ongoing discipline | Prevents future *undiscoverable* debt rather than fixing known debt |

**Total estimated effort:** ~20–28 engineer-days if done sequentially by one person; W1–W3 can run in parallel across 2–3 engineers since they touch disjoint files (auth controllers/services vs. indexing vs. state machine).

**If you can only fund one workstream this sprint: W1.** It's the only one where "not yet" has a live downside.

---

## 1. Root-Cause Clusters (why 22 findings become 6 workstreams)

1. **Security/auth logic is reinvented at each call site instead of centralized.** F4 (OAuth bridge has no CSRF/state), F11 (two lockout guards check three different data sources), F19 (two independent path-containment checks) are the same disease: nobody has a single place to put "is this actually safe," so each new safety check is written from scratch and drifts from its siblings. → **W1**.
2. **Config properties are declared before their enforcement lands, and nothing flags the gap.** F21 (`MaxNestingDepth`), F22 (`MaxParallelOpenAIRequests`, `SettingsOverridePath` binding). → **W2**.
3. **A well-designed data structure exists but nothing reads from it.** F2/F3 (`MemoryIndex` is write-only; two O(N) scans do its job manually). → **W3**.
4. **Formula/threshold code has untested boundary conditions.** F1 (`MemoryStateMachine` Deprecated dead-zone), F10 (`MemoryScorer` reference-count dominance), F17 (fragile override-based branch). → **W4**.
5. **Files grew past the point where one person can safely change them.** F12 (`SqliteMemorySmithDatabase`), F15 (sizing data for the other four). → **W5**.
6. **When something goes wrong mid-sprint, the response is to skip it, not track it.** F18 (abandoned doc edit, no task filed) is the clearest single incident; F9 (BOM files) and F16 (migration-never-persists nuance) are smaller instances of "nobody's on the hook for cleaning this up later." → **W6**.

---

## 2. W1 — Auth/Security Consolidation (P0)

### 2.1 Build the shared primitives first (blocks 2.2 and 2.3)

**Task W1.1 — Extract `PathSecurity.IsUnderRoot`** (closes F19)
- **New file:** `MemorySmith.Core/Security/PathSecurity.cs`, sibling to the existing `SecurityCompare.cs` (this project already treats that folder as the home for shared security primitives — follow the precedent).
- **Signature:** `public static bool IsUnderRoot(string candidateFullPath, string rootFullPath)` — take the more defensive of the two existing implementations (`MaintenanceWritePermissionService.IsUnderPath`'s explicit exact-equality branch) as the baseline, since it's a strict superset of `VarResolver.IsUnderRoot`'s behavior.
- **Callers to update:** `VarResolver.IsUnderRoot` (delete, replace call sites at lines 294, 308), `MaintenanceAgentServices.MaintenanceWritePermissionService.IsUnderPath` (delete, replace call sites at lines 487, 500).
- **Test strategy:** port both files' existing path-boundary tests to target the new shared helper directly (unit tests in `MemorySmith.Tests/PathSecurityTests.cs`, new file); keep the existing `VarResolverTests`/`MaintenanceAgentServices`-adjacent tests as integration-level checks that the callers still behave correctly, don't duplicate the boundary-condition matrix in both places.
- **Effort:** 0.5 day. **Risk:** low — behavior-preserving refactor, both implementations already agree on every case traced in the audit.

**Task W1.2 — Extract `SignInMethodResolver.GetEffectiveState`** (unblocks 2.3, informs 2.2)
- **Problem this solves:** `SecurityServices.IsUsableSignInMethod` (lines 794-818) already correctly ANDs three sources of truth for whether a sign-in method is *actually* usable: DB `Providers.IsEnabled`, config `Auth.{LocalPasswordEnabled|Providers.X.Enabled}`, and (for external providers) `MemorySmithExternalAuthSupport.IsConfiguredExternalProvider` (client ID/secret present). Neither of the two lockout guards added for TSK-0300 uses this — they each hand-roll a partial subset.
- **New method** (extract from existing `IsUsableSignInMethod` logic, don't rewrite from scratch): `Task<IReadOnlyDictionary<string, bool>> SecurityServices.GetEffectiveSignInMethodStatesAsync(CancellationToken)` — returns `{"LocalPassword": bool, "GitHub": bool, "Google": bool, "Microsoft": bool}` reflecting the fully-resolved (DB ∧ config ∧ configured-if-external) state for every method, not just one.
- **Callers to update:**
  - `AdminController.SetProviderEnabled` (currently lines 155-166): replace the hand-rolled `otherProviderEnabled` scan + `auth.LocalPasswordEnabled` check with: compute the hypothetical post-change state (the method being toggled → `request.Enabled`, everything else → current effective state from the new helper), reject if all-false.
  - `AdminSettingsService.TryValidateCrossSettingConstraints`: same pattern — for the settings-JSON save path, resolve what the *hypothetical* post-save config values would be (via the existing `ResolveAuthBool`/`ResolveProviderBool` JSON-tree walk, which stays — that part is legitimately about "what would this save produce," not duplicated security logic), but cross-reference against the **DB `Providers.IsEnabled`** state via the new helper before deciding lockout risk, instead of only checking config-side flags.
- **Test strategy:** this is the highest-value test target in the whole plan — write an integration test that: (a) sets DB `Providers.GitHub.IsEnabled = false` directly, (b) attempts a settings-JSON save that sets `Auth.Providers.GitHub.Enabled = true` and `Auth.LocalPasswordEnabled = false` with no other provider enabled, (c) asserts the save is rejected. This is the exact scenario F11 describes and today, per the audit, would **not** be caught. Use this as the regression test that proves the fix; if it doesn't reproduce the gap on `main` before the fix lands, re-check the guard logic before proceeding.
- **Effort:** 1.5 days (extraction + both call sites + the cross-source integration test). **Risk:** medium — touches live auth-lockout logic; get a second reviewer on this one specifically, and land it behind the existing test suite plus the new integration test before merging.

### 2.2 Harden the OAuth bridge (closes F4)

**Task W1.3 — Add `state` validation and rate limiting to `OAuthBridgeController`**
- **Confirmed during planning (resolves prior open question):** `OAuthBridgeController` does not inject a `client_secret` anywhere — it forwards the caller's POST body verbatim to `https://github.com/login/oauth/access_token`. This is a same-origin CORS-relay for a public/native OAuth client (most likely a companion CLI or editor extension that can't call `github.com` cross-origin directly), not a secret-handling component. **This changes the fix's scope**: no secret-injection work needed; the real gaps are CSRF/state and abuse-throttling, exactly as F4 originally flagged, just with the credential-leak concern now ruled out rather than open.
- **Fix 1 — rate limiting:** add `[EnableRateLimiting("login")]` to both `Authorize()` and `ExchangeCode()`, matching the treatment `AdminController` and `SourceLinksController` already got in commit `6281037`. This is a one-line-per-action change; do it first as a zero-risk immediate mitigation while Fix 2 is designed.
- **Fix 2 — state validation:** since this controller is a pure relay and doesn't originate the `state` parameter itself (the calling client does), the validation has to happen at the boundary: reject `/authorize` requests whose query string lacks a `state` parameter of at least a minimum entropy/length (e.g., ≥16 chars), and reject `/token` exchanges if... — **open design question, see §7** — the bridge is stateless and doesn't currently persist an association between a given `/authorize` call and the later `/token` call, so it cannot itself verify the two round-trip. Two real options:
  - (a) **Minimal fix:** just enforce that `state` is present and non-trivial on `/authorize` (reduces the "no state at all" class of CSRF, but doesn't give the bridge itself round-trip verification — that responsibility stays with whatever calls this bridge, since it's the party that generates and later checks `state`).
  - (b) **Full fix:** have the bridge itself generate and sign a `state` value (via the existing `IDataProtector`, already used for cookie encryption per `MemorySmithSecuritySetup.cs:92`) on `/authorize`, and validate/consume it on `/token`, making the bridge a genuine CSRF-protection boundary rather than a pass-through.
  - **Recommendation:** ship (a) immediately (cheap, non-breaking for whatever client calls this), and open a follow-up task for (b) once the actual calling client (CLI? VS Code extension? — not identified in this repo) is confirmed, since (b) changes the bridge's contract and needs coordination with that caller.
- **Effort:** 0.5 day for Fix 1 + option (a) of Fix 2. **Risk:** low for both — additive checks, no behavior change for well-formed requests.

---

## 3. W2 — Config-Surface Trust Sweep (P1)

### 3.1 Immediate fixes for the two confirmed-dead settings

**Task W2.1 — `MaxNestingDepth`** (extends TSK-0276, doesn't duplicate it)
- Don't implement the enforcement now — that's TSK-0276's job and it's already scoped against `docs/PHASE3.md`. Instead: add a startup-time diagnostic warning (via `OperationalDiagnosticsService.cs` or `LoggingObservabilityService.cs`, both already exist for this purpose) that fires when `MaxNestingDepth != 1` (i.e., someone has actively configured it away from default), logging that the ceiling isn't enforced pending Phase 3. **Effort: 1 hour.**

**Task W2.2 — `MaxParallelOpenAIRequests`**
- Two legitimate paths, pick one after a 10-minute discussion with whoever owns `OpenAICompatibleChatProvider.cs`:
  - (a) **Implement it for real:** wrap outbound calls in a `SemaphoreSlim(_options.MaxParallelOpenAIRequests)`, acquired before each request and released after. Small, contained change to one file.
  - (b) **Remove it:** if there's a reason concurrency limiting was descoped (e.g., the underlying HTTP client already has connection-pool limits that make this redundant), delete the property, its doc comment, and any config references, rather than leaving a promise nobody intends to keep.
- **Recommendation:** (a) — the doc comment's stated rationale ("stay within burst rate limits common on API tiers") is a real, common need for hosted OpenAI-compatible endpoints, and the fix is small. **Effort: 2–3 hours including a test that asserts requests actually serialize under the semaphore.**

**Task W2.3 — `SettingsOverridePath` binding**
- Either: (a) delete the bound property from `MemorySmithOptions` entirely, since every real consumer already reads `IConfiguration["MemorySmith:SettingsOverridePath"]` directly and that's evidently the intended pattern (it has to be readable before options binding fully resolves, at startup, which is likely *why* it's read raw) — document that intentionally in a comment where the constant/key string is defined; or (b) make the raw-config consumers read the bound option instead, if binding order permits. **Recommendation: (a)** — matches the existing pattern and requires zero behavior change, just removing a misleading unused property. **Effort: 1 hour.**

### 3.2 The actual deliverable: finish the sweep properly

**Task W2.4 — Full `MemorySmithOptions` consumer-reachability sweep**
- The audit's scripted check (regex property-name grep across `.cs`/`.razor`/`.json`, manually corrected once for `.razor`-only bindings) is a reasonable starting point but explicitly flagged as non-exhaustive (F22's stated 85% confidence, not higher). Turn it into a real, reviewed deliverable:
  1. Script: for every property on `MemorySmithOptions` and its nested option classes, find at least one consumer outside the declaring file, test files, and doc comments.
  2. Manually triage every "no consumer found" hit — expect some false positives from `IConfiguration`-string-indexed access (as `SettingsOverridePath` was) and reflection-based settings-editor binding (`AdminSettingsService.ListEditableSettings`'s allowlist-descriptor model) — these need source-reading, not just grep, to rule out.
  3. For every genuine hit: wire it up, or delete it, per the same decision framework as W2.1/W2.2 above (staged-but-tracked → warn; dead/aspirational → implement or delete).
- **Effort:** 2 days for the sweep + triage, then roughly 0.5–1 day per additional genuine finding (budget for 2–4 more beyond the two already found, based on the ~85% confidence and the size of the 284-property surface).
- **Ownership note:** this is exactly the kind of task that benefits from being done by someone who didn't write the audit script — fresh eyes on the "no consumer found" list will catch the script's own blind spots faster than re-running the same heuristic.

---

## 4. W3 — Finish TSK-3077: Wire `MemoryIndex` Into Search (P1)

This is the single highest-leverage item in the whole plan — closing it collapses F3 entirely and half of F2.

**Task W3.1 — Fix `MemoryIndex` locking before wiring reads (prerequisite, not optional)**
- Per F2: `MemoryIndex.Add/Remove/Rebuild` are the only lock-protected operations; there are currently zero readers in production, so the missing read-side locking has never been exercised. Wiring live reads onto the current model first would introduce the race, not just leave it latent.
- **Fix:** convert `ById`, `ByTag`, `ByReference` from `Dictionary<string, T>`/`Dictionary<string, HashSet<string>>` to `ConcurrentDictionary<string, T>` / `ConcurrentDictionary<string, ConcurrentBag<string>>`(or a `ConcurrentDictionary<string, HashSet<string>>` with lock-per-key only if you need `HashSet` semantics — evaluate against actual read/write ratio, but `ConcurrentDictionary` is the simpler, safer default given this project's stated preference for eliminating hand-rolled concurrency primitives where a BCL type exists). Remove the public mutable-dictionary exposure entirely — replace `public Dictionary<...> ById { get; }` with a `TryGetById(string id, out MemoryRecord record)` / `GetReferencing(string id)` method-based surface, so callers can't bypass whatever concurrency control is chosen (this also directly fixes the "tests read the raw dictionaries" pattern flagged in F2 — update `MemoryMaintenanceTasksTests.cs` lines 56-58 to use the new accessor methods).
- **Effort:** 1 day including updating the 3 existing test call sites.

**Task W3.2 — Add `ByConflict` to `MemoryIndex`**
- Per F3's implementation note: `ByReference` only indexes `record.References`, not `record.Conflicts`. Add a parallel `ByConflict` dictionary populated the same way in `AddCore`/`RemoveCore`/`Rebuild`, so the index can fully replace both scan sites, not just the references half.
- **Effort:** 0.5 day (mechanical, mirrors existing `ByReference` code).

**Task W3.3 — Replace the two O(N) backlink scans**
- `MemoryApplicationService.EnumerateLinks`'s backlink loop (lines 424-456) and `GetReverseReferencesAsync` (lines 512-529): replace both with lookups against `_index.ByReference`/`_index.ByConflict` (via the new accessor methods from W3.1). This closes TSK-0316 (de-duplicate reverse-reference computation) as a byproduct — verify and close that task explicitly rather than leaving it orphaned.
- **Test strategy:** the existing tests for both methods should pass unchanged if this is a pure implementation swap (same inputs → same outputs, just O(1) instead of O(N)); add one test with a record that has 50+ inbound references to assert the new path doesn't regress to a full scan anywhere, and one test confirming `ByConflict` results now appear where they previously required the separate conflicts-scan branch.
- **Effort:** 1 day.

**Task W3.4 — Wire the index into the actual search query path**
- This is TSK-3077's original, still-open core ask, not something the audits could fully scope from outside (it depends on the current shape of the search/ranking pipeline, e.g. `CodeSearchService.cs`/`SemanticEmbeddingSearchService.cs`, neither of which has been audited line-by-line yet). Recommend: once W3.1–W3.3 land, have whoever owns the search pipeline scope this specific piece — the index now has correct locking and complete reference/conflict data, which were the actual blockers; the search-query integration itself is a separate, more open-ended design task.
- **Effort:** unscoped — recommend a half-day design spike before estimating.

---

## 5. W4 — Scoring & State-Machine Correctness (P2)

**Task W4.1 — Fix `MemoryScorer.Score` reference-count dominance** (closes F10)
```csharp
// Before:
+ 0.15 * record.References.Count
// After:
+ 0.15 * Math.Log10(record.References.Count + 1)
```
- Add a regression test asserting that reference count alone, with `Confidence = 0`, `UsageCount = 0`, and old `LastUpdated`, cannot cross `CoreThreshold` regardless of how large the reference count is (property-based test over `References.Count` from 0 to 1000 is cheap and directly encodes the invariant, not just a single example).
- **Effort:** 2 hours including the test. **Risk:** low, but note this changes scores for any existing heavily-cross-linked record — if this ships to a running instance with real data, expect some records currently at `Core` (promoted via reference-count alone) to demote on the next triage pass. That's the correct outcome, but flag it in the release notes/changelog so it isn't mistaken for a regression.

**Task W4.2 — Fix the Deprecated-recovery dead zone** (closes F1)
- Add the missing reconsolidation branch to `MemoryStateMachine.Evaluate`:
  ```csharp
  else if (original == MemoryStatus.Deprecated && score >= DeprecationThreshold)
      newStatus = MemoryStatus.Unconsolidated;
  ```
  placed after the existing `Deprecated → Working` branch (only fires when that one doesn't, i.e., partial recovery).
- Add a test for the score band `(DeprecationThreshold, WorkingThreshold)` starting from `Deprecated`, asserting the record moves to `Unconsolidated`, not staying `Deprecated` and not jumping to `Working`.
- **Open design question carried from the audit** (see §7): confirm with whoever owns the state-machine design whether `Unconsolidated` (re-enter the front of the pipeline) or a smaller quality bump within `Deprecated` is the intended semantics — the fix above assumes the former, which matches the direction of every other transition in this method (recovery re-enters the evaluation pipeline rather than getting a special-cased partial state).
- **Effort:** 2 hours including the test, pending the design confirmation above.

**Task W4.3 — Clean up the TSK-0364 override pattern** (closes F17)
- Replace the two-block pattern with a single corrected guard, as specified in Delta Report 3:
  ```csharp
  if (allowDeprecation && score < DeprecationThreshold
      && original is not (MemoryStatus.Deprecated or MemoryStatus.Unconsolidated))
  {
      newStatus = MemoryStatus.Deprecated;
  }
  ```
  Delete the now-redundant override block. **Do this in the same PR as W4.2**, since both touch the same method and W4.2 adds a new branch to the same chain — sequencing them separately risks a second round of merge-conflict-driven ordering mistakes in exactly the kind of code this finding is about.
- Add the invariant test suggested in Delta Report 3: one that would fail if someone reordered the branches or converted the method to a `switch` expression without preserving the `Unconsolidated` exclusion — e.g., directly assert `Evaluate(new MemoryRecord { Status = Unconsolidated, ... }, allowDeprecation: true)` with a low score never produces `Deprecated`, independent of *how* that's achieved in the implementation.
- **Effort:** included in W4.2's estimate — same PR, same file, same test suite.

**Suggested PR grouping:** W4.2 + W4.3 together (one file, one method, sequenced correctly) = ~half a day; W4.1 separately (different file, different concern, no reason to couple it) = a couple hours. Both are safe to do in parallel with W1–W3 since they touch neither auth nor indexing code.

---

## 6. W5 — God-Class Decomposition (P2)

This workstream is **pure execution** — TSK-0042 (ChatServices, in progress), TSK-0191 (MemoryApplicationService), TSK-0192 (ChatToolCatalog), TSK-0285 (SecurityServices), and TSK-0043 (MaintenanceAgentServices) are all already correctly scoped. The audits added one new item (`SqliteMemorySmithDatabase`, TSK-3081) with concrete seams, and one re-prioritization input (current sizes).

**Task W5.1 — Decompose `SqliteMemorySmithDatabase`** (F12, extends TSK-3081)
- Stage 1: extract shared connection/command helpers (`OpenSqliteConnectionAsync`, `ExecuteNonQueryAsync`/`ExecuteScalarLongAsync`/`ExecuteScalarStringAsync`, `QueryRowsAsync<T>`, `SqliteDataReader` extensions) into `SqliteConnectionFactory`/`SqliteCommandHelpers`.
- Stage 2: extract `IMemorySmithUserStore` + `IMemorySmithRoleStore` together (they join across each other for `GetRolesForUserAsync`).
- Stage 3: extract `IProviderLinkStore` + `ILoginHistoryStore` together (both auth-adjacent).
- Stage 4: extract `IAuditLogStore`, `ISettingsStore`, `IVersionHistoryStore`, `ISemanticIndexMetadataStore`, `IApiTokenStore` — each independent, parallelizable across separate PRs/engineers once Stage 1 lands.
- Reduce `SqliteMemorySmithDatabase` itself to a thin composition root implementing `IMemorySmithDatabase`, exposing the nine `IXStore` properties backed by real objects instead of `this`.
- **Effort:** 1 day (Stage 1) + 1 day (Stage 2) + 1 day (Stage 3) + 2 days (Stage 4, can be split across engineers) ≈ 5 eng-days total, 3 days wall-clock if Stage 4 is parallelized.
- **Migration/test strategy:** no behavior change intended at any stage — the existing `SqliteMetadataStoreTests.cs` (573 lines) should pass unmodified after each stage if the composition-root properties still resolve to working implementations; treat any test change as a signal the refactor accidentally changed behavior, not as an expected side effect.

**Task W5.2 — Re-sequence the other four decompositions by current size** (F15)
Current line counts, largest first: `MaintenanceAgentServices.cs` (2,187), `ChatToolCatalog.cs` (1,603), `MemoryApplicationService.cs` (1,552), `SecurityServices.cs` (1,224). If `ChatServices.cs` (TSK-0042) is genuinely in progress and near completion, let it finish, then do `MaintenanceAgentServices.cs` (TSK-0043) next — it's now the largest untouched file and has been growing while other decompositions were prioritized ahead of it. **No new task — this is a sequencing recommendation for existing Backlog items.**

---

## 7. W6 — Process Hardening (P3, but cheap and prevents recurrence)

**Task W6.1 — Loosen `SemanticToolQualityTests`'s rank assertions** (closes F18)
- Change the `HybridProbes` assertion model from a fixed top-K rank ceiling (e.g., "must be in top 2") to either: (a) a wider tolerance band (e.g., "must be in top 5"), or (b) a presence-only check (record must appear somewhere in the top-K result set, order unchecked). Recommend (a) as the minimal change — it preserves the test's value as a sanity check on gross relevance regressions while giving normal content-editing enough slack not to trip it.
- Add a short doc comment above `HybridProbes` explaining the tradeoff and pointing future editors at a re-baselining process: if a legitimate content edit shifts a probe's rank outside tolerance, that's a deliberate, reviewed test update in the same PR, not a reason to abandon the content edit.
- Separately: track down and actually complete whatever `project-wiki-source-link-security-boundaries` content update was abandoned in commit `e8a3065` — file a task if the intended change can be reconstructed, since it's currently just gone with no record of what it was meant to say.
- **Effort:** 2 hours for the test change, unscoped for recovering/completing the abandoned doc edit (depends on whether anyone remembers what it was supposed to say).

**Task W6.2 — "No silent skips" norm**
- Lightweight process fix, not code: any commit message containing "Skipped," "deferred," "needs investigation," or similar hedge language about a specific planned change must be accompanied by a same-day TSK task filing (even a one-line Backlog entry is enough — the point is a paper trail, not immediate action). This is exactly what would have prevented F18 from being a "we found it by accident during an audit" finding instead of a tracked, known item.
- **Effort:** zero engineering cost; requires buy-in/habit, not code. Consider adding a pre-commit hook or PR-template checkbox as a light forcing function, mirroring the existing `.githooks/pre-commit` pattern already used for secret-scanning (per TSK-0288's precedent).

**Task W6.3 — Minor cleanup: BOM-prefixed task JSONs** (closes F9)
- Re-save `tsk-0294-*.json` and `tsk-0296-*.json` without a UTF-8 BOM (any editor's "save as UTF-8 without BOM" or a one-line `iconv`/PowerShell fix). Low value on its own; bundle into whatever PR next touches the task-tooling scripts, don't spend a dedicated cycle on it.
- **Effort:** 10 minutes.

**Task W6.4 — Document the `SyncRelationships` read-vs-write persistence asymmetry** (closes F16)
- One-line addition to the XML doc comment on `MemoryApplicationService.SyncRelationships` noting that the legacy→typed-edge migration is in-memory-only on pure read paths and only persists when a write naturally occurs. If a migration-progress metric is ever wanted, note that `RunConsolidationAsync`'s existing sweep is the natural place to force a save for records whose `Relationships` collection was just populated, not just for status-changed records.
- **Effort:** 15 minutes, documentation-only, no behavior change.

---

## 8. Sequencing & Dependency Graph

```
Week 1:  W1.1 (PathSecurity) ──┐
         W1.3 Fix1 (rate limit, trivial) │
         W4.2+W4.3 (state machine)      │  ← all parallelizable, no shared files
         W6.1/6.3/6.4 (process cleanup) │

Week 1-2: W1.2 (SignInMethodResolver) ──requires── W1.1 done first (shares the security folder/patterns, not a hard code dependency, but do it second for review bandwidth)
          W2.1/2.2/2.3 (dead-settings quick fixes) ── independent, parallel with anything

Week 2:   W1.3 Fix2a (state param minimal) ── after W1.3 Fix1
          W4.1 (scorer fix) ── independent
          W2.4 (full options sweep) ── independent, can start anytime, 2-day investment

Week 2-3: W3.1 (MemoryIndex locking) ── prerequisite for W3.2/W3.3
Week 3:   W3.2 (ByConflict) + W3.3 (replace scans) ── after W3.1
Week 3-4: W3.4 (wire into search) ── design spike first, after W3.1-3.3

Week 3-6: W5.1 (Sqlite decomposition, 4 stages) ── independent of everything else, can run
          W5.2 (re-sequence existing backlog) ── ongoing, no blocking dependency
          in parallel on a separate engineer's track
```

**Critical path for closing the P0/P1 material:** W1.1 → W1.2 (with its integration test) is the one sequence I'd personally watch most closely — it's the highest-risk change (live auth-lockout logic) and the one most worth a second reviewer and a deliberate "does the new integration test actually reproduce the gap before the fix, and pass after" verification step before merge.

---

## 9. Assumptions Carried Into This Plan

- All effort estimates assume one mid-to-senior engineer already familiar with this codebase; unfamiliar-engineer ramp-up would roughly double W1 and W5 estimates specifically (auth logic and the god-class both require holding a lot of context).
- W1.3's "who calls the OAuth bridge" question is unresolved — the plan proceeds on the assumption it's an external client (CLI/editor extension) not present in this repo. If it turns out nothing external calls it and it's genuinely dead code, the fix becomes "delete the controller" instead of "harden it" — worth a 15-minute check (search any companion repos/clients) before starting W1.3.
- W4.2's fix (Deprecated → Unconsolidated on partial recovery) is my best-guess interpretation of intended semantics, explicitly flagged as needing confirmation from whoever owns the state-machine design — don't treat the code snippet in §5 as final without that conversation.
- W2.4's effort estimate for "2-4 more genuine findings beyond the two already confirmed" is a guess based on hit-rate in the sample, not a property of the full 284-property surface — treat it as a planning input, not a commitment.
- This plan does not include a line-by-line audit of `CodeSearchService.cs` (3,116 lines) or full reads of `ChatServices.cs`/`ChatToolCatalog.cs` — those remain unaudited beyond grep-level sweeps and their own existing decomposition tasks (W5). If "resolve all issues" is meant to include undiscovered issues in those files, that requires a fifth audit pass scoped specifically to them before a plan can be written for what it finds.

---

## 10. Definition of Done (per workstream)

- **W1:** new integration test (W1.2) reproduces the F11 lockout gap on unpatched code and passes on patched code; `OAuthBridgeController` has rate limiting and minimal state validation; `PathSecurity.IsUnderRoot` has exactly one implementation with two call sites, both existing test suites still green.
- **W2:** every property flagged in W2.4's sweep is either backed by a real consumer, has a startup warning for staged-but-unenforced settings, or has been deleted with a changelog note.
- **W3:** `MemoryIndex`'s three collections are no longer publicly mutable; both O(N) backlink scans are gone; TSK-0316 is explicitly closed; TSK-3077's search-wiring gets a scoped follow-up plan (not necessarily finished, since it depends on the search pipeline's current design).
- **W4:** `MemoryScorer` and `MemoryStateMachine` each have a property-style test encoding the invariant the bug violated, not just an example-based regression test.
- **W5:** `SqliteMemorySmithDatabase` is a thin composition root; the four pre-existing decomposition tasks proceed in size-order with `MaintenanceAgentServices.cs` next after `ChatServices.cs`.
- **W6:** the abandoned doc edit from `e8a3065` is either recovered or explicitly written off with a reason; a "no silent skips" note exists somewhere a future engineer will actually see it (PR template, CONTRIBUTING doc, or pre-commit hook message).
