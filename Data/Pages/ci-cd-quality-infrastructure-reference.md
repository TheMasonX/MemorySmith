# MemorySmith.Agent — CI/CD & Quality Infrastructure Reference

**Source:** Deep-dive audit (Agent 7 of 10), verified against disk on 2026-07-10
**Confidence:** 97%

---

## Tags

`ci/cd`, `quality-infrastructure`, `workflows`, `validation-scripts`, `test-architecture`, `benchmarks`, `skill-architecture`, `coverage`, `dependabot`, `codeql`, `editorconfig`, `package-vetting`, `e2e-testing`

---

## 1. Repository Overview

Two repositories are in scope:

| Repository | Path | Solution File | Project Count | Main App |
|---|---|---|---|---|
| **MemorySmith.Agent** | `D:\@Repos\MemorySmith.Agent\` | `MemorySmith.Agent.slnx` | 10 projects | `WebUI.Blazor` |
| **MemorySmith** | `D:\@Repos\MemorySmith\` | `MemorySmith.slnx` | 7 projects | `MemorySmith.App` |

---

## 2. CI Workflow Files

### 2.1 MemorySmith.Agent — `ci.yml`

**Path:** `D:\@Repos\MemorySmith.Agent\.github\workflows\ci.yml`

**Triggers:**
- Push to `main`, `master`, `feature/**`
- Pull request to `main`, `master`

**Runs-on:** `ubuntu-latest`

**Jobs (1 total):** `build-and-test`

**Steps:**
1. `actions/checkout@v4`
2. `actions/setup-dotnet@v4` — .NET `10.0.x`
3. `dotnet restore MemorySmith.Agent.slnx`
4. `dotnet build MemorySmith.Agent.slnx --no-restore --configuration Release -p:CopilotSkipCliDownload=true`
5. `dotnet test MemorySmith.Agent.slnx --no-build --configuration Release --verbosity normal -p:CopilotSkipCliDownload=true --collect:"XPlat Code Coverage" --results-directory artifacts/TestResults -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura`
6. `pwsh ./Scripts/Test-TaskRecords.ps1` — validate task records

**Key observations:**
- ✅ Collects XPlat Code Coverage with Cobertura format
- ❌ **No `reportgenerator` step** — coverage XML is written then discarded
- ❌ **No coverage artifact upload**
- ❌ **No coverage summary in PR annotations**
- ❌ Only 1 validation script (task records)
- ❌ No `dotnet list package --vulnerable`
- ❌ No Dependabot, no CodeQL
- ❌ No e2e/browser tests
- ❌ No `nuget audit` or vulnerability scanning

### 2.2 MemorySmith (Base Repo) — `ci.yml`

**Path:** `D:\@Repos\MemorySmith\.github\workflows\ci.yml`

**Triggers:**
- Push to `main`, `master`, `feature/**`
- Pull request to `main`, `master`

**Runs-on:** `ubuntu-latest`

**Jobs (3 total):** `build-and-test`, `browser-route-smoke`, `browser-navigation-freeze`

#### Job 1: `build-and-test`

1. `actions/checkout@v4`
2. `actions/setup-dotnet@v4` — .NET `10.0.x`
3. `dotnet restore` (no .slnx specified — resolves from directory)
4. `pwsh ./Scripts/Test-TaskRecords.ps1` — validate task records
5. `pwsh ./Scripts/Test-MemoryRecords.ps1` — validate memory records
6. `pwsh ./Scripts/Test-PageLinks.ps1` — validate page links
7. `pwsh ./Scripts/Test-PagePathLiterals.ps1` — validate page path literals
8. `dotnet build --no-restore --configuration Release`
9. `dotnet test MemorySmith.slnx --no-build --configuration Release --verbosity normal --collect:"XPlat Code Coverage" --results-directory artifacts/TestResults -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura`
10. **Generate coverage report** — installs `dotnet-reportgenerator-globaltool`, generates `HtmlInline_AzurePipelines`, `Cobertura`, and `MarkdownSummaryGithub`
11. **Upload Cobertura coverage** — `actions/upload-artifact@v4` → `cobertura-coverage`
12. **Upload coverage HTML report** — `actions/upload-artifact@v4` → `coverage-html-report`

#### Job 2: `browser-route-smoke`

- `actions/setup-node@v4` — Node `22`, `cache: npm`, `cache-dependency-path: e2e/package-lock.json`
- `npm ci` in `e2e/`
- `npx playwright install --with-deps chromium`
- `npm run test:route-smoke`
- Uploads route-smoke artifacts (`artifacts/browser-validation/route-smoke`) on always
- Uploads Playwright diagnostics on failure
- Summarizes artifacts in `GITHUB_STEP_SUMMARY`

#### Job 3: `browser-navigation-freeze`

- Same Node/Playwright setup as route-smoke
- `npm run test:nav-freeze`
- Uploads Playwright diagnostics on failure only

### 2.3 MemorySmith — `docs-pages.yml`

**Path:** `D:\@Repos\MemorySmith\.github\workflows\docs-pages.yml`

**Triggers:**
- Push to `main`, `master`
- `workflow_dispatch`

**Permissions:** `contents: read`, `pages: write`, `id-token: write`

**Jobs (2):** `build-docs`, `deploy-pages`

**Setup:** Python `3.x`, Node `20`, Mermaid CLI, `markdown` pip package
**Build:** `python docs/build_pages_site.py --export-mermaid-svg`
**Deploy:** `actions/upload-pages-artifact@v3` → GitHub Pages

---

## 3. Validation Scripts Inventory

### 3.1 MemorySmith.Agent Scripts

**Path:** `D:\@Repos\MemorySmith.Agent\Scripts\`

| Script | Purpose | In CI? |
|---|---|---|
| `Test-TaskRecords.ps1` | Validates task JSON files: allowed statuses (Backlog/Ready/InProgress/Blocked/Rejected/Done/Archived), allowed priorities (Critical/High/Medium/Low), unique keys, unique ids, id↔filename match, embedded control characters, priority-as-label prohibition | ✅ Yes |
| `Normalize-TaskRecords.ps1` | Bulk fix id/format drift, strip priority labels from labels arrays, add missing keys | ❌ No |
| `Triage-BacklogTasks.ps1` | Classify backlog tasks into epics (entity, security, testing, adapter, bug) | ❌ No |
| `Create-DebugTasks.ps1` | Create debug tasks via REST API against a running MemorySmith instance | ❌ No |
| `Verify-AboutDeps.ps1` | Cross-references `.csproj` PackageReferences against `about.html` dependency table (Policy P-2) | ❌ No |
| `Start-Agent.ps1` | Launches `WebUI.Blazor` via `dotnet run` with WebUI profile | ❌ No |
| `Start-Mineflayer.ps1` | Launches Mineflayer adapter with env vars for MC_HOST/MC_PORT/WS_PORT/MC_USERNAME | ❌ No |
| `Deploy-CodebaseWiki.ps1` | Publishes and registers a Windows Service serving Agent repo's `Data/` | ❌ No |
| `Deploy-WorldWiki.ps1` | Similar wiki deployment for world data | ❌ No |
| `Get-CodebaseWikiStatus.ps1` | Displays Windows Service status for Agent wiki | ❌ No |
| `Get-WorldWikiStatus.ps1` | Displays Windows Service status for world wiki | ❌ No |
| `Stop-CodebaseWikiService.ps1` | Stops the Agent wiki Windows Service | ❌ No |
| `Stop-WorldWikiService.ps1` | Stops the world wiki Windows Service | ❌ No |
| `Uninstall-CodebaseWikiService.ps1` | Uninstalls the Agent wiki Windows Service | ❌ No |
| `Uninstall-WorldWikiService.ps1` | Uninstalls the world wiki Windows Service | ❌ No |
| `Fix-CorruptedTasks.py` | Python script to fix corrupted task JSON files | ❌ No |
| Various Python scripts | `apply_htn_v2.py`, `edit_htntasklibrary.py`, `final_fix.py`, `final_fix2.py`, `fix_syntax.py` (HTN task library tooling) | ❌ No |

### 3.2 MemorySmith (Base Repo) Scripts

**Path:** `D:\@Repos\MemorySmith\Scripts\`

| Script | Purpose | In CI? |
|---|---|---|
| `Test-TaskRecords.ps1` | Validates task JSON files (similar to Agent version, without control-character check but with `linkedPages` slug validation via `Test-PageSlug`) | ✅ Yes |
| `Test-MemoryRecords.ps1` | Runs `dotnet test --filter FullyQualifiedName~LiveMemoryRecordValidationTests` to validate memory records through NUnit | ✅ Yes |
| `Test-PageLinks.ps1` | Validates relative `.md` links in `Data/Pages`: no absolute links, no backslashes, linked files must exist. Excludes fenced code blocks and inline code spans | ✅ Yes |
| `Test-PagePathLiterals.ps1` | Validates `Data/Pages/...` path literals in markdown: no backslashes, must point to existing files. Suggests replacements for moved files | ✅ Yes |
| `Validate-Repo.ps1` | **Consolidated entrypoint** — runs build, test suite, all 4 validation scripts (task records, memory records, page links, page path literals), plus optional `-IncludeCoverage`, `-IncludeE2E`, `-IncludeDocs` flags. Referenced by `.github/copilot-instructions.md` as default validation entrypoint | ✅ Yes (recommended local) |
| `Test-FinetuneHarnessPrereqs.ps1` | Checks prerequisites for ML finetune harness | ❌ No |
| `Run-FinetuneHarness.ps1` | Runs ML finetune training | ❌ No |
| `Setup-FinetuneTrainingEnv.ps1` | Sets up ML training environment | ❌ No |
| `Warm-CodeSearchIndex.ps1` | Pre-warms code search index | ❌ No |
| `Measure-CodeSearchQueries.ps1` | Measures code search query performance | ❌ No |
| `Measure-CodeSearchRelevance.ps1` | Measures code search relevance metrics | ❌ No |
| `Merge-CodeSearchIndex.ps1` | Merges code search index shards | ❌ No |
| `Publish-WikiSite.ps1` | Publishes static wiki site | ❌ No |
| `New-MemorySmithDevCert.ps1` | Creates development certificates | ❌ No |
| `Redeploy-MemorySmithService.ps1` | Redeploys MemorySmith Windows Service | ❌ No |
| `Import-OpenTasksFromWorkbench.ps1` | Imports tasks from workbench export | ❌ No |
| `AddSourceLinks.ps1` | Adds source links to memories | ❌ No |
| `Install-CodeSearchModel.ps1` | Installs code search ML model | ❌ No |
| `Run-ShortSmoke.ps1` | Runs short smoke test | ❌ No |

---

## 4. Verified Gaps

### G-001: Coverage Collection Without Reporting (Agent Repo)
**Path:** `D:\@Repos\MemorySmith.Agent\.github\workflows\ci.yml` (step 5)
**Evidence:** The `dotnet test` step collects `XPlat Code Coverage` in Cobertura format but there is no subsequent `reportgenerator` step and no `actions/upload-artifact` step. Coverage XML is written to `artifacts/TestResults/` then discarded when the runner cleans up.
**Severity:** Medium
**Fix:** Add `reportgenerator` and `upload-artifact` steps (mirroring base repo's ci.yml steps 10-12).

### G-002: No Memory/Page Validation in Agent CI
**Path:** `D:\@Repos\MemorySmith.Agent\.github\workflows\ci.yml`
**Evidence:** Agent CI runs exactly 1 validation script (`Test-TaskRecords.ps1`). Base repo CI runs 4 scripts: task records, memory records, page links, page path literals.
**Severity:** Medium
**Fix:** Add `Test-MemoryRecords.ps1`, `Test-PageLinks.ps1`, `Test-PagePathLiterals.ps1` to Agent repo (or adapt base repo scripts).

### G-003: No Vulnerability Enforcement in CI
**Evidence:** Neither repo has `dotnet list package --vulnerable` in CI. `AGENTS.md` P-3 explicitly requires zero vulnerable packages as a P0 blocker, but there is no automated gate. The `Directory.Build.props` in Agent repo does suppress `NU1903` (NuGet advisory) via `<WarningsNotAsErrors>NU1903</WarningsNotAsErrors>`, which means advisory-level warnings don't break the build.
**Severity:** High
**Fix:** Add `dotnet list package --vulnerable` step to both CI workflows.

### G-004: No `.editorconfig` in Either Repo
**Evidence:** Confirmed via grep search — no `.editorconfig` file exists in either repo root. An old audit report (`Data/Pages/audits/Audit_20260521_191625.md`) flagged this and a hyperagent audit (`Data/Pages/audits/hyperagent-audit-8-refactoring-techdeb-20260531.md`) recommended adding one.
**Severity:** Low

### G-005: No Consolidated Validation Entrypoint in Agent Repo
**Evidence:** `Scripts/Validate-Repo.ps1` exists in base repo and is referenced in `.github/copilot-instructions.md`. Agent repo has no equivalent.
**Severity:** Low-Medium

### G-006: No Dependabot or CodeQL Configuration
**Evidence:** Confirmed via grep search — no `dependabot.yml`, `dependabot.yaml`, or CodeQL workflow in either repo's `.github/` directory.
**Severity:** High

### G-007: No Benchmark Runs in CI
**Evidence:** Base repo has `MemorySmith.Benchmarks/` (2 benchmark classes, see §6), but neither repo wires benchmarks into CI.
**Severity:** Low

### G-008: Skill Architecture Inconsistency
**Evidence:** Agent repo's `codebase-audit` skill (`D:\@Repos\MemorySmith.Agent\.github\skills\codebase-audit\`) is standalone — it doesn't inherit from `task-core-loop`. Base repo skills all build on `task-core-loop` (see §9).
**Severity:** Low

### G-009: Agent Repo Missing Delivery-Loop Skills
**Evidence:** Agent repo has `codebase-audit`, `debug-msa`, `mcp-tools` skills. Base repo has `ci-status-monitor`, `codebase-audit-sprint-planner`, `council`, `pr-review-delivery`, `runtime-parity-audit`, `self-review`, `task-core-loop`, `task-delivery-sprint-loop`, `training-sprint-loop`, `wiki-hygiene-audit`.
**Severity:** Low-Medium

---

## 5. Tooling & SDK Versions

| Tool | Agent Repo | Base Repo |
|---|---|---|
| .NET SDK | `10.0.x` (ci.yml) | `10.0.x` (ci.yml) |
| Node.js | Not used in CI | `22` (CI e2e), `20` (docs-pages) |
| Python | Not used in CI | `3.x` (docs-pages) |
| Test framework | NUnit 4.6.1 | NUnit 4.6.1 |
| .NET SDK version (local) | net10.0 (`TargetFramework` in all .csproj) | net10.0 |
| C# language version | `latest` (via Directory.Build.props) | `latest` (per-project) |
| Nullable | `enable` (all projects) | `enable` (all projects) |
| ImplicitUsings | `enable` (all projects) | `enable` (all projects) |

---

## 6. Test Architecture

### 6.1 MemorySmith.Agent.Tests

**Path:** `D:\@Repos\MemorySmith.Agent\MemorySmith.Agent.Tests\`

**Test file count:** ~49 test files (`.cs` test classes)
**Framework:** NUnit 4.6.1
**Test SDK:** Microsoft.NET.Test.Sdk 18.7.0
**Coverage:** coverlet.collector 10.0.1
**Logger:** GitHubActionsTestLogger 3.0.4 (CI annotations)
**Run settings:** `ci.runsettings` — enables console logger + GitHubActions logger
**References:** `Agent.Core`, `Agent.Construction`, `Agent.Planning`, `Agent.Tools`, `WebUI.Blazor`

**Key test files:**
- Sprint regression tests: `Sprint19Tests.cs` through `Sprint57ExecutionContextTests.cs` (14 sprint test files)
- Goal tests: `BuildGoalTests.cs`, `CraftItemGoalTests.cs`, `GatherWoodGoalTests.cs`, `GenericGatherGoalTests.cs`, `GoalFactoryBuildTests.cs`, `GoalFactoryBuiltInTests.cs`, `GoalFactoryTests.cs`, `SimpleGoalTests.cs`, `SurviveNightGoalTests.cs`, `TaskSequenceGoalTests.cs`
- Planning: `HtnPlannerTests.cs`, `HtnPlannerBuildTests.cs`, `HtnTaskLibraryCraftingTests.cs`, `HtnTaskLibraryExtraTests.cs`
- Chat: `ChatInterpreterTests.cs`, `LlmChatInterpreterTests.cs`
- Tools: `ToolDispatcherTests.cs`, `ToolDispatchTests.cs`, `ToolEngineTests.cs`
- World state: `WorldStateBuilderTests.cs`, `WorldStateProjectorTests.cs`
- Agent: `AgentBackgroundServiceTests.cs`, `ActionPlanTests.cs`
- Knowledge: `KnowledgeResolverTests.cs`
- Supporting: `MockMemoryGateway.cs`, `MockPlanner.cs`, `MockWorldAdapter.cs`, `FakeTimeProvider.cs`

### 6.2 MemorySmith.Tests

**Path:** `D:\@Repos\MemorySmith\MemorySmith.Tests\`

**Test file count:** ~48 test files
**Framework:** NUnit 4.6.1
**Test SDK:** Microsoft.NET.Test.Sdk 18.7.0
**Coverage:** coverlet.collector 10.0.1
**Logger:** GitHubActionsTestLogger 3.0.4
**Run settings:** `ci.runsettings` — identical configuration
**References:** `MemorySmith.Core`, `MemorySmith.Storage`, `MemorySmith.App`
**Also:** `Microsoft.AspNetCore.Mvc.Testing` 10.0.9 (integration testing)

**Key test files:**
- Search: `SearchBenchmarkTests.cs`, `McpAndSemanticSearchTests.cs`, `CodeSearchServiceTests.cs`, `CodeSearchBenchmarkTests.cs`
- Memory: `MemoryApplicationServiceTests.cs`, `MemoryRecordTests.cs`, `FileMemoryStoreTests.cs`, `FileMemoryStoreHardeningTests.cs`, `LiveMemoryRecordValidationTests.cs`
- Chat: `PagesAndChatTests.cs`, `ChatToolCatalogAndInterceptTests.cs`, `ChatToolLoopParityTests.cs`, `ChatModelProfileServiceTests.cs`
- Security: `SecurityAndSourceLinkTests.cs`, `GitHubOAuthCallbackHandlerTests.cs`
- MCP: `McpAndSemanticSearchTests.cs`
- Embedding: `OnnxEmbeddingVectorProjectorTests.cs`, `SemanticEmbeddingPathTests.cs`, `SemanticEmbeddingPrewarmServiceTests.cs`, `CudaEmbeddingBatchBenchmarkTests.cs`
- Tag governance: `TagGovernanceTests.cs`
- Maintenance: `MaintenanceAgentWorkflowTests.cs`, `MemoryMaintenanceTasksTests.cs`, `ConsolidationTaskRulesTests.cs`

### 6.3 ci.runsettings (both repos, identical content)

```xml
<RunSettings>
  <LoggerRunSettings>
    <Loggers>
      <Logger friendlyName="console" enabled="True">
        <Configuration><Verbosity>normal</Verbosity></Configuration>
      </Logger>
      <Logger friendlyName="GitHubActions" enabled="True" />
    </Loggers>
  </LoggerRunSettings>
</RunSettings>
```

---

## 7. Benchmark Project Status

**Path:** `D:\@Repos\MemorySmith\MemorySmith.Benchmarks\`

**Files:**
- `MemorySmith.Benchmarks.csproj` — net10.0, Exe, references BenchmarkDotNet 0.15.8
- `Program.cs` — supports `--smoke` flag for quick validation, otherwise delegates to `BenchmarkSwitcher`
- `SearchBenchmarks.cs` — 6 benchmarks: LexicalSearch, LexicalSearchMetadataWithDiagnostics, SemanticSearch, HybridSearch, ChatContextSearch, ContextPack
- `CodeSearchBenchmarks.cs` — 2 benchmarks: SearchToolQuery, SearchScrewdriverQuery + RunSmokeAsync for relevance scorecard

**Status:** Functional but **not wired into CI**. No benchmark regression gates. Must be run manually.

**Agent Repo:** No benchmark project exists.

---

## 8. Package Vetting & Directory.Build.props

### Agent Repo `Directory.Build.props`

**Path:** `D:\@Repos\MemorySmith.Agent\Directory.Build.props`

```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<WarningsNotAsErrors>NU1903</WarningsNotAsErrors>
```

- `TreatWarningsAsErrors` is enabled — no new warnings allowed
- `NU1903` (NuGet advisory-level) is exempted from the error policy; it remains visible in build output but does not break the build
- ⚠️ No `Directory.Packages.props` (no Central Package Management) in either repo

### Package Vetting Policy

**Path:** `D:\@Repos\MemorySmith.Agent\Data\Pages\policies\package-vetting.md`

Policies P-1 through P-5:
- P-1: Documented justification required for every new package
- P-1a: License whitelist (MIT, Apache-2.0, BSD-2/3-Clause only)
- P-2: Every dependency in `about.html` (verified by `Verify-AboutDeps.ps1`)
- P-3: Vulnerable packages = P0 blocker (`dotnet list package --vulnerable` must return zero)
- P-4: Deprecated packages prohibited
- P-5: Direct pinning of transitive deps requires justification

---

## 9. Skill Architecture Comparison

### MemorySmith.Agent Skills

**Path:** `D:\@Repos\MemorySmith.Agent\.github\skills\`

| Skill | Inherits `task-core-loop`? |
|---|---|
| `codebase-audit` | ❌ Standalone |
| `debug-msa` | ❌ Standalone |
| `mcp-tools` | ❌ Standalone |

### MemorySmith (Base) Skills

**Path:** `D:\@Repos\MemorySmith\.github\skills\`

| Skill | Inherits `task-core-loop`? |
|---|---|
| `ci-status-monitor` | ✅ Yes |
| `codebase-audit-sprint-planner` | ✅ Yes |
| `council` | ✅ Yes |
| `pr-review-delivery` | ✅ Yes |
| `runtime-parity-audit` | ✅ Yes |
| `self-review` | ✅ Yes |
| `task-core-loop` | N/A (base skill) |
| `task-delivery-sprint-loop` | ✅ Yes |
| `training-sprint-loop` | ✅ Yes |
| `wiki-hygiene-audit` | ✅ Yes |

### Agent Config Files

| Repository | Agent Config |
|---|---|
| MemorySmith.Agent | `.github/agents/stevebot.agent.md` — repo-scoped maintenance agent |
| MemorySmith | `.github/agents/smith.agent.md` — primary development agent (Agent Smith) |

---

## 10. Dependabot & CodeQL Status

### Dependabot
- **Neither repo** has a `.github/dependabot.yml` or `.github/dependabot.yaml` file
- No automated dependency update PRs, no automated vulnerability alerting
- **Severity:** High — manual dependency management only

### CodeQL
- **Neither repo** has a CodeQL workflow in `.github/workflows/`
- No automated security analysis, no code-path tracing for CVEs
- **Severity:** High

---

## 11. E2E Test Infrastructure (Base Repo Only)

**Path:** `D:\@Repos\MemorySmith\e2e\`

**Package:** `@playwright/test` ^1.54.2
**Node requirement:** ^22

**Test spec files:**
- `tests/route-smoke.spec.ts` — basic route reachability
- `tests/navigation-freeze.spec.ts` — navigation regression

**Playwright config (`playwright.config.ts`):**
- Base URL: `http://localhost:5089` (from `MEMORYSMITH_BASE_URL` env var)
- `webServer`: auto-starts MemorySmith.App with `--launch-profile http`
- Timeout: 90s per test, 10s per assertion
- Retries: 2x on CI
- Reporting: list + HTML
- Artifacts: trace (first retry), screenshot (failure), video (failure)
- Single browser: Desktop Chrome only
- Fully parallel: false

**Agent Repo:** No e2e test infrastructure.

---

## 12. .editorconfig Status

- **MemorySmith.Agent:** ❌ No `.editorconfig` file
- **MemorySmith:** ❌ No `.editorconfig` file
- **Historical note:** An audit report (`Data/Pages/audits/Audit_20260521_191625.md`, TEST-04 / TOOL-03) flagged the absence of `.editorconfig` as a tooling gap. A hyperagent audit recommended adding `charset = utf-8` (no BOM) rule. Neither has been actioned.
- **Impact:** No automated formatting enforcement, no charset/indent/end-of-line consistency across editors.

---

## 13. Miscellaneous Findings

### 13.1 Backup/Bak Files in Agent Repo Tests
Several `.cs.bak` files remain in `MemorySmith.Agent.Tests/`: `Sprint23Tests.cs.bak`, `Sprint25Tests.cs.bak`, `Sprint26Tests.cs.bak`, `Sprint27Tests.cs.bak`, `Sprint39Tests.cs.bak`, `ToolDispatchTests.cs.bak`, plus `SystemTimeProvider.cs.bak` and `SystemTimeProvider.cs.bak.bak` in `Agent.Core/`. These appear to be outdated backup copies and should be cleaned up.

### 13.2 NuGet Version Discrepancies
Base repo tests reference `Microsoft.NET.Test.Sdk 18.7.0` — this is a very high version number that warrants verification. Agent repo tests have the same reference. Both repos use `NUnit4TestAdapter 6.2.0`.

### 13.3 Browser Test Coverage
- Agent repo: 0 browser tests
- Base repo: 2 e2e spec files (route-smoke, navigation-freeze), run as separate CI jobs
- Base repo e2e artifacts are uploaded with 14-day retention

### 13.4 PowerShell Script Execution Policy
All `.ps1` scripts in both repos use `$ErrorActionPreference = 'Stop'` for deterministic error handling. CI workflows use `shell: pwsh` consistently.

---

## 14. Summary Comparison Table

| Capability | MemorySmith.Agent | MemorySmith (Base) |
|---|---|---|
| CI workflows | 1 (`ci.yml`) | 2 (`ci.yml`, `docs-pages.yml`) |
| CI jobs | 1 (build-and-test) | 3 (build-and-test, browser-route-smoke, browser-navigation-freeze) |
| Coverage collection | ✅ Collected | ✅ Collected |
| Coverage reporting | ❌ Not reported | ✅ reportgenerator + artifact upload |
| Task record validation | ✅ 1 script | ✅ 1 script |
| Memory record validation | ❌ Missing | ✅ `Test-MemoryRecords.ps1` |
| Page link validation | ❌ Missing | ✅ `Test-PageLinks.ps1` |
| Page path literal validation | ❌ Missing | ✅ `Test-PagePathLiterals.ps1` |
| Consolidated validation entrypoint | ❌ Missing | ✅ `Validate-Repo.ps1` |
| .editorconfig | ❌ Missing | ❌ Missing |
| Dependabot | ❌ Missing | ❌ Missing |
| CodeQL | ❌ Missing | ❌ Missing |
| E2E tests | ❌ Missing | ✅ Playwright (2 specs) |
| Benchmark project | ❌ Missing | ✅ `MemorySmith.Benchmarks` (2 benchmarks) |
| Benchmarks in CI | ❌ N/A | ❌ Not wired |
| `dotnet list package --vulnerable` in CI | ❌ Missing | ❌ Missing |
| Package vetting policy | ✅ `Data/Pages/policies/package-vetting.md` | ❌ Not present |
| Central Package Management | ❌ No `Directory.Packages.props` | ❌ No `Directory.Packages.props` |
| Skill count (github/skills) | 3 | 10 |
| Skills inherit task-core-loop | ❌ (0/3) | ✅ (7/7 leaf skills) |
| Agent config files | `stevebot.agent.md` | `smith.agent.md` |
