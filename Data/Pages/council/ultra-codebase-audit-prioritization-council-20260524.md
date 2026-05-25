# Council Review: Ultra Codebase Audit Prioritization

## Decision
Prioritize stabilization, governance, and validation gates for the next three sprints before broad feature expansion or large UI/runtime feature work.

## Evidence Reviewed
- Audit report: `research/ultra-codebase-audit-20260524`.
- Task tracker: `Data/Tasks` summary with `Backlog=95`, `Done=15`, `Archived=1`, and duplicate `TSK-0060`.
- Source metrics: largest source/UI files in `MemorySmith.App/Services`, `MemorySmith.App/Components/Pages`, and `MemorySmith.Tests`.
- Security code: `Program.cs`, `SecurityServices.cs`, `MemorySmithRequestGuardMiddleware.cs`, `MaintenanceAgentServices.cs`, `ChatServices.cs`.
- Validation code: `.github/workflows/ci.yml`, `Scripts/Validate-Repo.ps1`, `e2e/tests/navigation-freeze.spec.ts`.
- Test inventory: 292 listed NUnit tests from `dotnet test MemorySmith.Tests/MemorySmith.Tests.csproj --no-build --list-tests --verbosity normal`.

## Findings
| Seat | Recommendation | Confidence | Blocking Concern |
|---|---|---:|---|
| Source-Grounded Archivist | Treat current code and Core memories as primary; update stale source-link and observability/task evidence as part of backlog grooming. | 88% | Historical docs and some task comments still describe pre-OTel/pre-source-governance state. |
| Data Model Architect | Fix task-key uniqueness and canonical task validation before relying on `/tasks` as a sprint source of truth. | 91% | Duplicate `TSK-0060` creates ambiguous key lookup and weakens audit traceability. |
| Retrieval Specialist | Keep lexical/semantic/hybrid/search/MCP work mostly stable; focus retrieval sprint energy on chat governance, source-read status drift, and response quality tasks already in the backlog. | 84% | Chat Agent write proposal routing still crosses maintenance proposal boundaries without caller-specific write roots. |
| Security Reviewer | Make remote hardening and dependency advisory tracking explicit before any remote-friendly release posture. | 87% | HSTS/secure cookie/forwarded header controls are absent and OTel advisories are current warnings. |
| Skeptical Reviewer | Do not label this audit exhaustive until e2e browser, dependency-vulnerability, and remote/proxy validation gates run. | 76% | This pass is evidence-heavy but still planning-mode, not full execution-mode validation. |
| Synthesizer | Commit a three-sprint stabilization plan: task integrity and chat governance first, CI/security gates second, decomposition and observability budgets third. | 82% | Scope discipline is the main delivery risk; many valuable tasks compete for the same single-host surfaces. |

## Synthesis
What changes now:
- Add focused task records for task-key consistency, benchmark budgets, and OTel advisory remediation.
- Use the sprint plan to sequence existing high-value tasks rather than creating a parallel backlog.
- Refresh the stale source-link security memory to match current code.

What is deferred:
- Broad markdown runtime features, Pyodide, advanced exports, and UI feature expansion remain behind stabilization gates.
- Large service/component decomposition starts after governance and CI gates have a stable baseline.
- Full e2e/browser/deployment execution is a sprint validation activity, not completed in this discovery pass.

## Dissent
- The Skeptical Reviewer would make `TSK-0067` the first task, ahead of task-key cleanup, because browser lockups are user-visible. The Synthesizer keeps task-key cleanup in Sprint 1 because this audit depends on `/tasks` traceability and the fix should be small.
- The Retrieval Specialist would defer benchmark budgets until after chat quality tasks; the Security Reviewer argues OTel/package warning normalization is already visible in validation output and must be tracked now.

## Acceptance Criteria
- Every committed sprint item has an existing or new `Data/Tasks` record.
- Duplicate task keys are removed or detected by an executable guard before new audit packages depend on key lookups.
- Chat page-write approval success/failure is covered by focused tests before user-facing Agent write UX expands.
- CI has a named browser validation lane or an explicit temporary non-blocking rollout with artifacts.
- OTel package advisory status is tracked with an upgrade/acceptance decision, not hidden in command output.

## Open Questions
- Should `TSK-0060` source governance be renumbered or should screenshot capture be renumbered to preserve historical links?
- Should chat write roots be enforced before proposal submission, at proposal apply time, or both?
- Should remote hardening fail startup or block specific request surfaces while showing admin diagnostics?

## Confidence
- Council synthesis confidence: 82%.