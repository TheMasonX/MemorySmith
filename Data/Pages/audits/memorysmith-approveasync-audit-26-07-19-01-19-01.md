# MemorySmith Audit — `MaintenanceProposalWorkflow.ApproveAsync`: Side Effect Precedes Durable State Transition
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-19
**Method:** began a genuine full-depth read of `MaintenanceAgentServices.cs` (2,187 lines — the largest, most-repeatedly-deferred god-file in the codebase per this engagement's F15, previously only partially read for its write-permission logic). This pass covers `MaintenanceProposalWorkflow` (the human-approval state machine for agent-proposed file changes, lines 672-941) and `FileMaintenanceProposalStore.ApplyAsync` (lines 617-646) in full. The rest of the file — `MaintenanceAgentConfigService`, `MaintenanceResourceProbe`, `MaintenanceDiffService`, `MaintenanceTopicMapService`, and whatever follows line 1116 — remains open scope for a further pass; this file is large enough to need more than one.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F54 | `ApproveAsync` calls `_store.ApplyAsync` (the actual file-write side effect) **before** persisting the proposal's own status change to `Approved`. If the process crashes in the gap between the two calls, the real file change has already happened, but the proposal record is permanently stuck showing its pre-approval status, with no built-in reconciliation path — a confirmed real, if narrow, gap, though a separate content-match guard inside `ApplyAsync` prevents the worse outcome (silent double-application of the same diff) | 88% | Medium (a genuine crash-window durability bug with a confusing, support-burden-shaped failure mode, but not silent data corruption thanks to the idempotency guard already in place) | **New** — no existing task covers this specific ordering gap |

---

## F54 — Apply-then-persist ordering leaves a crash window with no recovery path (Medium, 88%)

**File:** `MemorySmith.App/Services/MaintenanceAgentServices.cs`, `MaintenanceProposalWorkflow.ApproveAsync`, lines 716-725:
```csharp
public async Task<MaintenanceWriteProposal> ApproveAsync(string proposalId, string? comment, CancellationToken cancellationToken)
{
    var proposal = await LoadRequiredAsync(proposalId, cancellationToken);
    EnsureAcceptAllowed(proposal);
    await _store.ApplyAsync(proposal, cancellationToken);                                    // ← the real side effect: writes to the actual target file(s)
    var updated = AppendHistory(proposal, MaintenanceProposalStatuses.Approved, "approve", comment);
    var saved = await _store.SaveAsync(updated, cancellationToken);                          // ← the durable state transition, happens SECOND
    await RecordAuditAsync("maintenance.proposal.approved", saved, cancellationToken);
    return saved;
}
```
The irreversible, externally-visible side effect (actually writing the approved change to whatever real file this maintenance proposal targets — potentially a knowledge-base page, a memory record file, or other project content) happens **before** the proposal's own record is durably updated to reflect that this happened. If the process is killed, crashes, or the container is recycled in the window between these two `await`s — a real, if narrow, window, and one that widens under any host-level instability (OOM kill, deploy rollout, etc.) — the target file now durably contains the approved change, but the `MaintenanceWriteProposal` record on disk still shows its prior status (`Open` or `NeedsRevision`), with no record anywhere that `ApplyAsync` already ran.

**Verified this doesn't lead to silent double-application, thanks to a real safeguard already in the code** — `FileMaintenanceProposalStore.ApplyAsync` (lines 617-646) checks, for every change in the proposal, that the file's *current* on-disk content exactly matches the proposal's recorded `Change.Before` value before writing anything:
```csharp
var current = File.Exists(item.FullPath) ? File.ReadAllText(item.FullPath) : string.Empty;
if (!string.Equals(current, item.Change.Before, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Current file content for '{item.Change.Path}' no longer matches the proposal.");
}
```
This is good, deliberate design — it's a compare-and-swap-style precondition that would correctly catch a second, redundant `ApplyAsync` call against a proposal that already succeeded once (since the file's content is now `Change.After` from the first successful run, which won't match the recorded `Change.Before` on a retry).

**But that same guard is exactly what turns this into a confusing, unrecoverable-without-manual-intervention state rather than a silent-corruption one.** Traced the only real caller (`MaintenanceAgentController.cs:52`, a plain pass-through) and confirmed there is no alternate endpoint, force-flag, or reconciliation path anywhere in this codebase for "this proposal's status is stuck, but the underlying change was actually already applied." Concretely: after the crash-window scenario above, a human reviewer sees the proposal still listed as `Open` (or `NeedsRevision`) in whatever UI lists pending proposals, and — reasonably, since nothing tells them otherwise — clicks Approve again. `EnsureAcceptAllowed` passes (the status is still whatever it was), `ApplyAsync` runs again, and the content-match guard above **correctly** throws, because the file no longer matches `Change.Before`. The reviewer now sees an opaque `InvalidOperationException` about content not matching a proposal they never touched, with no way to know the real cause (a prior crash mid-approval) and no built-in action to take other than manual file/database surgery to force the proposal's status to `Approved` directly.

**Recommendation:** invert the order so the durable state transition is recorded *before* the side effect, or — more robustly — make the two steps atomic with respect to crash recovery. Two reasonable approaches:
1. **Simplest, matches this codebase's existing pattern of optimistic-then-verify:** persist an intermediate `Applying`/`InProgress` status (or a dedicated `AppliedAtUtc` timestamp field set) immediately before calling `ApplyAsync`, then persist the final `Approved` status after. On restart/next load, a proposal found in the intermediate state can be resolved deterministically: re-check the target file's current content against `Change.After` (not `Before`) — if it already matches `After`, the apply demonstrably already succeeded and the status can be safely advanced to `Approved` without re-running `ApplyAsync`; if it still matches `Before`, the apply never ran and it's safe to retry from scratch.
2. **Alternative:** swap the call order outright — persist `Approved` status first, then call `ApplyAsync`, and if `ApplyAsync` throws, catch it and roll the status back to a distinguishable `ApprovalFailed` state (rather than leaving it silently `Approved`-but-not-actually-applied, which would be a worse failure mode than the current one). This trades "stuck-open-but-actually-applied" for "shows-approved-but-actually-failed," which is arguably an easier state to reason about and recover from in a UI (a clearly-failed status inviting a retry, versus an open status silently hiding a completed action) — but requires the rollback-on-failure path to be implemented correctly, which is its own small piece of work.
Either way, **add a specific test**: simulate a crash between `ApplyAsync` and `SaveAsync` (call `ApplyAsync` directly against the store, confirm the file changed, then attempt `ApproveAsync` again on the still-`Open` proposal and assert the system detects the already-applied state and resolves it cleanly rather than surfacing the current opaque content-mismatch exception to the caller).
**Effort:** half a day including the reconciliation logic and the crash-simulation test — this is a good candidate for the same "test proves the fix closes the gap" discipline already applied to this engagement's other concurrency/durability findings (F36, F48, F52).
**Confidence (88%):** the ordering and its consequence are directly read from the code, and the "no recovery path exists" claim is confirmed by tracing the only real caller and finding no alternate endpoint. The 12% held back is for not having empirically reproduced an actual process-crash mid-approval against a running instance (not achievable in this sandbox) — the logical gap is solid, but I haven't watched it happen.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- This finding covers only the two methods read in full so far (`MaintenanceProposalWorkflow`, `FileMaintenanceProposalStore.ApplyAsync`) out of `MaintenanceAgentServices.cs`'s 2,187 lines — `MaintenanceAgentConfigService`, `MaintenanceResourceProbe`, `MaintenanceDiffService`, `MaintenanceTopicMapService`, and the file's remaining ~1,000 lines beyond line 1116 are unread and represent substantial open scope for a follow-up pass on this specific file, independent of the rest of the codebase.
- The severity rating (Medium, not High) rests on the confirmed presence of the content-match guard inside `ApplyAsync` — if that guard were ever weakened or removed in a future change, this same ordering issue would become a silent-double-application risk instead of a confusing-but-safe stuck-state risk, which would raise the severity substantially. Worth re-checking this finding's severity if `ApplyAsync`'s precondition check is ever modified.
