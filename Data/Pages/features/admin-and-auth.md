# Admin and Authentication

The admin and authentication features cover local sign-in, role-based access control, provider management, audit visibility, and operational settings.

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

## Related Pages

- [Proposals and Governance](proposals-and-governance.md)
- [Health and Diagnostics](health-and-diagnostics.md)
- [Architecture](../guides/architecture.md)
