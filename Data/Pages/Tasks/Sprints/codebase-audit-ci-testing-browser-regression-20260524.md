# Comprehensive Codebase Audit Report

## Scope
- Included:
  - CI and CD workflows in `.github/workflows`.
  - Test strategy and execution paths in `MemorySmith.Tests` and `e2e`.
  - Browser-regression safeguards related to navigation lockups/freezes.
- Excluded:
  - Deep feature-level audits outside testing and delivery hardening.
  - Broad performance tuning not tied to test reliability.
- Timebox:
  - Deep-dive planning pass on 2026-05-24.

## Evidence Reviewed
- [README.md](../../../../README.md#L403)
- [.github/workflows/ci.yml](.github/workflows/ci.yml#L1)
- [.github/workflows/docs-pages.yml](.github/workflows/docs-pages.yml#L1)
- [e2e/playwright.config.ts](e2e/playwright.config.ts#L1)
- [e2e/tests/navigation-freeze.spec.ts](e2e/tests/navigation-freeze.spec.ts#L1)
- [e2e/package.json](e2e/package.json#L1)
- [MemorySmith.App/Components/SafeJsInterop.cs](MemorySmith.App/Components/SafeJsInterop.cs#L1)
- [MemorySmith.Tests/SafeJsInteropTests.cs](MemorySmith.Tests/SafeJsInteropTests.cs#L1)
- [MemorySmith.App/Components/Layout/MainLayout.razor](MemorySmith.App/Components/Layout/MainLayout.razor#L1)
- [MemorySmith.App/wwwroot/app.css](MemorySmith.App/wwwroot/app.css#L69)
- [Data/Tasks/tsk-0002-add-browser-level-smoke-coverage-for-the-main-app-routes-memories-pages-chat-and-health.json](Data/Tasks/tsk-0002-add-browser-level-smoke-coverage-for-the-main-app-routes-memories-pages-chat-and-health.json#L1)
- [Data/Tasks/tsk-0065-pages-lock-when-navigating-to-new-page.json](Data/Tasks/tsk-0065-pages-lock-when-navigating-to-new-page.json#L1)

## Findings

| ID | Domain | Severity | Confidence | Summary | Evidence |
|---|---|---|---:|---|---|
| F-001 | CI/CD | High | 95% | CI does not execute browser e2e regression tests, so navigation-freeze regressions can merge undetected. | [ci.yml](.github/workflows/ci.yml#L1), [navigation-freeze.spec.ts](e2e/tests/navigation-freeze.spec.ts#L1) |
| F-002 | Testing | High | 90% | A targeted Playwright regression test exists locally, but it is not a required quality gate for PRs. | [e2e/package.json](e2e/package.json#L1), [README.md](../../../../README.md#L413), [ci.yml](.github/workflows/ci.yml#L1) |
| F-003 | Testing Strategy | Medium | 84% | Browser smoke coverage across critical routes is still backlog-only, despite previous task tracking. | [tsk-0002](Data/Tasks/tsk-0002-add-browser-level-smoke-coverage-for-the-main-app-routes-memories-pages-chat-and-health.json#L1) |
| F-004 | Regression Guardrails | Medium | 82% | Recent UI lockup incident tracking exists, but prevention is fragmented across task backlog and local-only scripts. | [tsk-0065](Data/Tasks/tsk-0065-pages-lock-when-navigating-to-new-page.json#L1), [navigation-freeze.spec.ts](e2e/tests/navigation-freeze.spec.ts#L1) |
| F-005 | Docs/Delivery Drift | Medium | 78% | Validation documentation and workflow narratives are broader than enforced checks, creating false confidence in browser guardrails. | [README.md](../../../../README.md#L466), [.github/workflows/ci.yml](.github/workflows/ci.yml#L1) |
| F-006 | Component-Level Safety | Low | 80% | JS interop resilience has unit tests and defensive helper patterns, but these do not replace end-to-end navigation/circuit verification. | [SafeJsInterop.cs](MemorySmith.App/Components/SafeJsInterop.cs#L1), [SafeJsInteropTests.cs](MemorySmith.Tests/SafeJsInteropTests.cs#L1) |

## High-Impact Council Review

### Decision
Adopt browser regression protection as a blocking CI gate on pull requests, starting with navigation-freeze route-hop coverage and then broadening to route smoke artifacts.

### Seats

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Promote existing local Playwright navigation-freeze test into CI with artifact retention and deterministic naming. | 93% | None if workflow remains optional for local dev and required for PR. |
| Data Model Architect | Keep task records as primary planning surface; encode audit-to-task traceability fields in descriptions/comments. | 86% | Avoid fragmentation between sprint pages and task records. |
| Retrieval Specialist | Treat browser-lockup regressions as trust and retrieval-access blockers because route failures prevent access to wiki and tools. | 82% | Need explicit rerun guidance for flaky external dependencies. |
| Skeptical Reviewer | Require fast-fail sanity checks and split jobs so browser failures are visible without hiding .NET test signals. | 88% | Flakiness and runtime inflation if environment setup is unstable. |
| Synthesizer | Ship a staged gate: (1) navigation-freeze spec required, (2) route smoke matrix + artifacts required, (3) docs + branch protection alignment. | 90% | Must define flaky-test policy and quarantine protocol before broad suite expansion. |

### Dissent
- Skeptical Reviewer argues full route smoke should not become required in one step; start with a single deterministic spec and expand after two green weeks.

### Acceptance Criteria
- Pull requests cannot merge when the navigation-freeze Playwright spec fails.
- CI publishes Playwright traces/screenshots/videos for browser failures.
- Route smoke coverage for `/memories`, `/pages`, `/chat`, `/tasks`, `/health` is added with per-route failure attribution.
- Sprint and task documents maintain one-to-one traceability to findings F-001 through F-005.

## Risk Register
- R-001: Browser e2e flakiness creates noisy failures.
  - Impact: High.
  - Likelihood: Medium.
  - Mitigation: deterministic selectors, retries in CI only, artifact-first triage runbook.
- R-002: CI duration increase reduces developer throughput.
  - Impact: Medium.
  - Likelihood: Medium.
  - Mitigation: parallel job split, cache dependencies, keep blocking suite minimal and fast.
- R-003: Quality gate drift (docs say covered but pipelines skip checks).
  - Impact: Medium.
  - Likelihood: High.
  - Mitigation: add CI enforcement matrix in README and task-linked audit cadence.

## Open Questions
- Q-001: Should browser e2e run on every PR or only when UI-affecting paths change?
  - Owner: Maintainer.
  - Due gate: Sprint 1 planning lock.
- Q-002: Which artifacts are mandatory for regressions (trace, screenshot, video) to balance storage versus diagnostics?
  - Owner: Maintainer.
  - Due gate: Sprint 1 implementation.
- Q-003: Should a quarantine lane exist for known flaky browser tests, and how long can tests remain quarantined?
  - Owner: Maintainer.
  - Due gate: Sprint 2 quality governance.

## Prioritized Backlog
1. TSK-0067 Add required PR Playwright navigation-freeze gate.
2. TSK-0068 Add browser smoke matrix and artifact publication.
3. TSK-0069 Add CI test topology split and branch quality-gate contract.
4. TSK-0070 Expand route interaction regressions and freeze diagnostics.
5. TSK-0071 Align docs and validation runbooks with enforced CI checks.

## Assumptions
- Planning mode only: execution of full CI changes is deferred to sprint implementation tasks.
- Existing `.NET` test baseline remains stable while browser gate is introduced.
- The recent navigation lockup concern is representative of broader browser-regression risk.

## Confidence
- Audit confidence: 88%
- Sprint planning confidence: 84%
- Delivery confidence (if committed and sequenced as planned): 79%
