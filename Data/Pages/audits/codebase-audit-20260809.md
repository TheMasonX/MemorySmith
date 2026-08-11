# Codebase Audit - Deep-Dive Synthesis

**Task:** Deep-dive audit without implementation; discover bugs and improvements, then create or enrich actionable task records.
**Repository:** MemorySmith
**Audit date:** 2026-08-08 to 2026-08-09
**Author:** Agent Smith
**Status:** Research complete; implementation intentionally excluded
**Confidence:** 90% for source-backed findings; lower where deployment or runtime behavior is conditional

## Executive Summary

This deep-dive reviewed the active single-host ASP.NET Core application, domain and storage layers, schemas, security and deployment paths, tests, benchmarks, scripts, and UI navigation. The review found no confirmed P0 issue in the current source pass. It did find a concentrated set of correctness, security, concurrency, configuration, and test-validity risks at persistence and control boundaries.

The highest-value findings are non-atomic read-check-write sequences, permissive or stale security configuration, and tests that can report success without exercising the intended implementation. Runtime and schema contracts also drift: typed relationships are richer than the checked-in schema, invalid memory status values can become unreachable, page visibility can fail open on malformed metadata, and route-level UI helpers do not match the actual heading structure.

The audit created 25 previously uncovered task records, `TSK-0453` through `TSK-0477`. The inventory contains 11 High-priority and 14 Medium-priority records, all currently Backlog. Existing overlapping records were enriched with current-source evidence rather than duplicated. Task metadata validation passed for all 457 records.

## Severity Summary

| Audit severity | New task records | Interpretation |
|---|---:|---|
| P0 | 0 | No confirmed critical outage, destructive behavior, or direct compromise in this source pass. |
| P1 | 11 | High-impact correctness, security, deployment, storage, or test-gate risks. |
| P2 | 14 | Medium-impact contract, concurrency, configuration, test-quality, accessibility, or maintainability risks. |
| P3 | 0 | No standalone low-impact finding retained as a new task. |

Task records use the repository's canonical `High` and `Medium` priorities rather than P-level labels.

## P1 - High Findings

### AUD-20260809-001 - Core state transition precedence prevents deprecation

**Evidence:** `MemorySmith.Core/StateMachine/MemoryStateMachine.cs`, the `Evaluate` transition branches.

A Core record below the deprecation threshold is first assigned `Core -> Deprecated`, then a later independent branch assigns `Core -> Working`. The later assignment wins, so very-low-scoring Core records cannot be deprecated through this state machine.

**Task:** `TSK-0453`.
**Recommendation:** Make the transition branches mutually exclusive and add boundary tests for scores below both thresholds.
**Confidence:** 99%.

### AUD-20260809-002 - Invalid memory status and usage values cross the write boundary

**Evidence:** `MemorySmith.App/Controllers/MemoriesController.cs`, `MemoryApplicationService.cs`, and `MemorySmith.Storage/FileMemoryStore.cs`.

The write path accepts undefined numeric `MemoryStatus` values and negative `UsageCount`. An undefined status can be written under a directory that normal reads never search, while negative usage violates scoring assumptions.

**Task:** `TSK-0456`.
**Recommendation:** Reject invalid enum values and negative counters before filesystem mutation, with API and store tests.
**Confidence:** 98%.

### AUD-20260809-003 - Browser mutation controllers retain antiforgery exemptions

**Evidence:** browser-facing Chat, Governance, MaintenanceAgent, Memories, Pages, and Tasks controllers; global antiforgery configuration.

Controller-level `IgnoreAntiforgeryToken` remains on cookie-authenticated browser mutation surfaces. Existing global antiforgery work therefore does not protect these residual paths.

**Task:** `TSK-0457`.
**Recommendation:** Remove the exemptions or isolate them to an explicit non-browser protocol, then add browser mutation regression coverage.
**Confidence:** 96%.

### AUD-20260809-004 - CI secret scanning is non-blocking and bounded by silent skips

**Evidence:** `.github/workflows/ci.yml` and `Scripts/Invoke-SecretScan.ps1`.

The workflow ignores secret-scan failures, the scanner caps traversal at 500 files, and read failures are silently skipped. A clean result therefore does not prove that the repository was fully scanned.

**Task:** `TSK-0462`.
**Recommendation:** Make failures blocking after baseline cleanup, remove or explicitly report file caps, and fail or visibly report read errors.
**Confidence:** 98%.

### AUD-20260809-005 - Positive MCP benchmark tests are blocked before tool execution

**Evidence:** `MemorySmith.Tests/SearchBenchmarkTests.cs` and request-guard configuration.

Positive tests expect HTTP 200 but their test host does not enable `MemorySmith:AllowRemoteApi`; requests receive 403 before the intended MCP tool contract runs. The test suite can therefore misrepresent tool coverage.

**Task:** `TSK-0463`.
**Recommendation:** Configure positive tests for the intended allowed mode and retain a separate explicit 403 denial test.
**Confidence:** 97%.

### AUD-20260809-006 - Semantic benchmark names conceal token fallback

**Evidence:** `MemorySmith.Tests/SearchBenchmarkTests.cs` construction paths.

Ordinary semantic benchmark cases construct `MemoryApplicationService` without `SemanticEmbeddingSearchService`, so they exercise token fallback while carrying semantic names.

**Task:** `TSK-0466`.
**Recommendation:** Inject the real provider for model-backed cases, assert provider metadata, and name fallback tests separately.
**Confidence:** 97%.

### AUD-20260809-007 - Redeployment defaults can expose remote HTTP

**Evidence:** `Scripts/Redeploy-MemorySmithService.ps1`, including binding, remote API, and certificate fallback settings.

The redeployment script defaults to all-interface binding, enables remote API access, and continues with HTTP when a certificate is unavailable. These defaults override safer application defaults and can expose the service without transport protection.

**Task:** `TSK-0467`.
**Recommendation:** Fail closed to loopback; require explicit remote exposure plus HTTPS and a valid certificate.
**Confidence:** 96%, conditional on using the script for deployment.

### AUD-20260809-008 - Development certificate passwords enter files and command lines

**Evidence:** `Scripts/New-MemorySmithDevCert.ps1` and `Scripts/Redeploy-MemorySmithService.ps1`.

The certificate password is written in plaintext and later passed as a Kestrel command-line argument, where it can appear in process inspection or shell history.

**Task:** `TSK-0469`.
**Recommendation:** Use certificate-store/thumbprint or protected secret bindings and eliminate plaintext/password-argument handling.
**Confidence:** 98%.

### AUD-20260809-009 - Navigation-freeze browser suite is disabled while CI still invokes it

**Evidence:** `e2e/tests/navigation-freeze.spec.ts` and `.github/workflows/ci.yml`.

The suite disables its entire describe block while CI continues to invoke the file. A green command can therefore mean zero executed navigation-freeze tests.

**Task:** `TSK-0470`.
**Recommendation:** Re-enable or repair the suite and assert a nonzero executed-test count. Reconcile completion evidence for `TSK-0067`, `TSK-0070`, and `TSK-0071`.
**Confidence:** 99%.

### AUD-20260809-010 - File-backed stores are not coordinated across processes

**Evidence:** `MemorySmith.Storage/FileMemoryStore.cs`, `FileEventStore.cs`, `FileVarStore.cs`, and storage registration.

Locks are instance-local, while variable writes use a fixed temporary path without a shared writer lock. Singleton registration coordinates one process only. Multiple app or service processes can race on memory saves, event appends, and variable updates.

**Task:** `TSK-0471`.
**Recommendation:** Add OS-level coordination or explicitly prohibit multi-process use, and test the supported deployment mode with two processes.
**Confidence:** 94%, conditional on multi-process access being supported or possible.

### AUD-20260809-011 - Persisted security settings do not consistently reload at runtime

**Evidence:** `AdminSettingsService.cs`, `MemorySmithRequestGuardMiddleware.cs`, security setup, and configuration setup.

The settings service reloads overrides, but request guarding and login rate limiting capture startup values. The UI can report a successful update while `AllowRemoteApi`, `ApiKey`, or rate limits remain stale until restart.

**Task:** `TSK-0476`.
**Recommendation:** Use monitor-backed policy evaluation or clearly mark these settings restart-required and test the chosen contract.
**Confidence:** 95%.

## P2 - Medium Findings

| ID | Finding | Evidence / task |
|---|---|---|
| AUD-20260809-012 | Fixed delays make asynchronous tests timing-dependent. | `AgentSessionTests.cs`, `SemanticEmbeddingPrewarmServiceTests.cs`, `AppApiContractTests.cs`; `TSK-0454` |
| AUD-20260809-013 | Runtime typed relationships are not represented by the checked-in schema. | `MemoryRecord.cs`, `MemoryRelationshipEdge.cs`, `Schemas/memory.schema.json`; `TSK-0455` |
| AUD-20260809-014 | Task attachment names use check-then-create semantics. | `TaskDomainService.cs`; `TSK-0458` |
| AUD-20260809-015 | Security history artifact versions are allocated from a non-atomic count. | `SecurityServices.cs`; `TSK-0459` |
| AUD-20260809-016 | Malformed page metadata falls back to the configured default role, which is Anonymous. | `PageService.cs`; `TSK-0460` |
| AUD-20260809-017 | Audit hash-chain predecessor reads and appends are separate operations. | `SecurityServices.cs`; `TSK-0461` |
| AUD-20260809-018 | SQLite history versions use `MAX(VersionNumber) + 1` without deterministic conflict retry. | `SqliteMemorySmithDatabase.cs`; `TSK-0464` |
| AUD-20260809-019 | A committed absolute path in `Data/vars.json` is machine-specific. | `Data/vars.json`, `VarResolver.cs`; `TSK-0465` |
| AUD-20260809-020 | Checked-in schema validation is not the runtime validation path. | `Schemas/memory.schema.json`, `Scripts/Test-MemoryRecords.ps1`; `TSK-0468` |
| AUD-20260809-021 | Current-directory mutation leaks process-wide test state. | `SemanticEmbeddingPathTests.cs`; `TSK-0473` |
| AUD-20260809-022 | Code-search measurement scripts have one-hour timeouts but no thresholds or CI gate. | `Scripts/Measure-CodeSearchRelevance.ps1`, `Measure-CodeSearchQueries.ps1`; `TSK-0474` |
| AUD-20260809-023 | Route focus targets `h1`, while representative pages render lower-level headings. | `Components/Routes.razor`, `Components/Pages/*.razor`; `TSK-0475` |
| AUD-20260809-024 | Route title fallback can overwrite valid page titles with a fixed Health title. | `wwwroot/memorysmith.js`, `HealthStats.razor`; `TSK-0477` |
| AUD-20260809-025 | Singleton-created page settings remain stale after an admin configuration update. | `Hosting/MemorySmithStorageSetup.cs`, `Services/PageService.cs`, `Services/AdminSettingsService.cs`; `TSK-0472` |

The following existing-task findings were also retained as current-source coverage rather than duplicated: model/provider override authorization (`TSK-0452`), source-sensitive code-search output (`TSK-0447`), shard provenance (`TSK-0448`), raw query retention (`TSK-0449`), page slug collision (`TSK-0450`), anonymous feedback isolation (`TSK-0451`), settings write serialization (`TSK-0445`), and related storage, maintenance, API, test, and UI owners.

## Cross-Cutting Findings

### Invariants are enforced outside the persistence boundary

Session caps, status validity, version allocation, attachment uniqueness, hash-chain continuity, and settings updates all use sequences of load, check, mutate, and save operations. These sequences cannot preserve invariants under concurrent callers unless the store exposes an atomic operation or the deployment explicitly guarantees single-writer access.

### Runtime configuration has inconsistent lifetime semantics

Some services reload persisted settings while other consumers capture startup options. Operators can receive a successful write response without an immediate behavior change. Every editable security or rendering setting needs an explicit live-reload or restart-required contract.

### Tests and names overstate coverage

Disabled suites, guard-blocked positive tests, and semantic names attached to fallback implementations create false confidence. Test acceptance should assert that the intended provider, endpoint mode, and nonzero test count were actually used.

### Schema, runtime, and documentation are separate contracts

Typed relationships, status rules, and source-sensitive output are richer or stricter in runtime than in checked-in schemas and tool descriptions. A parity test or executable schema should be treated as a product contract, not just documentation.

### Error classification is too lossy at boundaries

Malformed configuration, missing data, authorization failures, transport failures, and storage corruption are sometimes flattened into null, empty results, or generic success. This prevents correct recovery and obscures operational diagnosis.

## Existing Task Reconciliation

The audit created only uncovered records. Overlapping tasks received evidence comments that identify current source behavior, stale completion claims, or acceptance gaps. In particular:

- Existing `Done` records were not treated as proof of resolution when current source contradicted their descriptions.
- `TSK-0344` was updated for the raw reference-count scoring behavior and stale wording.
- Storage/index/maintenance records were enriched where current behavior remained broader than the historical task scope.
- Agent-session, OAuth, settings, security, API, search, and test records received concrete current-source acceptance evidence.
- No product implementation was performed, including no P3 quick-fix batch.

## Task Inventory

The 25 new records are exactly:

- **High:** `TSK-0453`, `TSK-0456`, `TSK-0457`, `TSK-0462`, `TSK-0463`, `TSK-0466`, `TSK-0467`, `TSK-0469`, `TSK-0470`, `TSK-0471`, `TSK-0476`.
- **Medium:** `TSK-0454`, `TSK-0455`, `TSK-0458`, `TSK-0459`, `TSK-0460`, `TSK-0461`, `TSK-0464`, `TSK-0465`, `TSK-0468`, `TSK-0472`, `TSK-0473`, `TSK-0474`, `TSK-0475`, `TSK-0477`.

All 25 are `Backlog`. No duplicate keys were found.

## Validation Evidence

- `pwsh ./Scripts/Test-TaskRecords.ps1`
- Result: `PASS: Checked 457 task record(s); keys and ids are unique.`
- Independent inventory check: 25 matching files, keys `TSK-0453` through `TSK-0477`, 11 High, 14 Medium, 0 non-Backlog, 0 duplicate keys.
- Product source implementation: intentionally not changed by this audit.
- Existing dirty worktree changes: preserved and excluded from findings unless they were directly relevant to the audit evidence.

## Conditional Findings and Assumptions

- The file-store process-coordination risk matters if multiple app/service processes can access the same data path. If single-process ownership is a hard deployment invariant, document and enforce that invariant instead.
- The redeployment and certificate findings apply when the supplied PowerShell deployment scripts are used. They are not claims about every manually configured deployment.
- The page metadata finding assumes malformed metadata should not downgrade visibility. If the intended policy is fail-open, that policy needs explicit documentation and tests.
- The inventory, route, and benchmark findings are source-backed; live browser, provider, and Minecraft execution was not run as part of this research-only pass.
- Task priorities are triage recommendations, not proof of exploitability or production impact.

## Out of Scope

- Applying any source fix, quick fix, refactor, schema change, deployment change, or test repair.
- Running a live Minecraft server, remote deployment, production OAuth provider, or external model service.
- Replacing the existing audit history or deleting stale reports.
- Reclassifying every historical task in the repository beyond records directly touched by this audit.

## Recommended Council Review

Run a four-seat council review before implementation planning:

1. **Source-grounded verifier:** independently recheck each P1 claim against current files and line locations.
2. **Security and deployment reviewer:** calibrate antiforgery, settings reload, remote HTTP, certificate, and secret-scanning severity under supported deployment modes.
3. **Storage and data-integrity reviewer:** review atomicity, version allocation, schema parity, malformed metadata, and cross-process assumptions.
4. **QA and observability reviewer:** verify that proposed tests exercise the intended provider, endpoint, browser suite, and failure classification.

The council should record confidence, dissent, severity changes, stale-task corrections, and acceptance criteria. No implementation should be marked Ready solely from this report until the council resolves the conditional findings.

## Next Steps

1. Council-review the 11 High findings and the enriched existing task records.
2. Choose the supported storage concurrency model: coordinated multi-process access or an explicit single-process constraint.
3. Define a common atomic-operation and failure-classification contract for stores and boundary services.
4. Reconcile schema, runtime validation, tool descriptors, and configuration reload semantics.
5. Convert the highest-confidence findings to Ready only after acceptance tests are scoped; leave implementation for a separate task cycle.
