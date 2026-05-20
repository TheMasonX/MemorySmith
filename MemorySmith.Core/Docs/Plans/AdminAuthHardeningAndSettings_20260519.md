# Admin Auth Hardening And Settings Plan

Date: 2026-05-19  
Branch: `feature/admin-auth-hardening-settings`  
Worktree: `../MemorySmith-admin-auth-hardening`  
Confidence: 86%

## Summary

The `/admin` route already declares `CanAdminMemorySmith`, and the admin APIs already use stronger policies. The exposure comes from policy resolution: `AnonymousAccess=Admin`, `AuthenticatedDefaultRole=Admin`, API-key compatibility, and first-admin loopback compatibility can satisfy high-privilege policies without an authenticated Admin principal.

The fix is to make privileged policies require an authenticated user with an explicit `Admin` role claim. Anonymous/local compatibility and the shared API key can continue supporting local read/write compatibility for memory/page work, but they must not authorize admin, user management, settings, audit, or restore workflows.

## Scope

- Harden policy evaluation so `Admin`, `ManageUsers`, `ManageSettings`, `ViewAudit`, and `RestoreHistory` require a signed-in Admin user even when compatibility settings or `Auth:Enabled=false` would otherwise relax authorization.
- Keep `/admin/setup` anonymous but loopback/token constrained for first-admin bootstrap.
- Sanitize config-derived anonymous/default roles so they cannot become `Admin`.
- Add regression tests for the anonymous-admin misconfiguration and role-change endpoint.
- Add a bounded settings editor for non-secret operational settings from `/admin`.
- Persist allowed setting edits to `appsettings.LocalDevelopment.json` in the running app directory and reload configuration.
- Audit setting edits.
- Update docs and project wiki records that mention the old compatibility behavior.

## Non-Goals

- Full OAuth provider implementation beyond the existing GitHub path.
- Per-record ACLs.
- Admin editing of secrets, API keys, DB connection strings, data paths, provider client secrets, or bootstrap token hashes.
- Replacing the existing shared API key with scoped service tokens in this branch.

## Implementation Steps

1. Update `MemorySmithPermissionHandler` so privileged permissions bypass anonymous/default/local/API-key compatibility and require an authenticated `Admin` role.
2. Clamp anonymous access to `Viewer` or no role, and clamp default authenticated role to `Viewer`/`Editor` only.
3. Respect first-admin setup options for loopback bootstrap and optional bootstrap token hash.
4. Validate external auth challenge schemes against configured/registered providers.
5. Add `AdminSettingsService` with an allowlist of editable scalar settings, validation, atomic JSON writes, reload, and audit logging.
6. Add `GET/PUT /api/admin/settings` guarded by `CanManageSettings`.
7. Replace the admin Configuration tab's read-only-only UX with editable rows and a read-only effective configuration table.
8. Remove or neutralize private local overrides that grant anonymous Admin in deployed/runtime directories.
9. Add NUnit regression tests and run focused/full validation.

## Assumptions

- A configured shared API key is still useful for compatibility automation, but it is not a substitute for a human Admin identity.
- `OpenLocalEditorCompatibility` should preserve pre-setup local memory/page workflows only for non-privileged operations.
- `appsettings.LocalDevelopment.json` is the correct local override target because `appsettings.Secrets.json` may contain secrets and should not be edited by the UI.

## Open Questions

- Should future work add scoped service tokens with explicit admin scopes for non-browser automation?
- Should settings edits display a restart-required marker per setting once more services use `IOptionsMonitor` consistently?
- Should first-admin bootstrap require a generated one-time token even on loopback?
