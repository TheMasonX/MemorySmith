# Sprint 60 Wave C Handoff — 2026-07-11

## Wave B (Complete)

| Task | Status | Summary |
|------|--------|---------|
| TSK-3075 | ✅ Done | Path traversal fix — verified `|\.\.` in SanitizeId regex |
| TSK-3076 | ✅ Done | BenchmarkDotNet 0.15.8→0.14.0 — verified in csproj |
| TSK-3078 | ✅ Done | MemoryScorer weights normalized 1.23→1.00 (0.50/0.25/0.15/0.10); thresholds adjusted proportionally (Working 1.0→0.81, Core 2.0→1.62, Deprecation 0.2→0.16) |
| TSK-3080 | ✅ Done | MemoryIndex thread safety — `ReaderWriterLockSlim` with lock-free `AddCore`/`RemoveCore` to prevent recursion |
| TSK-3082 | ✅ Done | ADMIN SQL literal replaced with parameterized `@normalizedName` + `MemorySmithRoles.Admin` |
| TSK-3083 | ✅ Done | Publisher subscriber isolation — per-subscriber try/catch with `ILogger` logging |
| TSK-3085 | ✅ Done | launchSettings `LocalDevelopment`→`Development` — matches `appsettings.Development.json` |
| TSK-0293 | ✅ Done | TreeSitter `c_sharp`→`CSharp` key fix; Roslyn failure `LogWarning` |
| TSK-0295 | ✅ Done | `TaskStatuses.All`/`TaskPriorities.All` validation sets + `ValidateEnumValue` |
| TSK-0301 | 🗄️ Archived | Superseded by TSK-3080 (keep & fix MemoryIndex, don't delete) |

## Wave C — Scope

**Theme:** Remaining high-ROI Ready tasks — security hardening, search wiring, and cleanup.

### Priority 1 — High (Security & Reliability)

| Task | Title | Key Files | Complexity |
|------|-------|-----------|------------|
| **TSK-0300** | Add total auth self-lockout guardrail | `SecurityServices.cs`, `MemorySmithOptions.cs` | Medium |
| **TSK-3077** | Wire MemoryIndex into search query path | `MemoryApplicationService.cs`, `MemoryIndex.cs` | Medium — unblocked by TSK-3080 |
| **TSK-3079** | Consolidate divergent state promotion paths | `MemoryStateMachine.cs` | Small |
| **TSK-3081** | Decompose SqliteMemorySmithDatabase god class | `SqliteMemorySmithDatabase.cs` | Large |
| **TSK-3084** | Add CSRF/state validation to OAuthBridge | `OAuthBridge.cs` | Medium |
| **TSK-3086** | Fix AdminController duplicate POST routes + rate limiting | `AdminController.cs` | Medium |
| **TSK-3087** | Add demotion/re-promotion paths to MemoryStateMachine | `MemoryStateMachine.cs` | Medium |
| **TSK-3088** | Add JsonPropertyName attributes to all model classes | `Models/*.cs` | Medium |
| **TSK-3089** | Fix fire-and-forget training harness error tracking | `TrainingHarness.cs` | Small |
| **TSK-3090** | Add audit logging + rate limiting to SourceLinksController | `SourceLinksController.cs` | Medium |

### Priority 2 — Medium (Cleanup)

| Task | Title | Key Files | Complexity |
|------|-------|-----------|------------|
| **TSK-0294** | Scrub dead search tool refs from README + wiki guides | `README.md`, `Data/Pages/guides/` | Small |
| **TSK-0296** | Consolidate `FixedTimeEquals` 3 copies → 1 shared helper | Middleware, `SecurityServices.cs` | Small |
| **TSK-0297** | Delete 10 dead methods from ChatServices.cs | `ChatServices.cs` | Small |
| **TSK-0298** | Fix training harness `warmupSteps` default + docstring | `harness.py` | Small (Python) |
| **TSK-0299** | Fix SplitThinking + silent catch + validation clobbering | `ChatServices.cs` | Small |

## Test Baseline

| Metric | Value |
|--------|-------|
| Total tests | 530 |
| Passing | 515 (97.2%) |
| Failing | 6 — 2 pre-existing data issues + 4 from concurrent `MemoryApplicationService.cs` changes |
| Core affected tests | All passing — MemoryMaintenanceTasks 7/7, ConsolidationTaskRules 9/9, PublisherAndStats 2/2, TagGovernance 31/31, MemoryApplicationService 27/27 |

## Risks & Assumptions

- TSK-3077 (Wire MemoryIndex into search) assumes the thread safety fix in TSK-3080 is sufficient — verify with concurrent CRUD + search tests
- TSK-3081 (SqliteMemorySmithDatabase decomposition) is the largest item; consider splitting into sub-tasks
- TSK-0298 is Python-only — requires different tooling than the C# tasks
- TSK-0300 requires verifying that `TryValidateCrossSettingConstraints` is actually called on all write paths

## Quick Wins (estimated <30 min each)

1. **TSK-0294** — grep for `unified_search` / `semantic_search` in docs, replace with `hybrid_search`
2. **TSK-0296** — consolidate `FixedTimeEquals`, delete 3 copies, add tests
3. **TSK-0297** — delete 10 verified-dead methods from ChatServices.cs
4. **TSK-0299** — fix `Regex.Match`→`Matches`, add logger to catch, fix key overwrite
