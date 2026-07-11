# Sprint Plan - Ultra Codebase Audit Stabilization Sequence - 2026-05-24

## Planning Assumptions
- Team size: one primary implementer plus review support.
- Sprint duration: one week each.
- Effective capacity: about 20 focused engineering hours per sprint after review, validation, and documentation overhead.
- Risk buffer: 25% reserved for test repair, UI validation, and task/wiki cleanup.
- Primary task surface: `Data/Tasks` and `/tasks`.
- Narrative source: this page plus `research/ultra-codebase-audit-20260524` and `council/ultra-codebase-audit-prioritization-council-20260524`.

## Sprint 1 - Governance And Tracker Integrity

## Sprint Objective
Make the planning and Agent-write trust boundaries reliable enough to support the rest of the backlog.

## Committed Items
- `TSK-0114` Add task-key uniqueness and backlog consistency validation. Estimate S. Depends on `TSK-0053` context.
- `TSK-0016` Fix chat agent page-write approval path validation for safe `Data/Pages/*.md` proposals. Estimate M.
- `TSK-0022` Add separate chat-agent write root settings distinct from maintenance-agent roots. Estimate M.
- `TSK-0017` Ensure chat status counters and pending-write badges update immediately after outcomes. Estimate S.
- `TSK-0018` Fix `Approve all` batch semantics to be per-item and deterministic. Estimate M.

## Stretch Items
- `TSK-0021` Reject unsafe page/memory proposal identifiers at proposal time.
- `TSK-0053` Add task JSON contract canonicalization and compatibility guardrails.

## Exit Criteria
- Duplicate task keys are detected by an executable check, and the current `TSK-0060` collision has a migration decision.
- Safe page proposal approval has focused regression coverage.
- Unsafe page/memory proposal identifiers are rejected before disk mutation and preferably before pending approval display.
- Pending approval counters and batch trace messages reconcile after approve/reject/block/fail outcomes.

## Demo Targets
- A safe Agent page proposal goes from pending to submitted/applied without the maintenance write-root error.
- A deliberately unsafe proposal is blocked with clear trace/audit text.
- A task integrity check reports duplicate keys and non-canonical task JSON clearly.

## Sprint 2 - Security And CI Gates

## Sprint Objective
Turn known safety and browser-regression gaps into enforced or explicitly staged gates.

## Committed Items
- `TSK-0067` Add required PR Playwright navigation-freeze gate. Estimate M.
- `TSK-0069` Split CI test topology and required check contract. Estimate M.
- `TSK-0071` Align validation docs and CI gates for browser coverage. Estimate S.
- `TSK-0023` Add startup/admin guardrails for secure remote mode. Estimate M.
- `TSK-0037` Add transport hardening baseline with secure cookie and HSTS controls. Estimate M.
- `TSK-0038` Add trusted proxy and forwarded-header security controls. Estimate M.

## Stretch Items
- `TSK-0039` Add targeted anti-forgery and bootstrap hardening for auth setup flows.
- `TSK-0040` Add security regression matrix for remote profile, proxy, and auth.
- `TSK-0116` Track and remediate OpenTelemetry package advisories if patched packages are available.

## Exit Criteria
- CI has a named browser validation lane or a documented staged rollout with artifacts.
- Remote API unsafe combinations are blocked or explicitly flagged with enforceable startup/admin behavior.
- Cookie, HSTS, forwarded-header, and proxy expectations are covered by tests or documented deployment gates.
- Validation docs match the checks that actually run.

## Demo Targets
- A CI run shows separate .NET and browser validation outputs.
- Diagnostics/admin settings make remote security posture understandable without hand-reading config.

## Sprint 3 - Architecture And Measurement

## Sprint Objective
Reduce blast radius in the largest code paths while adding measurable performance and observability budgets.

## Committed Items
- `TSK-0042` Decompose ChatServices into bounded modules. Estimate L.
- `TSK-0043` Decompose MaintenanceAgentServices into bounded modules. Estimate L.
- `TSK-0044` Split PagesAndChatTests into focused fixtures. Estimate M.
- `TSK-0105` Retarget request, exception, and ProblemDetails correlation after current request logging work. Estimate M.
- `TSK-0106` Surface structured application and Windows Event Log records in admin diagnostics. Estimate M.
- `TSK-0115` Add benchmark regression budgets and CI summary checks. Estimate M.

## Stretch Items
- `TSK-0047` Add complexity guardrails and architecture conformance checks.
- `TSK-0107` Add log-derived health charts for error trends, latency, and slow operations.
- `TSK-0108` Add performance benchmark trace events for critical MemorySmith flows.

## Exit Criteria
- Chat and maintenance extractions are behavior-preserving and covered by focused tests.
- Pages/chat tests are split enough that failures point to one bounded feature area.
- Benchmark budgets are reportable and at least one critical flow has a stable threshold or report-only trend.
- ProblemDetails/correlation design reflects current Serilog and OTel behavior instead of stale pre-implementation assumptions.

## Demo Targets
- A chat/tool/approval test failure points to a focused fixture rather than a broad catch-all file.
- A validation summary shows benchmark budget results or clearly marked report-only budget drift.

## Deferred Items
- Markdown rendering feature expansion (`TSK-0075` through `TSK-0090`) waits until browser and task integrity gates are stable.
- Chat response quality polish (`TSK-0091` through `TSK-0100`) follows write-governance fixes unless a user-facing retrieval regression appears.
- Full remote deployment testing remains outside this planning pass and should be covered by the security sprint's validation matrix.

## Task Links
- Audit report: `research/ultra-codebase-audit-20260524`.
- Council review: `council/ultra-codebase-audit-prioritization-council-20260524`.
- New task records: `TSK-0114`, `TSK-0115`, `TSK-0116`.