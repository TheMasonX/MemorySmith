# Admin and Authentication

Visibility: this page is intentionally Admin-only.

The admin and authentication features cover local sign-in, role-based access control, provider management, audit visibility, and operational settings.

## Access Model

```mermaid
flowchart TD
    U[User Request] --> A{Authenticated?}
    A -- No --> V[Viewer baseline access only]
    A -- Yes --> R{Role}
    R -- Viewer --> RV[Read-focused surfaces]
    R -- Editor --> RE[Read + allowed edit workflows]
    R -- Admin --> RA[Admin surfaces + governance + settings]
    RA --> S[/admin/]
    RA --> AU[/api/admin/*/]
    RA --> AD[/api/diagnostics/]
```

> [!NOTE]
> Screenshot placeholder [FEAT-ADMIN-01]: `/admin/setup` first-admin bootstrap page.

## What It Does

- Provides first-admin bootstrap through `/admin/setup`.
- Supports login and profile workflows at `/login` and `/profile`.
- Enforces role-based access for UI, API, and MCP actions.
- Exposes admin controls for users, providers, settings, audit, and history.

## Why It Matters

MemorySmith needs local-first convenience without losing governance. Admin and auth controls protect write paths, diagnostics, and sensitive operations.

## Key Capabilities

- Roles: Viewer, Editor, Admin.
- Local password authentication and provider administration.
- Admin-only views for settings, audit, and change history.
- Compatibility path for first-run local editing before first admin exists.

> [!NOTE]
> Screenshot placeholder [FEAT-ADMIN-02]: `/admin` settings and role-management surface.
> [!NOTE]
> Screenshot placeholder [FEAT-ADMIN-03]: `/login` with provider/local auth options.
> [!NOTE]
> Screenshot placeholder [FEAT-ADMIN-04]: audit/history visibility in admin workflows.

## Related Pages

- [Proposals and Governance](proposals-and-governance.md)
- [Health and Diagnostics](health-and-diagnostics.md)
- [Architecture](../guides/architecture.md)

## Screenshot Backlog Template

- [ ] FEAT-ADMIN-01 admin setup bootstrap
- [ ] FEAT-ADMIN-02 admin settings and role management
- [ ] FEAT-ADMIN-03 login and auth options
- [ ] FEAT-ADMIN-04 audit/history admin view
