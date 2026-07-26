# Council Review: Swarm Audit Synthesis — 3-seat Council

## Decision
Run a focused remediation sprint: fix high-risk fail-open security defaults, provider normalization, and unbounded-diff crash risks before broad refactor/integration work.

## Evidence Reviewed
- Data/Pages/audits/swarm-audit-synthesis-26-07-26.md
- Data/Pages/audits/memorysmith-isloopback-audit-26-07-16-20-02-55.md
- File excerpts: MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs, MemorySmith.App/Services/MaintenanceAgentServices.cs (diff service references via tasks), MemoryIndex task (TSK-0397)

## Findings
| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Prioritize provider normalization and DB correction; produce migration report before mass updates | 88% | Existing corrupt provider rows may cause runtime failures if blindly changed |
| Data Model Architect | Centralize provider normalization; add DB-level constraints and validation in save paths | 85% | Migration complexity with existing data; need fallback mapping for legacy entries |
| Skeptical Reviewer | Flip IsLoopback default to fail-closed and add tests; do not change behavior silently in prod without a rollout plan | 80% | Potential false-positives blocking legitimate local-editor modes in rare hosting setups |

## Synthesis
- Immediate changes (apply in current sprint):
  - Flip `IsLoopback` default to fail-closed and add unit+integration tests (TSK-0398, TSK-0418)
  - Fix provider normalization and add DB validation plus a report-only migration (TSK-0395, TSK-0400)
  - Add size guard and iterative backtrack for Maintenance diffs (TSK-0396)
  - Harden RequestMetadata HMAC handling to avoid silent regeneration (TSK-0405)
- Defer: large decomposition tasks (ChatToolCatalog, McpController) to next sprint after these high-risk fixes.

## Dissent
- Human Learning Advocate would prefer immediate staged rollout with feature flags for `IsLoopback` behavior; council majority accepts direct flip with a verification window and smoke checks (TSK-0418) as sufficient mitigation.

## Acceptance Criteria
- Tests added and passing for `IsLoopback` behavior.
- Provider normalization centralized and validated; migration report produced and dry-run verified.
- Maintenance diff unbounded input gracefully fails; iterative backtrack implemented.

## Open Questions
- Are there any supported deployment modes that expect `RemoteIpAddress` to be null while still being legitimately local? Who owns deployment docs?
- Do we have a preferred migration mapping for legacy provider strings that may be corrupt?