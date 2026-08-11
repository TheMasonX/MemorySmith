# MemorySmith Codebase Maturity, Health, Portability, and Task Status

**Audit date:** 2026-08-10
**Repository:** `D:\@Repos\MemorySmith`
**Scope:** Active MemorySmith single-host repository: Core, Storage, App, Bridge, Tests, Benchmarks, E2E, CI, scripts, task records, and project documentation.
**Author:** Agent Smith
**Audit type:** Evidence-based status update and maturity review; no product implementation was performed.
**Confidence:** 88% for repository-local measurements and validation results; 65-80% for conditional operational and deployment ratings.

## Executive Summary

MemorySmith is a broad, unusually complete local knowledge-workbench product rather than a prototype. It has a single-host ASP.NET Core application, Blazor workbenches, REST and MCP surfaces, file-backed wiki content, SQLite security metadata, chat and agent workflows, semantic and hybrid search, maintenance jobs, operational diagnostics, deployment scripts, CI coverage artifacts, CodeQL, and browser regression jobs. The architecture is coherent and the repository has strong documentation and task governance.

The current maturity constraint is verification closure, not feature breadth. The active tree contains 457 task records, but only 163 are `Done` (35.7%); 126 open tasks are `High` or `Critical` (27.6% of all records), and 138 open records are older than 30 days. The latest deep audit identified 11 High and 14 Medium source-backed risks, concentrated in persistence atomicity, security configuration lifetime, deployment defaults, schema/runtime parity, and test validity.

The current working tree also has substantial uncommitted changes. The focused solution build reached and built the Core, Storage, Bridge, App, and Benchmark projects, but failed while compiling the Tests project. The first structural failures are in the changed `MemorySmith.Tests/McpAndSemanticSearchTests.cs`; repository validation therefore stops at memory-record validation. This makes the current release-readiness rating materially lower than the product-scope rating until the test tree is repaired and the full validation chain is green.

## Overall Scorecard

These are maturity ranges, not mathematically precise quality scores. The lower bound reflects verified gaps and current validation state; the upper bound credits implemented breadth, documentation, and existing controls.

| Area | Current range | Signal | Interpretation |
|---|---:|---|---|
| Product capability and scope | 82-90% | Strong | Many user-facing surfaces are implemented and share one host and data model. |
| Architecture coherence | 72-82% | Good | Single-host boundaries are clear, but large orchestrators and duplicated boundary logic remain. |
| Core correctness and data integrity | 58-70% | Watch | State transitions, input invariants, version allocation, and multi-writer assumptions need closure. |
| Security and trust boundaries | 58-70% | Watch | Good defaults and layered controls exist, but residual antiforgery, live-setting, deployment, and secret-scan gaps remain. |
| Test maturity | 55-68% | Watch | 545 NUnit test attributes and strong fixture patterns exist, but the current test project does not compile and navigation-freeze coverage is skipped. |
| CI/CD maturity | 72-82% | Good with gaps | CI has vulnerability scanning, CodeQL, coverage artifacts, and browser jobs; secret scanning remains report-only. |
| Observability and operations | 68-78% | Good | Health, diagnostics, audit metadata, JSONL events, and OpenTelemetry are present; failure classification and live reload semantics still drift. |
| Maintainability and modularity | 45-60% | Weakest area | The App project is approximately 47,075 source lines, with large service/component orchestrators and known complexity debt. |
| Portability | 54-68% | Conditional | .NET and CI run on Ubuntu, but Windows service/certificate scripts, local path variables, and PowerShell-first operations constrain deployment portability. |
| Documentation and knowledge continuity | 82-90% | Strong | README, architecture pages, prompt contracts, memories, audit history, and validation runbooks are unusually extensive. |
| Task governance and tracking | 78-88% | Good process | Task schema validation passes and status vocabulary is enforced; backlog aging and stale completion evidence reduce delivery confidence. |
| Release readiness at audit time | 40-55% | Blocked | Current test compilation failure prevents a green repository validation run; E2E and secret-scan gaps remain even after compilation is restored. |

**Overall engineering maturity:** **62-72%**.

**Overall health:** **58-68%**.

**Portability readiness:** **54-68%**.

**Task-record completion:** **35.7% Done**, or **44.4% Done/Archived/Rejected**. These percentages describe tracker state, not feature completion or production readiness.

## Current Validation Evidence

| Check | Result | Meaning |
|---|---|---|
| `Scripts/Test-TaskRecords.ps1` | PASS: 457 records; IDs and keys unique | Task data is structurally healthy at this snapshot. |
| `Scripts/Test-PageLinks.ps1` | PASS: 358 links across 258 files | Markdown link integrity is healthy. |
| `Scripts/Test-PagePathLiterals.ps1` | PASS: 8 literals across 258 files | Checked page path references are healthy. |
| `Scripts/Validate-Repo.ps1 -SkipBuild -SkipTests` | FAIL at memory-record validation | The orchestrator invokes test-backed validation and stops when the Tests project cannot compile. |
| `dotnet build MemorySmith.slnx --configuration Debug` | FAIL in `MemorySmith.Tests` | Core, Storage, Bridge, App, and Benchmarks built; test compilation emitted 213 errors. |
| Test project | NOT EXECUTABLE | The first failures are structural errors in the current `McpAndSemanticSearchTests.cs` edit; later errors are cascade diagnostics. |
| Browser E2E | Partially covered | Route smoke is active; `navigation-freeze.spec.ts` is still `test.describe.skip`. |
| Secret scanning | CI report-only | `.github/workflows/ci.yml` uses `continue-on-error: true` for the scan. |

The working tree was already materially dirty at audit start, with changes across source, tests, task records, memories, graph embeddings, and audit pages. The audit does not attribute those edits to this report and does not revert or repair them.

## Task Completion Status

### Lifecycle distribution

| Status | Count | Share |
|---|---:|---:|
| Done | 163 | 35.7% |
| Archived | 40 | 8.8% |
| Backlog | 231 | 50.5% |
| InProgress | 17 | 3.7% |
| Ready | 5 | 1.1% |
| Blocked | 1 | 0.2% |
| Total | 457 | 100% |

### Priority and delivery risk

| Metric | Count | Share of all records |
|---|---:|---:|
| Critical total | 27 | 5.9% |
| High total | 184 | 40.3% |
| Medium total | 233 | 51.0% |
| Low total | 13 | 2.8% |
| Open Critical/High | 126 | 27.6% |
| Active (`Ready` + `InProgress`) | 22 | 4.8% |
| Open records older than 30 days | 138 | 30.2% |

The task system is functioning as a durable planning surface, but the current queue is not tightly coupled to execution throughput. The most important management signal is the combination of 126 open High/Critical records, only 22 actively staged records, and 138 open records older than 30 days. A task marked `Done` is also not sufficient evidence by itself when later audits identify the same invariant; acceptance tests and current-source reconciliation should remain mandatory.

## Area Breakdown

### 1. Product and architecture

**Maturity: 72-82%.** The single-host design is a good fit for local structured memory management. UI, REST, MCP, chat, storage, maintenance, diagnostics, and background work share a recognizable runtime boundary. Core and Storage are comparatively compact at approximately 618 and 1,833 source lines, while the App project carries approximately 47,075 lines across 126 source files.

The principal architecture debt is concentration. Chat, admin, maintenance, code search, and application orchestration carry multiple responsibilities, and the latest audit continues to identify large classes and duplicated policy logic. This raises change risk even where individual features are well tested.

**Positive evidence:** `MemorySmith.Core`, `MemorySmith.Storage`, `MemorySmith.App`, `MemorySmith.Tests`, `MemorySmith.Benchmarks`, and `MemorySmith.Bridge` have explicit project roles; `Data/Pages/architecture.md` describes the intended UI/API/service flow.

**Remaining risk:** large orchestrators and boundary code need decomposition only where it reduces invariant duplication, not as a broad rewrite.

### 2. Core correctness and persistence

**Maturity: 58-70%.** The storage and domain layers have meaningful tests and explicit data policies, but the latest source audit found correctness risks that affect durable state:

- Core state transition precedence can prevent deprecation.
- Undefined memory status values and negative usage counters can cross the write boundary.
- SQLite and security history version allocation use non-atomic patterns.
- File-backed stores coordinate only within one process; multi-process access is not safe unless explicitly prohibited.
- Attachment creation and audit hash-chain updates have check-then-act windows.
- Schema, runtime validation, and typed relationships are not fully aligned.

These are not all confirmed production incidents. They are high-confidence invariant risks, with multi-process findings conditional on the supported deployment model.

### 3. Security and trust boundaries

**Maturity: 58-70%.** The repository has strong foundations: loopback-first API posture, API-key support, RBAC, first-admin setup, protected source roots, raw HTML disabled by default, security metadata in SQLite, CodeQL, and dependency vulnerability checks in CI.

The remaining security maturity gap is consistency at boundaries. The latest audit identifies residual browser mutation antiforgery exemptions, stale runtime consumers after persisted security settings change, deployment scripts that can default to remote HTTP, plaintext certificate-password handling, and non-blocking secret scanning. These should be treated as deployment and policy risks until the supported operational contract is explicit and tested.

### 4. Test and quality engineering

**Maturity: 55-68%.** There are 42 test files and approximately 545 NUnit test attributes in the current tree, with strong hand-written test doubles, temporary wiki fixtures, API contract coverage, storage hardening tests, search tests, and focused security tests. The test architecture is one of the stronger parts of the repository in design.

The current snapshot is nevertheless not test-healthy because the Tests project does not compile. The first errors are in an uncommitted `McpAndSemanticSearchTests.cs` edit, so this is a current workspace blocker rather than a confirmed committed regression. Separately, semantic tests can depend on optional model availability, benchmark tests are not ordinary CI gates, no Blazor component test layer is present, and the navigation-freeze browser suite is disabled while its CI job still runs.

### 5. CI/CD and supply chain

**Maturity: 72-82%.** CI runs on Ubuntu with .NET 10, validates task/memory/page data, checks vulnerable packages, builds, runs tests with Cobertura collection, publishes coverage artifacts, runs CodeQL, and executes route-smoke and navigation-freeze jobs. This is a credible delivery pipeline.

The main weakness is false-green potential. Secret scanning explicitly continues on error, and the navigation-freeze spec is skipped. There is also no single green local validation result at the audit snapshot because test compilation stops the orchestrator. The repository should make the intended gate semantics executable: a scan that is advisory should be named advisory, and a required browser gate should assert that it executed a nonzero test count.

### 6. Operations and observability

**Maturity: 68-78%.** Health and diagnostics routes, audit history, JSONL event mirroring, maintenance telemetry, OpenTelemetry packages, and dashboard surfaces provide a substantial operational base. The README and wiki describe these surfaces clearly.

The current risk is semantic rather than absent instrumentation. Configuration settings have inconsistent lifetime behavior, failures can be flattened into defaults or empty results, and some dashboards or service paths have historically carried placeholder values. Operational contracts should state which settings are live, which require restart, and which diagnostics are authoritative.

### 7. Portability and deployment

**Maturity: 54-68%.** The application targets `net10.0`, builds on Ubuntu CI, and uses standard ASP.NET Core components. The core domain and storage projects do not require Windows-specific APIs. This supports a reasonable cross-platform application baseline.

Portability is reduced by the deployment model around the app: Windows Service hosting, PowerShell certificate and redeployment scripts, Windows certificate-store assumptions, Windows path examples, local absolute paths in `Data/vars.json` and local overrides, and a repository that assumes PowerShell for canonical validation. The product is portable as code more than it is portable as an operational package.

**Portability recommendation:** define two explicit profiles: `cross-platform process/container` and `Windows Service`. Each profile should have its own configuration, health checks, packaging path, and validation command. Do not let Windows deployment defaults define the security posture of the cross-platform host.

### 8. Maintainability and developer experience

**Maturity: 45-60%.** This is the weakest area. The App project is approximately 47,075 lines, including approximately 24,515 service lines and 14,055 component lines. Existing audit history and task records document cognitive complexity, large constructors, duplicated parsing and policy logic, and overburdened controllers/services.

The repository compensates with extensive documentation, task records, tests, and source-linked memories. That is valuable, but documentation cannot fully offset concentrated change surfaces. The most effective work is targeted extraction around stable contracts and invariant ownership, supported by characterization tests before movement.

### 9. Documentation and knowledge continuity

**Maturity: 82-90%.** The README, architecture page, prompt contracts, decisions, active memories, audit corpus, validation scripts, and task governance create a strong institutional memory layer. Page-link and path-literal validation both pass.

The weakness is volume and drift. There are many historical audits, some reports describe earlier repository states, and current source has already outgrown portions of older knowledge records. New reports should always identify the snapshot date, distinguish current source from historical claims, and link each actionable finding to a current task or explicitly state that it is a conditional observation.

## Highest-Value Completion Risks

The latest deep audit remains the best source for the current defect backlog. Its 25 newly created records are 11 High and 14 Medium, all Backlog at report time. The most consequential themes are:

1. State and persistence invariants are enforced with non-atomic load/check/save sequences.
2. Runtime configuration reload semantics differ between settings storage and consuming services.
3. Browser mutation protection is incomplete on residual cookie-authenticated controller paths.
4. Positive tests can be blocked by request guards, fall back to token search, or be skipped while names imply stronger coverage.
5. Deployment scripts can weaken transport security or expose secrets through command-line/password-file handling.
6. Schema, runtime validation, tool descriptors, and documentation are separate contracts that can drift.

The current build blocker adds a separate immediate concern: test-source integrity is not presently verified. This should be resolved before re-triaging the 126 open High/Critical tasks, because a broken test project makes acceptance evidence unreliable.

## Recommended Completion Sequence

### Immediate: restore trustworthy feedback

1. Repair the uncommitted structure in `MemorySmith.Tests/McpAndSemanticSearchTests.cs` and rerun the solution build.
2. Run `Scripts/Validate-Repo.ps1` without skip flags and record the first fully green result.
3. Re-enable or replace the navigation-freeze suite and make its execution count observable.
4. Decide whether secret scanning is a blocking gate; if not, label it explicitly as advisory and publish the baseline exception list.

### Next: close the highest-risk invariants

1. Fix state-transition and memory-write validation invariants.
2. Define the supported process-concurrency model and enforce it in code or documentation.
3. Make history/version/hash-chain and attachment writes atomic or conflict-safe.
4. Make security and page settings either live-reloadable or explicitly restart-required.
5. Reconcile schemas, runtime validation, tool descriptors, and benchmark/provider names.

### Then: improve delivery throughput

1. Select a bounded sprint slice from the 126 open High/Critical records; avoid moving the whole backlog to `Ready`.
2. Require current source evidence and executable acceptance tests before moving a task to `Done`.
3. Age or archive stale duplicate findings after verification, without deleting historical audit evidence.
4. Decompose the largest App services only where the extraction creates a testable ownership boundary.
5. Add a cross-platform process profile and a separately hardened Windows Service profile.

## Assumptions and Open Questions

- The audit scope is the base `MemorySmith` repository, not `MemorySmith.Agent`; the workspace contains both repositories.
- The current uncommitted test-file edits may be active user work. No attempt was made to repair or revert them.
- The product is intended to support local single-process operation; if multiple processes are unsupported, that constraint should be explicit and enforced.
- Windows Service deployment is a supported profile, but it is not assumed to be the only deployment profile.
- The percentages are engineering judgment ranges anchored to observed code, task counts, validation outputs, and prior source-backed audits; they are not a formal ISO or compliance score.
- Open question: which backlog records are still authoritative after the latest 25-task audit additions and subsequent working-tree edits?
- Open question: should the next review be a council verification of the 11 High findings, or a completion-focused sprint review after the test tree is green?

## Audit Artifacts and Evidence Sources

- `README.md` - product surface, configuration, storage, search, chat, MCP, and validation map.
- `Data/Pages/architecture.md` - active single-host architecture and safety boundaries.
- `Data/Pages/Audits/codebase-audit-20260809.md` - latest deep source audit with 11 High and 14 Medium findings.
- `Scripts/Validate-Repo.ps1` - canonical local validation orchestration.
- `.github/workflows/ci.yml` - CI, vulnerability scanning, CodeQL, coverage, and browser jobs.
- `MemorySmith.App/MemorySmith.App.csproj` - target framework and runtime/deployment dependencies.
- `MemorySmith.Tests/McpAndSemanticSearchTests.cs` - current test compilation blocker observed in the dirty worktree.
- `Data/Tasks/` - 457 task records and lifecycle/priority distribution used in this report.

## Final Assessment

MemorySmith is feature-rich, documented, and architecturally legible enough to support serious continued development. It is not currently in a trustworthy release state because the test project is structurally broken in the active worktree, and the broader system still has a meaningful set of high-priority invariant, security, deployment, and test-validity risks. The right maturity target is not more surface area. It is a green, reproducible validation loop followed by deliberate closure of the highest-risk boundary contracts.

**Recommended next review:** council verification of the 11 High findings after the test project is compiling, followed by a task-status reconciliation against current source and acceptance evidence.