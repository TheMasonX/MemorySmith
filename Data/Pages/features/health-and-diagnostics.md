# Health and Diagnostics

The health and diagnostics feature set gives runtime visibility into readiness, activity, maintenance state, and operational configuration.

## What It Does

- Serves a health dashboard at `/health`.
- Provides readiness and diagnostics APIs for local operations.
- Reports search and maintenance telemetry for troubleshooting.
- Surfaces operational state for audit and support workflows.

## Why It Matters

Local-first systems still need clear observability. Health and diagnostics help confirm that storage paths, search providers, and background jobs are working as expected.

## Key Capabilities

- Runtime status cards and activity charting.
- Diagnostic endpoint coverage for local triage.
- Visibility into indexing and maintenance behavior.
- Role-gated access to sensitive operational data.

## Related Pages

- [Admin and Authentication](admin-and-auth.md)
- [Search System](search-system.md)
- [Architecture](../guides/architecture.md)
