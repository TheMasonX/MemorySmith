# Codebase Audit Task Vetting - 2026-05-23

## Scope

- Included: active task backlog under `Data/Tasks`, task system usage in `/tasks`, chat-governance work items, admin/config hardening tasks, and quality gaps around browser smoke and live wiki validation.
- Excluded: broad feature ideation beyond the current imported backlog and user-authored tasks from `Themasonx` unless needed for context.
- Timebox: focused medium audit.

## Evidence Reviewed

- `README.md`
- `MemorySmith.App/Services/TaskDomainService.cs`
- `MemorySmith.App/Controllers/TasksController.cs`
- `MemorySmith.App/Services/AdminSettingsService.cs`
- `MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs`
- `MemorySmith.App/Services/OperationalDiagnosticsService.cs`
- `MemorySmith.App/Components/Pages/Chat.razor`
- `MemorySmith.Tests/AppApiContractTests.cs`
- `MemorySmith.Tests/SecurityAndSourceLinkTests.cs`
- `logs/agent-smith-20260523-chat-agent-testing-tracker.md`
- `logs/agent-smith-20260523-mcp-tooling-audit.md`

## Findings

| ID | Domain | Severity | Confidence | Summary | Evidence |
| --- | --- | --- | ---: | --- | --- |
| F-001 | Backlog governance | Medium | 94% | The non-`Themasonx` backlog is entirely the imported `tracker-import` set, and many records still carry placeholder descriptions rather than sprint-ready acceptance criteria. | `Data/Tasks/*.json`, `README.md`, `TaskDomainService.cs` |
| F-002 | Backlog accuracy | Medium | 96% | `TSK-0009` is stale: editable admin settings are already implemented and documented. | `README.md`, `AdminSettingsService.cs`, `project-wiki-admin-auth-hardening` |
| F-003 | Quality gates | Medium | 88% | Core routes are documented and tested at API/component levels, but no dedicated browser smoke workflow was found for the main workbench routes. | `README.md`, `MemorySmith.Tests/*`, imported `TSK-0002` |
| F-004 | Data validation | High | 86% | The live `Data/Memories` corpus is used as product content and test fixture input, but no dedicated whole-wiki validation task or command was found. | `README.md`, `ProjectWikiTestbaseTests.cs`, imported `TSK-0003` |
| F-005 | Chat governance | High | 91% | Proposal approval for safe page writes can still fail because chat approvals are effectively coupled to maintenance write-root rules. | `logs/agent-smith-20260523-mcp-tooling-audit.md`, imported `TSK-0016` |
| F-006 | Chat state integrity | High | 90% | Recent testing still reports stale pending counts and badges after mixed approval outcomes, even after batch-approval improvements. | `logs/agent-smith-20260523-chat-agent-testing-tracker.md`, imported `TSK-0017`, `TSK-0018` |
| F-007 | Remote hardening | High | 89% | Remote API exposure is guarded and diagnosed, but current behavior remains warning-first rather than enforced hardening when `AllowRemoteApi=true` without an API key. | `MemorySmithRequestGuardMiddleware.cs`, `OperationalDiagnosticsService.cs`, `SecurityAndSourceLinkTests.cs`, imported `TSK-0023` |
| F-008 | Task-system governance gap | Medium | 84% | The task system preserves provenance through `reporter`, but there is no explicit review-state workflow for imported tasks, making backlog trust maintenance too manual. | `TaskDomainService.cs`, `Data/Tasks/*.json` |

## Task Actions

- Archived `TSK-0009` as obsolete because the admin settings-editing gap is already closed.
- Rewrote `TSK-0002`, `TSK-0003`, `TSK-0016`, `TSK-0017`, `TSK-0018`, and `TSK-0023` into evidence-backed task records with concrete acceptance criteria and validation notes.
- Added `TSK-0029` to track imported-task provenance and review-state governance inside `/tasks`.

## Sprint Plan - Stability First Backlog Triage

### Sprint Objective

Reduce delivery risk on active governance surfaces before taking additional net-new feature work.

### Capacity Assumptions

- Team size: 1 primary maintainer plus agent assistance.
- Effective days: 4 to 5 focused days.
- Risk buffer: 25% for chat-governance repro and validation churn.

### Committed Items

- `TSK-0016` Fix safe chat page approvals and tighten proposal-time path validation.
- `TSK-0017` Reconcile pending-write counters and badges after mixed outcomes.
- `TSK-0003` Add live wiki validation for `Data/Memories`.
- `TSK-0002` Add browser smoke coverage for core workbench routes.
- `TSK-0029` Add review metadata and triage workflow for imported tasks.

### Stretch Items

- `TSK-0018` Finish deterministic post-batch reconciliation and close remaining gaps.
- `TSK-0023` Harden remote-enable workflows beyond warning-only diagnostics.

### Exit Criteria

- Safe chat page proposals can be approved reliably without maintenance-root false negatives.
- Pending-write state is deterministic across mixed outcomes and history reload.
- A dedicated live-wiki validator and route smoke workflow both exist and are runnable.
- Imported tasks can be triaged with an explicit review outcome rather than freeform conventions only.

### Demo Targets

- Show a mixed chat proposal batch resolving with accurate post-action state.
- Show a failing and passing run of the wiki validator.
- Show the route smoke workflow producing artifacts.
- Show one imported task moving through the new review workflow.

## Risk Register

- R-001: Chat-governance fixes may expose a deeper server-side pending-state model issue. Mitigation: keep `TSK-0017` and `TSK-0018` separate enough to validate incrementally.
- R-002: Browser smoke coverage can become flaky if it depends on unstable local timing. Mitigation: keep the smoke suite small and deterministic.
- R-003: A review-metadata feature can overcomplicate the task model if it jumps straight to a large schema change. Mitigation: start with the minimum queryable provenance/review contract.

## Open Questions

- Should imported-task review state be a first-class field, a label convention, or a hybrid with dedicated UI affordances?
- Should remote hardening become startup-blocking when `AllowRemoteApi=true` without an API key, or remain a strong readiness/admin gate?
- What is the canonical source of truth for pending-write state: turn-local UI state, trace events, or a server-backed pending store?

## Confidence

- Overall audit confidence: 89%
- Highest-confidence finding: imported backlog provenance and stale-task identification.
- Lowest-confidence finding: the exact implementation shape required for durable pending-write reconciliation.
