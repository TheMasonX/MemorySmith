# MemorySmith Audit — Delta Report 2 (Continued Deep Dive)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` · **Commit:** `6281037` (unchanged from prior pass)
**Report generated:** 2026-07-11
**Relationship to prior report:** `memorysmith-sprint60-wavec-audit-26-07-11-08-46-00.md`. This document contains **only new findings and corrections/extensions to existing tasks** discovered in this continuation pass. F1–F9 from the prior report are not repeated. New items are numbered F10+ for a stable cross-reference.

**This pass expanded scope to:** `MemorySmith.Core/StateMachine/MemoryScorer.cs`, `MemorySmith.Storage/SqliteMemorySmithDatabase.cs` (full structural read, migration path, seed/schema SQL), `MemorySmith.App/Services/AdminSettingsService.cs` + `AdminController.cs` (cross-referencing the two self-lockout guards added in the same commit), `MemorySmith.App/Services/SecurityServices.cs` (provider-enablement resolution logic), `MemorySmith.App/Services/MemoryApplicationService.cs` (`SyncRelationships` typed-edge migration), `MemorySmith.App/Services/TaskDomainService.cs` (status/priority modeling), and a sizing sweep of the other known god-service files.

---

## Executive Summary (new items only)

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F10 | `MemoryScorer.Score`: `References.Count` is an unbounded, un-normalized term while every other factor is bounded/damped — **11 references alone (0.15×11=1.65) exceeds `CoreThreshold` (1.62) with zero confidence, usage, or recency** | 90% | Medium-High (feeds every promotion/demotion decision) | **New** — not covered by TSK-3078 (which fixed weight *sum*, not input normalization) |
| F11 | The two auth self-lockout guards added in the **same commit** for TSK-0300 check three only-partially-overlapping sources of truth and can each independently report "safe" while the combined state is a full lockout | 85% | High (security/availability — the exact failure TSK-0300 exists to prevent) | **Correction/extension to TSK-0300** — confirms and sharpens a risk the Wave C handoff doc itself already flagged as unverified |
| F12 | `SqliteMemorySmithDatabase` confirmed as a 1,455-line god class implementing **9** store interfaces via `this` | 95% | Medium (maintainability) | **Extension to TSK-3081** — concrete decomposition seams provided, not previously enumerated |
| F13 | TSK-0373's "Critical" severity is likely overstated: schema DDL and seed SQL are already idempotent (`IF NOT EXISTS` / `INSERT OR IGNORE`), and `PRAGMA busy_timeout` is already applied. The real residual gap is narrower than the task description implies | 75% | Re-scope from Critical → Medium/Low | **Correction to TSK-0373** |
| F14 | `TaskStatuses`/`TaskPriorities` string-constant + `.All`-HashSet pattern is **not** primitive obsession requiring a fix — it's the right call for an admin-configurable task board | 70% | N/A (non-finding) | **Closes a candidate concern** raised by this audit's own brief — documented so it isn't re-flagged later |
| F15 | Fresh size data for the four other known god-service files: `MaintenanceAgentServices.cs` is now the largest at 2,187 lines | 95% (line counts are exact) | Info | **Supporting data for re-prioritizing** TSK-0043/0191/0192/0285 |
| F16 | `SyncRelationships` (legacy `References`/`Conflicts` → typed `Relationships` migration) only persists on write paths; read-only access paths re-derive the same migration in memory on every call without ever writing it back | 80% | Low | **New minor nuance**, no existing task covers this specific mechanic |

---

## F10 — `MemoryScorer.Score` is dominated by an unbounded reference count (Medium-High, 90%)

**File:** `MemorySmith.Core/StateMachine/MemoryScorer.cs` (full file, 14 lines):

```csharp
public static double Score(MemoryRecord record)
{
    var daysSince = (DateTime.UtcNow - record.LastUpdated).TotalDays;
    var recencyFactor = 1.0 / (1 + daysSince);                 // bounded (0, 1]
    var usageFactor = Math.Log10(record.UsageCount + 1);        // damped, grows slowly
    return 0.50 * usageFactor
         + 0.25 * record.Confidence                             // bounded [0, 1] by convention
         + 0.15 * record.References.Count                       // ← raw, unbounded
         + 0.10 * recencyFactor;
}
```

Three of the four inputs are either bounded to roughly `[0, 1]` or logarithmically damped (`usageFactor` needs 100 uses to reach `2.0`, 1000 to reach `3.0`). `record.References.Count` gets none of that treatment — it's a raw integer, and it's the only term with a weight (`0.15`) applied directly to an unbounded count rather than to a bounded proxy for the same signal.

**Concrete consequence, using the current thresholds** (`CoreThreshold = 1.62`, from `MemoryStateMachine.cs`): a record with `Confidence = 0`, `UsageCount = 0`, `LastUpdated` far in the past (recency ≈ 0), and **11 references** scores `0.15 × 11 = 1.65`, which already exceeds `CoreThreshold`. A moderately-cross-linked wiki page — the kind this project's own `Data/Pages/` corpus has plenty of — can reach `Core` status purely by accumulating outgoing references, independent of whether anyone has actually used, trusted, or recently touched it. `ScoringTests.cs`'s `Score_IncreasesWithReferences` test only exercises 2 references, so this dominance behavior has no regression coverage and no one would notice it drifting further as the KB grows and cross-linking increases (which is an explicit stated project goal per `Data/Tasks/tsk-0318`/`tsk-0319`, the typed-edge work — more relationships is the intended direction, which will make this worse, not better, over time).

**Recommendation:** apply the same damping treatment used for `UsageCount` — e.g. `Math.Log10(record.References.Count + 1)` — or cap the raw count before weighting (e.g. `Math.Min(record.References.Count, 10)`). Either way, add a test asserting that reference count alone, with all other factors at zero, cannot cross `CoreThreshold`. This should probably land in the same PR as, or immediately after, whatever addresses TSK-3078's weight rework, since it's the same scoring function and the same class of bug (unexamined magnitude assumptions in the formula).

---

## F11 — Two brand-new self-lockout guards check different, overlapping-but-inconsistent sources of truth (High, 85%)

This directly answers the open risk the Wave C handoff document flagged for itself: *"TSK-0300 requires verifying that `TryValidateCrossSettingConstraints` is actually called on all write paths."* It's not just a call-path gap — the two guards added in commit `6281037` for the same goal use **three different flags** across **two different storage layers**, and neither one checks all three:

**Guard A — `AdminController.SetProviderEnabled`** (lines 155-166):
```csharp
var auth = _options.CurrentValue.Auth;
var providers = await _database.ProviderLinks.ListProvidersAsync(cancellationToken);   // ← DB table
var otherProviderEnabled = providers.Any(p =>
    !string.Equals(p.ProviderName, providerName, ...) && p.IsEnabled);
if (!auth.LocalPasswordEnabled && !otherProviderEnabled)                                // ← config flag
{ return BadRequest(...); }
```
This checks: config `Auth.LocalPasswordEnabled`, plus the **DB `Providers` table's `IsEnabled` column** for every provider *other than* the one being toggled by this call.

**Guard B — `AdminSettingsService.TryValidateCrossSettingConstraints`** (new methods `ResolveAuthBool`/`ResolveProviderBool`, lines 127-134 + additions):
```csharp
var localPasswordEnabled = ResolveAuthBool(root, "LocalPasswordEnabled", _options.CurrentValue.Auth.LocalPasswordEnabled);
var gitHubEnabled = ResolveProviderBool(root, "GitHub", _options.CurrentValue.Auth.Providers.GitHub.Enabled);
var googleEnabled = ResolveProviderBool(root, "Google", _options.CurrentValue.Auth.Providers.Google.Enabled);
var microsoftEnabled = ResolveProviderBool(root, "Microsoft", _options.CurrentValue.Auth.Providers.Microsoft.Enabled);
if (!localPasswordEnabled && !gitHubEnabled && !googleEnabled && !microsoftEnabled)
{ error = "..."; return false; }
```
This checks **only config**: `Auth.LocalPasswordEnabled` and `Auth.Providers.{GitHub,Google,Microsoft}.Enabled`. It never queries `_database.ProviderLinks`.

**Why this matters — confirmed via a third call site.** `SecurityServices.IsUsableSignInMethod` (lines 801-818) is the actual per-user gate used at real sign-in/link-removal time, and it correctly ANDs *both* sources for `LocalPassword`:
```csharp
var providerRecord = providers.FirstOrDefault(...);           // DB Providers.IsEnabled
if (providerRecord?.IsEnabled != true) return false;
...
MemorySmithProviders.LocalPassword => auth.LocalPasswordEnabled && user.LocalPasswordEnabled && ...,
```
For GitHub/Google/Microsoft it further requires `MemorySmithExternalAuthSupport.IsConfiguredExternalProvider(...)`, i.e. a **third** independent condition (whether the provider is actually configured with client ID/secret at runtime) that neither Guard A nor Guard B checks at all.

**The gap this creates:** the DB `Providers` table row and the config `Auth.Providers.{X}.Enabled` flag are two independent booleans that are not the same setting and are not synchronized by anything I found. An admin can:
1. Use the Settings JSON editor to set `Auth.Providers.GitHub.Enabled = true` and `Auth.LocalPasswordEnabled = false` — Guard B sees GitHub "enabled" (config) and allows the save.
2. Separately, the DB `Providers` table's `GitHub` row can still have `IsEnabled = false` (e.g., never toggled on via the provider list UI, or toggled off earlier) — Guard A was never invoked for this change because it only runs inside `SetProviderEnabled`, not inside the settings-JSON save path.
3. Net result: `IsUsableSignInMethod` requires *both* flags true for GitHub and finds them not aligned — no usable sign-in method exists — yet **neither guard blocked the change that created this state**, because each guard only sees its own half of the picture.

**Recommendation:** extract a single `IAuthLockoutGuard` (or a static helper) that takes the *effective, fully-resolved* state of all sign-in methods — DB `IsEnabled` ∧ config `Enabled` ∧ (for external providers) runtime-configured — and call that same helper from both `AdminController.SetProviderEnabled` and `AdminSettingsService.TryValidateCrossSettingConstraints`. This is the same fix in spirit as `SecurityServices.IsUsableSignInMethod`, which already does it correctly for the runtime login path — the settings-save-time guards should delegate to (or mirror) that logic rather than each re-deriving a partial version of it.

---

## F12 — `SqliteMemorySmithDatabase` god-class: concrete decomposition seams (Medium, 95%)

Confirms TSK-3081 (Ready, Large) with specifics the task description doesn't currently enumerate. One 1,455-line `sealed class` implements **nine** store interfaces, all satisfied via `public IXStore X => this;`:

```
IMemorySmithUserStore, IMemorySmithRoleStore, IProviderLinkStore, ILoginHistoryStore,
IAuditLogStore, ISettingsStore, IVersionHistoryStore, ISemanticIndexMetadataStore, IApiTokenStore
```

Each interface's methods are already contiguous in the file (verified via the method list — Users lines 106-234, Roles 236-306, ProviderLinks 308-399, LoginHistory 401-428, AuditLogs 430-483, Settings 486-522, VersionHistory 524-618, SemanticIndexMetadata 620-703, ApiTokens 705-756) — the interfaces already impose a clean seam; nothing needs to be re-designed, only extracted. Shared infrastructure to keep centralized rather than duplicate per split class: `OpenSqliteConnectionAsync`, `ExecuteNonQueryAsync`/`ExecuteScalarLongAsync`/`ExecuteScalarStringAsync`, `QueryRowsAsync<T>`, the `SqliteDataReader` extension methods (lines 1429+), and the migration/init logic (lines 69-101, 769-820) — these belong in a shared `SqliteConnectionFactory`/`SqliteCommandHelpers` internal class that each of the nine split repository classes takes as a constructor dependency, with `SqliteMemorySmithDatabase` itself reduced to a thin composition-root that still implements `IMemorySmithDatabase` and exposes the nine `IXStore` properties, but now backed by real separate objects instead of `this`.

**Suggested sub-task breakdown** (if TSK-3081 wants to be split into stages, per the handoff doc's own suggestion that it "consider splitting into sub-tasks"): Stage 1 — extract shared connection/command helpers; Stage 2 — extract Users+Roles (tightly coupled, `GetRolesForUserAsync` joins across both); Stage 3 — extract ProviderLinks+LoginHistory (both auth-adjacent); Stage 4 — extract AuditLogs+Settings+VersionHistory+SemanticIndexMetadata+ApiTokens (each independent, can be parallelized across PRs).

---

## F13 — TSK-0373 severity is likely overstated relative to actual crash-safety risk (Re-scope, 75%)

TSK-0373 ("Wrap schema migrations and seed operations in a transaction... Add retry/backoff for SQLITE_BUSY," status: **Backlog**, priority: **Critical**) describes two asks. Verified both against the actual code:

1. **Transaction wrapping.** `ApplyPendingMigrationsAsync` (lines 769-820) does run schema DDL, seed SQL, and the tracking-row `INSERT` as three separate auto-committed statements with no explicit `BEGIN TRANSACTION`. **However**, verified: the schema SQL uses `CREATE TABLE IF NOT EXISTS` throughout (checked `InitialSchemaSql`, lines 1213+), and the seed SQL uses `INSERT OR IGNORE` throughout (checked `SeedSql`, lines 1404-1417). This means a crash between the schema step and the tracking-row insert is **self-healing on next startup** — the retry re-runs idempotent DDL/seed statements harmlessly, then successfully records the migration. The actual data-loss/corruption window is much narrower than "no transaction" implies; it's really only an atomicity/cleanliness concern (a future non-idempotent migration in this same list would not get this safety net for free) rather than a live risk with the current single migration.
2. **SQLITE_BUSY retry/backoff.** Verified `_busyTimeoutMilliseconds` is applied via `PRAGMA busy_timeout` on every connection open (line 763, guarded by `if (_busyTimeoutMilliseconds > 0)`). SQLite's native `busy_timeout` already makes the SQLite engine itself wait/retry internally before surfacing `SQLITE_BUSY` — this is a real backoff mechanism, just not an application-level one. Whether *additional* app-level retry is needed on top of this is an empirical question (depends on observed contention under the WAL + this timeout), not a foregone "missing" feature.

**Recommendation:** keep the task, but re-scope: (a) explicit transaction wrapping is good hygiene and should still happen, especially before a second/third migration is ever added to `MigrationsLazy`, but framing it as data-loss-critical for the *current* state of the code overstates the risk — retitle/reprioritize to Medium unless there's contention evidence I don't have visibility into; (b) verify whether `BusyTimeoutSeconds` actually has a non-zero default in `DatabaseOptions` / `appsettings.json` — if it defaults to `0`, the `if (_busyTimeoutMilliseconds > 0)` guard means **no backoff is applied at all** in the out-of-box config, which would resurrect this as a real gap. I did not check the `DatabaseOptions` default in this pass — flagging as the one open sub-question rather than asserting an answer.

---

## F14 — `TaskStatuses`/`TaskPriorities` as string constants: not primitive obsession (non-finding, 70%)

Called out because the request explicitly asked to hunt for primitive obsession, and this pattern (`public const string Ready = "Ready";` + a `HashSet<string> All`) is a textbook shape that *often* is primitive obsession. Assessed it directly: `TaskDomainService.cs` lines 15-38. Verdict: **not a defect**. A task board's status/priority taxonomy is a plausible target for future admin-configurability (custom statuses, custom priority labels) — an audit history and prior task backlog in this same repo (e.g. TSK-0030 "task severity," TSK-0032 "task tags") show a general pattern of wanting flexible, extensible categorization rather than fixed compiler-enforced enums. The `.All` HashSet added by TSK-0295 already gives controlled validation (reject unknown values) without giving up that flexibility. Converting to a C# `enum` would be a net loss of flexibility for a domain concept that's plausibly meant to be admin-editable later. **No action recommended** — documenting this so a future pass doesn't re-flag it as unexamined primitive obsession.

---

## F15 — Fresh size data on the other known god-service files (Info, 95%)

For prioritizing TSK-0042 (ChatServices — in progress this sprint), TSK-0191 (MemoryApplicationService split), TSK-0192 (ChatToolCatalog modularize), TSK-0285 (SecurityServices split), and TSK-0043 (MaintenanceAgentServices decompose):

| File | Lines | Task |
|---|---|---|
| `MaintenanceAgentServices.cs` | **2,187** | TSK-0043 |
| `ChatToolCatalog.cs` | 1,603 | TSK-0192 |
| `MemoryApplicationService.cs` | 1,552 | TSK-0191 |
| `SecurityServices.cs` | 1,224 | TSK-0285 |
| `MemoryGovernanceServices.cs` | 635 | (not currently tracked for decomposition) |

`MaintenanceAgentServices.cs` is now the single largest untracked-for-this-sprint god file in the app layer, larger than `ChatServices.cs` was before this sprint's dead-code deletion. If the team is sequencing these decomposition tasks by size/risk, this is the one to look at next after the current `ChatServices.cs` work (TSK-0042) closes out — no other task currently references it as In Progress.

---

## F16 — Typed-relationship-edge migration doesn't persist on read-only paths (Low, 80%)

`MemoryApplicationService.SyncRelationships` (lines 802-855) is the mechanism migrating legacy `References`/`Conflicts` string arrays into the new typed `Relationships` collection (per TSK-0318/0319's typed-edge initiative) — and it's well-designed: idempotent, deduplicating, and treats `Relationships` as authoritative once populated. It's called from three places: `GetAsync` (line 472), `LoadAllSynced` (line 487), both pure reads, and `NormalizeRecord` (line 781), which runs ahead of writes. Only the third call site's result gets persisted (`_store.Save(record)` follows `NormalizeRecord` on create/update paths). The two read-path calls mutate the returned in-memory `MemoryRecord` object but never write it back to disk.

**Consequence:** a record that is only ever *read* (viewed, searched, included in a context pack) and never explicitly updated will re-run the exact same migration/dedup logic on every single load, forever, without the on-disk file ever actually gaining the migrated `Relationships` array. This is harmless for correctness (the derivation is deterministic and idempotent — same input always produces the same in-memory result) but means: (a) there's a small repeated CPU cost on every read for records that will never naturally get an update, and (b) if anything ever wants to report "how many records have completed the typed-edge migration" by inspecting on-disk state directly (e.g., a maintenance dashboard metric, or the background `RunConsolidationAsync`/`RunTriageAsync` sweep which does call `_store.Save` — but only when `newStatus != original`, not unconditionally), that on-disk number will understate true logical migration progress indefinitely for read-only records.

**Recommendation:** low priority, but worth a one-line comment in `SyncRelationships`'s XML doc noting this explicitly (the current doc comment says "the legacy data is migrated into typed edges" without clarifying that this migration is in-memory-only unless a save happens to occur), so a future engineer building a migration-progress metric doesn't get surprised. If completing the on-disk migration is ever a project goal, `RunConsolidationAsync`'s existing full-table sweep would be the natural place to force a save for every record whose `Relationships` collection was just populated from legacy fields, not just for records whose `Status` changed.

---

## Assumptions Carried Into This Pass

- Same tarball/commit as the prior report (`6281037`); no new commits landed on `dev/sprint-1` between the two passes (re-verified: this pass's file contents match the previous pass's cached copies exactly for every file re-read).
- F13's residual open question (default value of `DatabaseOptions.BusyTimeoutSeconds`) was not resolved — flagged rather than guessed at, per the standing instruction not to take claims at face value without evidence.
- F11's fix recommendation (shared lockout-guard helper) assumes the three-flags-independently-checked behavior is unintentional drift rather than a deliberate design where DB-row and config-row toggles are meant to represent different things (e.g., "installed" vs. "enabled"). I found no doc or comment asserting that distinction anywhere in `SecurityModels.cs`, `AdminSettingsService.cs`, or `MemorySmithOptions.cs`, so I'm treating it as unintentional, but this is worth a 30-second confirmation with whoever designed the original provider-enablement model.
