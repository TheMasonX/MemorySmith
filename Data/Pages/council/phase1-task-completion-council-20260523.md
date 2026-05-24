# Council Review: Phase 1 Task Completion (Critical/High ROI)

## Decision

Accept Phase 1 as complete for the highest ROI and critical task set in this round: task mutation safety hardening, task read-surface authorization hardening, and task hybrid semantic search parity.

## Evidence Reviewed

- MemorySmith.App/Controllers/TasksController.cs
- MemorySmith.App/Services/TaskDomainService.cs
- MemorySmith.App/Services/MemorySmithOptions.cs
- MemorySmith.App/Services/AdminSettingsService.cs
- MemorySmith.App/appsettings.json
- MemorySmith.Tests/AppApiContractTests.cs
- Data/Tasks/tsk-0054-standardize-task-mutation-error-mapping-for-malformed-record-safety.json
- Data/Tasks/tsk-0055-enforce-task-read-api-view-authorization-and-safe-remote-defaults.json
- Data/Tasks/tsk-0056-add-task-hybrid-semantic-search-default-on-and-admin-toggle.json
- Data/Pages/Tasks/Sprints/security-architecture-convergence-sprint-20260523.md

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Security Reviewer | Keep TSK-0055 in done state and monitor profile combinations where anonymous viewer remains intentionally enabled. | 93% | Viewer-by-config can still make read access broad by policy choice; docs must stay explicit. |
| Reliability Reviewer | Keep TSK-0054 in done state and require the malformed-task mutation test cases to remain in the contract suite. | 95% | Future endpoint additions may regress to uncaught exceptions without shared mapping patterns. |
| Retrieval Reviewer | Accept TSK-0056 as high-ROI parity work; default-on hybrid task search improves query recall without requiring model assets. | 90% | Semantic token scoring is heuristic and should be periodically quality-checked with real queries. |
| Architecture Reviewer | Use this slice as a seam for future TaskDomainService decomposition (TSK-0045) now that behavior contracts are better defined. | 88% | Decomposition can still break behavior if tests are narrowed too aggressively. |
| Synthesizer | Approve completion and proceed to next phase focused on deeper architecture decomposition and guardrail drift checks. | 91% | Need disciplined follow-through on unresolved medium-risk governance items (TSK-0051/0053). |

## Synthesis

- Completed and accepted:
  - TSK-0054: standardized mutation-path argument error mapping to client-safe responses.
  - TSK-0055: enforced task read endpoint view authorization and added unauthenticated rejection coverage.
  - TSK-0056: delivered hybrid lexical+semantic task search with default-on config and admin toggle.
- Immediate next-phase focus:
  - Advance TSK-0045 decomposition under behavior-locking tests.
  - Keep TSK-0053 canonicalization and TSK-0051 drift guardrails as follow-on governance hardening.

## Dissent

- Retrieval Reviewer notes that heuristic semantic ranking should be measured against real task-query samples before treating current ordering quality as final.

## Acceptance Criteria Check

- Non-500 malformed mutation behavior verified in contract tests: met.
- Task read endpoints gated by view policy: met.
- Task hybrid semantic search default-on toggle exists in config and admin surface: met.
- Focused API contract test run passes: met (25/25 in AppApiContractTests).

## Open Questions

- Should task read APIs require stricter-than-view policy in remote-enabled profiles by default?
- Should hybrid task semantic ranking expose score metadata in API responses for diagnostics?
- Should task-search quality have a fixed benchmark query set in CI?
