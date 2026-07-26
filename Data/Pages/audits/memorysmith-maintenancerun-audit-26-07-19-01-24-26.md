# MemorySmith Audit — `MaintenanceAgentService.RunAsync` Has No Run-Exclusivity Guard
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-19
**Method:** continued the deep read of `MaintenanceAgentServices.cs` from the prior report. `MaintenanceActiveRunStore` (lines 37-71, read in the prior pass but not fully traced to its consumers at the time) looked, from its name and shape, like it might be a concurrency guard — tracing every real caller across the codebase is what confirmed it isn't one, and that nothing else fills that role either.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F55 | `MaintenanceAgentService.RunAsync` — the single entry point behind `RunMaintenanceNowAsync`/`_WeeklyAsync`/`_OnDemandAsync` — has **no check anywhere** for whether a maintenance run is already in progress before starting a new one. `MaintenanceActiveRunStore.Begin` is a pure bookkeeping call (unconditionally overwrites its one tracked slot) with zero enforcement power, and no lock/semaphore/attribute exists anywhere else in the call chain either. Two overlapping runs — entirely plausible given there's both an on-demand HTTP endpoint and an independent scheduled background trigger hitting the same unguarded method — would both execute the full pipeline (resource probe, topic-map build, proposal submission) concurrently, and the "is a run currently active" status the store exists to expose can end up flatly wrong as a direct consequence | 90% | Medium-High (no data corruption confirmed, but real double-work, potential duplicate/racy proposal writes, and a status API that can actively mislead an operator about system state) | **New** — TSK-0043 tracks decomposing this file generally; nothing tracks this specific concurrency gap |

---

## F55 — No mutual exclusion between overlapping maintenance runs (Medium-High, 90%)

**Traced the full call chain, `MaintenanceAgentServices.cs`:**
```csharp
// line 1475
private async Task<MaintenanceRunResult> RunAsync(string trigger, CancellationToken cancellationToken, string? taskFilter = null)
{
    var started = DateTimeOffset.UtcNow;
    var activeRun = _activeRuns.Begin(trigger, taskFilter, started);   // ← unconditional; never checks GetCurrent() first
    ...
    try { /* resource probe, topic-map build, per-task proposal submission, optional LLM review */ }
    finally { _activeRuns.End(activeRun.RunId); }
}
```
And `MaintenanceActiveRunStore.Begin` (lines 50-59) itself:
```csharp
public MaintenanceActiveRunSnapshot Begin(string trigger, string? task, DateTimeOffset startedAtUtc)
{
    var snapshot = new MaintenanceActiveRunSnapshot(Guid.NewGuid().ToString("N"), trigger, task, startedAtUtc);
    lock (_lock) { _current = snapshot; }   // ← unconditional overwrite, no check of the existing value first
    return snapshot;
}
```
The `lock` here only protects the assignment itself from a data race on the field — it does **not** implement "refuse to begin if `_current` is already non-null." Confirmed via a full repo-wide trace that nothing else fills that role either:
- **The two real entry points into `RunAsync` are entirely independent of each other.** `MaintenanceAgentController.cs` exposes `run_maintenance_now` and `run_maintenance_on_demand` as plain, unguarded `[HttpPost]` actions (only gated by the `CanApproveAgentWrites` authorization policy — no rate limit, no de-duplication attribute). Separately, a background hosted service (confirmed at `MaintenanceAgentServices.cs:2106`) independently calls `RunMaintenanceWeeklyAsync` on its own schedule. **These two triggers share no coordination whatsoever** — an admin clicking "Run Maintenance Now" at the same moment the weekly scheduled job fires is a realistic, not contrived, overlap scenario, not just a double-click edge case.
- No semaphore, no `SemaphoreSlim(1,1)`, no distributed lock, no early-return-if-busy check exists anywhere in `MaintenanceAgentService`'s constructor or `RunAsync` itself.

**Confirmed real consequence beyond "wasted duplicate work":** `MaintenanceActiveRunStore` is a single-slot store (`_current`, one field) — it cannot represent "two runs are active" as a concept at all. Trace the interleaving: Run A begins (`_current = SnapshotA`), Run B begins before A finishes (`_current = SnapshotB`, silently overwriting A's snapshot with no error). When Run A finishes and calls `End(SnapshotA.RunId)`, the equality check (`_current?.RunId == runId`) fails — since `_current` now holds `SnapshotB` — so `End` for Run A is silently a no-op, and `_current` still correctly shows `SnapshotB` (so far, harmless by coincidence). But when Run B finishes and calls `End(SnapshotB.RunId)`, that succeeds and clears `_current` to `null` — and if Run A happens to still be executing at that point (a very plausible ordering, since two independently-timed triggers have no reason to finish in start-order), **the public `GetActiveRun()` status now reports "no run active" while a maintenance run is, in fact, still actively writing files.** Any admin dashboard or monitoring built on `GetActiveRun()` (its entire evident purpose) would be actively wrong at exactly the moment it matters most.

**Confirmed real consequence beyond the status display:** both overlapping runs proceed through the *entire* pipeline independently and concurrently — including `SubmitOutputProposalsAsync` (which creates and saves proposal files via `FileMaintenanceProposalStore`, examined in the prior report) and `MaintenanceTopicMapService`'s cache-writing path (`SaveCacheAsync`, writing to a single shared `TopicMapCachePath` file — confirmed same path for both runs via `_config.GetCurrent().Storage.TopicMapCachePath`). Two runs racing to read-then-rebuild-then-write the same topic-map cache file, or two runs each independently detecting and proposing a fix for the same underlying maintenance finding (since neither run has any visibility into what the other is doing), are both realistic outcomes — the first is a plain file-write race (last-writer-wins, at best just wasted work; at worst a torn/incomplete write if the two writes interleave at the OS level), and the second is a duplicate-proposal nuisance an approving human would have to notice and reconcile manually.

**Recommendation:** make `Begin` actually enforce exclusivity rather than just recording the latest snapshot:
```csharp
public MaintenanceActiveRunSnapshot? TryBegin(string trigger, string? task, DateTimeOffset startedAtUtc)
{
    lock (_lock)
    {
        if (_current is not null)
        {
            return null;   // a run is already active; caller must handle this
        }
        var snapshot = new MaintenanceActiveRunSnapshot(Guid.NewGuid().ToString("N"), trigger, task, startedAtUtc);
        _current = snapshot;
        return snapshot;
    }
}
```
and have `RunAsync` check for `null` and return a `Skipped: true` result (mirroring the existing pattern already used for the resource-busy skip case just a few lines below it — `RunAsync` already knows how to produce a `Skipped` result cleanly, so this fix reuses an existing shape rather than inventing a new one) instead of proceeding. This closes the concurrency gap and, as a direct side effect, makes `GetActiveRun()`'s status accurate again (there's now only ever one run in flight, full stop, so the single-slot store's own implicit assumption becomes actually true instead of just usually true by luck).
**Effort:** 1-2 hours for the fix; add a concurrency test — call `RunAsync` (or `TryBegin` directly) twice without awaiting the first, assert the second returns `null`/a `Skipped` result rather than proceeding — mirroring this engagement's established practice of treating the concurrency test as the actual proof a fix works, not the code change alone.
**Confidence (90%):** the absence of any guard is confirmed via exhaustive repo-wide trace of every real caller and every layer between the two HTTP endpoints/background trigger and the unconditional `Begin` call — this is about as directly verifiable as a concurrency finding gets without actually running the app. The 10% held back is the standing caveat applied to every concurrency finding in this engagement: I reasoned through the interleaving by reading the code, but could not empirically fire two real overlapping requests against a running instance to watch it happen.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not verify how severe the topic-map-cache race's worst case actually is at the filesystem level (a fully torn/corrupted write vs. a clean last-writer-wins overwrite) — `File.WriteAllText` is not atomic against concurrent writers, but the practical outcome depends on OS/filesystem specifics outside what a static read of this code can determine with certainty; flagged as a plausible consequence, not a proven one.
- This continues, rather than completes, the read of `MaintenanceAgentServices.cs` — `MaintenanceAgentConfigService`, `MaintenanceResourceProbe`, `MaintenanceDiffService`, `MaintenanceTopicMapService`'s full body, `SubmitOutputProposalsAsync`, `TryRunLlmReviewAsync`, and the remainder of `MaintenanceAgentService`'s own methods (roughly lines 1420-2158, minus what was read for this specific trace) remain open scope for a further pass.
