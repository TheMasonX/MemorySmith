# Security and Architecture Convergence Addendum (2026-05-23)

## Intent

Blend security hardening and architecture simplification into one delivery stream so controls improve as complexity decreases, instead of layering more controls onto already oversized modules.

## Why Convergence

- Security risk is amplified by monolithic service files where trust boundaries and behavior are harder to inspect.
- Architecture risk is amplified when refactors do not preserve security invariants explicitly.
- The fastest risk reduction path is decomposition with embedded security checks at each extraction seam.

## Converged Findings

| ID | Theme | Severity | Confidence | Summary | Evidence |
|---|---|---|---:|---|---|
| C-001 | Secure refactoring boundary | High | 92% | Oversized orchestration files (chat and maintenance) carry both behavior and authorization/governance decisions, making security regressions likely during refactor unless invariants are codified first. | MemorySmith.App/Services/ChatServices.cs, MemorySmith.App/Services/MaintenanceAgentServices.cs, MemorySmith.App/Services/SecurityServices.cs |
| C-002 | Configuration safety + architecture drift | High | 88% | Security posture depends on many configuration switches; without architectural guardrails and profile governance, complexity drift can reopen insecure combinations. | MemorySmith.App/Services/AdminSettingsService.cs, MemorySmith.App/appsettings.json, MemorySmith.App/appsettings.LocalDevelopment.json |
| C-003 | Test topology and trust guarantees | Medium | 86% | Monolithic mixed-domain tests reduce the ability to prove security invariants at architectural seams. | MemorySmith.Tests/PagesAndChatTests.cs, MemorySmith.Tests/SecurityAndSourceLinkTests.cs |
| C-004 | Dependency and attack surface coupling | Medium | 79% | Dependency hygiene is part of architecture health and security posture; stale packages increase maintenance and potential exposure. | MemorySmith.App/MemorySmith.App.csproj, workspace package/use scan |
| C-005 | Task-store fault containment | High | 90% | Task loading now includes malformed-file fallback handling, but recovery behavior is still incomplete across mutation/error-contract paths; malformed artifacts can still degrade reliability and governance UX if endpoint mappings are inconsistent. | MemorySmith.App/Services/TaskDomainService.cs, MemorySmith.App/Components/Pages/Tasks.razor, MemorySmith.App/Controllers/TasksController.cs, MemorySmith.App/bin/Debug/net10.0/logs/memorysmith-20260523.log |
| C-006 | Task contract version drift | Medium | 91% | Task records currently exist in mixed root property casing variants (25 PascalCase, 27 camelCase). Runtime tolerates both today, but tooling/migrations/validation paths can diverge without an explicit canonical format and compatibility tests. | Data/Tasks/*.json audit, MemorySmith.App/Services/TaskDomainService.cs |
| C-007 | Inconsistent task mutation error mapping | High | 93% | Task service now blocks edits for malformed fallback records via `EnsureTaskIsEditable` (`ArgumentException`), but not all mutation endpoints map `ArgumentException` to 4xx, so some paths can still return 500 instead of actionable client errors. | MemorySmith.App/Controllers/TasksController.cs, MemorySmith.App/Services/TaskDomainService.cs |
| C-008 | Task read API authorization and disclosure posture | High | 95% | Task read endpoints (`GET /api/tasks`, `GET /api/tasks/{id}`) have no explicit view policy and are reachable without auth headers in current runtime; combined with remote-enabled local development profile this can expose task content and operational details unexpectedly. | MemorySmith.App/Controllers/TasksController.cs, MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs, MemorySmith.App/appsettings.LocalDevelopment.json, runtime probe to `/api/tasks` |

## Converged Strategy

1. Extract architecture seams where security decisions live (chat tool/write loop, maintenance proposal workflow).
2. Add invariant tests before/with each extraction (authorization, approval gating, request guard behavior).
3. Apply transport/remote hardening concurrently with service decomposition.
4. Add complexity and security drift guardrails in CI/local validation.

## Risk Register

- R-CA-001: Refactor introduces authorization bypass in extracted components.
  - Impact: High
  - Likelihood: Medium
  - Mitigation: Add invariant tests first and require green gates per slice.
- R-CA-002: Security hardening introduces functional regressions in local workflows.
  - Impact: Medium/High
  - Likelihood: Medium
  - Mitigation: explicit localhost compatibility profile and acceptance tests.
- R-CA-003: Parallel architecture/security streams diverge in ownership.
  - Impact: Medium
  - Likelihood: Medium
  - Mitigation: single convergence sprint board linked to both audit pages.
- R-CA-004: Corrupted task artifact causes hard failure across tasks UI/API paths.
  - Impact: High
  - Likelihood: Medium
  - Mitigation: per-file parse isolation, quarantine/skip policy, and integrity validation on write/import.
- R-CA-005: Task JSON contract drift breaks automation or future schema migration.
  - Impact: Medium
  - Likelihood: Medium
  - Mitigation: canonical task JSON format, compatibility matrix tests, and lint/normalization command.
- R-CA-006: Malformed task recovery paths regress into 500 due to uneven controller exception handling.
  - Impact: High
  - Likelihood: Medium
  - Mitigation: standardize mutation endpoint exception-to-ProblemDetails mapping and add malformed-record regression tests per endpoint.
- R-CA-007: Task metadata disclosure through unauthenticated read APIs under permissive profile combinations.
  - Impact: High
  - Likelihood: Medium
  - Mitigation: require explicit `CanViewMemorySmith` policy on task read endpoints and fail startup when remote mode is enabled without required auth controls.

## Open Questions

- Q-CA-001: Which chat extraction seam should be first to minimize both complexity and trust-boundary risk?
- Q-CA-002: Should remote-hardened profile be strict-blocking by default at startup or readiness gate only?
- Q-CA-003: Which guardrails should fail CI immediately versus report-only in first phase?
- Q-CA-004: Should malformed task records be auto-quarantined or retained in-place with explicit admin recovery workflow?
- Q-CA-005: Should legacy PascalCase task records be auto-normalized to canonical camelCase on write, via one-time migration, or preserved indefinitely?
- Q-CA-006: Should task mutation endpoints standardize on typed error envelopes (ProblemDetails code set) instead of ad hoc BadRequest strings?
- Q-CA-007: Should task read APIs require authenticated viewer policy in all profiles, or only when remote access is enabled?

## Confidence

- Convergence plan confidence: 86%
- Highest confidence: C-001
- Lowest confidence: C-004 (pending dependency-prune validation)
