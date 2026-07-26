# MemorySmith Audit — Unguarded Last-Admin Role Removal (New Finding)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-19
**Method:** full read of `AdminController.cs` (214 lines, never previously examined end-to-end in this engagement), then traced `RemoveRoleAsync`'s storage-layer implementation to confirm no protection exists at either layer, following the same "check every level, don't stop at the first plausible-looking guard" discipline established across this engagement's concurrency and lockout findings (F11, F36, F48, F52).

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F53 | `DELETE /api/admin/users/{userId}/roles/{roleName}` has zero protection against removing the `Admin` role from the last remaining admin account — confirmed unguarded at both the controller and the SQLite storage layer — while the sibling `SetProviderEnabled` endpoint *in the same controller* has an explicit, working self-lockout guard for the analogous "don't disable the last sign-in method" concern | 92% | High (an authorized admin action can deterministically zero out the whole application's admin population, with no recovery path other than the OAuth-bootstrap or local-setup flows this engagement has already found to have their own gaps) | **New** — no existing task covers this specific endpoint |

---

## F53 — `RemoveRole` can silently create a zero-admin application state (High, 92%)

**File:** `MemorySmith.App/Controllers/AdminController.cs`, `RemoveRole`, lines 121-133:
```csharp
[HttpDelete("users/{userId}/roles/{roleName}")]
[Authorize(Policy = MemorySmithPolicies.CanManageUsers)]
public async Task<IActionResult> RemoveRole(string userId, string roleName, CancellationToken cancellationToken)
{
    if (!ValidRoles.Contains(roleName))
    {
        return BadRequest(...);
    }

    await _database.Roles.RemoveRoleAsync(userId, roleName, _currentUser.UserId, cancellationToken);
    await _audit.RecordAsync("role.removed", "User", userId, MemorySmithAuditOutcomes.Success, details: new { roleName }, cancellationToken: cancellationToken);
    return NoContent();
}
```
The only validation is `ValidRoles.Contains(roleName)` — a (correctly implemented, per its own comment referencing a real prior audit finding, "SEC-ROLE-01") allowlist against arbitrary role-string injection. There is **no check of any kind for whether this removal would leave zero users holding the `Admin` role.**

**Confirmed unguarded at the storage layer too**, ruling out the possibility that protection exists one level down: `SqliteMemorySmithDatabase.RemoveRoleAsync` (lines 294-306) is a plain, unconditional SQL `DELETE FROM UserRoles WHERE UserId = @userId AND RoleId IN (...)` — no `COUNT` check, no trigger, no constraint preventing the last admin-role row from being deleted.

**Why this is a real, concrete gap rather than a theoretical one — the direct comparison that makes it obvious:** this exact controller, 30 lines later in the same file, implements precisely this kind of guard for a closely analogous concern:
```csharp
// Auth self-lockout guardrail: prevent disabling the last active sign-in method.
if (!request.Enabled)
{
    var otherProviderEnabled = providers.Any(p => ... && p.IsEnabled);
    if (!auth.LocalPasswordEnabled && !otherProviderEnabled)
    {
        return BadRequest(new { error = "Cannot disable the last remaining sign-in method. ..." });
    }
}
```
The team has clearly already recognized "don't let an admin action leave the application with zero ways in" as an important invariant worth actively defending — they built exactly this kind of check for provider-enablement (albeit with its own gap already flagged in F11, since that guard checks a different/incomplete combination of data sources). But the single most direct way to break this invariant — using the standard, intended role-management API to remove `Admin` from the only account that has it — has no equivalent protection at all, despite sitting in the same file, written with the same care, right next to a guard for a related concern.

**Concrete scenario:** an admin managing users (e.g., cleaning up a departed team member's roles, or reorganizing role assignments across several accounts in sequence) calls `DELETE /api/admin/users/{lastAdminUserId}/roles/Admin` — whether on someone else's account by mistake, on their own account while multitasking across several role changes, or via a scripted/bulk role-cleanup operation that doesn't specifically account for this edge case — and the call succeeds with a plain `204 No Content`. No error, no warning. The application now has zero users with the `Admin` role, and the only paths back to admin access are the OAuth-first-admin-bootstrap flow (previously found to have its own TOCTOU race in F36) or whatever local re-setup/reseed path exists for an already-initialized instance (not independently verified in this pass whether such a path even exists for a database that already has non-admin users — if `NeedsSetupAsync`, called by `SetupStatus`, checks "does any admin exist" the same way `HasAnyAdminAsync` does per F36's investigation, this could plausibly re-open the *original* setup flow, which would at least provide *a* recovery path, though likely not a well-tested one for this specific "already-initialized, now zero-admin" state).

**Recommendation:** add the same shape of guard already proven out for `SetProviderEnabled`, and — per this engagement's standing recommendation from F11's investigation of that guard's own gap — implement it against the fully-resolved admin count rather than a partial view:
```csharp
if (string.Equals(roleName, MemorySmithRoles.Admin, StringComparison.OrdinalIgnoreCase))
{
    var adminCount = await _database.Roles.CountUsersWithRoleAsync(MemorySmithRoles.Admin, cancellationToken);
    var targetHasRole = (await _database.Roles.GetRolesForUserAsync(userId, cancellationToken))
        .Any(r => string.Equals(r.Name, MemorySmithRoles.Admin, StringComparison.OrdinalIgnoreCase));
    if (targetHasRole && adminCount <= 1)
    {
        return BadRequest(new { error = "Cannot remove the Admin role from the last remaining administrator." });
    }
}
```
(exact method name for the count query illustrative — use whatever `IMemorySmithRoleStore` already exposes, or add a small counting method if none exists). **This should be implemented as a check-and-act inside a single transaction or with appropriate locking**, not as a separate check followed by a separate delete with an await gap between them — this engagement has now found the check-then-act race pattern (F36, F48) enough times that any *new* guard written for this codebase should be built correctly the first time rather than needing a follow-up fix for the identical class of race a few sprints later. Add a test: create two admins, remove the role from one (should succeed), attempt to remove it from the last remaining one (should be rejected); separately, a concurrency test with two simultaneous removal attempts against the last two admins, asserting at least one admin remains — mirroring the test rigor already established for F36/F48's fixes.
**Effort:** 2-3 hours including both the sequential and concurrent test cases.
**Confidence (92%):** the gap itself — verified at both the controller and storage layer — is about as directly confirmed as a finding gets in this engagement. The 8% held back accounts for the unconfirmed question of what recovery path (if any) exists for an already-initialized, now-zero-admin instance; if a robust re-bootstrap path does exist and is well-tested, the practical severity would be somewhat lower (still a bug, but a recoverable one) than if no such path exists (in which case this is effectively a self-inflicted, unrecoverable-without-direct-database-access lockout).

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not independently verify what happens when `NeedsSetupAsync`/the setup flow is invoked against a database that has existing non-admin users but zero admins — this is the key unresolved variable in the severity assessment (recoverable bug vs. effectively-permanent lockout) and is worth a direct check before treating this as urgent-and-unrecoverable versus urgent-but-recoverable-via-a-clunky-path.
- This closes out `AdminController.cs` as fully read in this engagement; no further findings beyond F53 were identified in this file — the rest of the controller (settings, audit log, version history endpoints) all correctly delegate to already-reviewed services with appropriate policy-based authorization and no other gaps of this kind found.
