# Sprint 60 Wave C Handoff — 2026-07-11 (Updated)

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

## Wave C — Status

**Theme:** Remaining high-ROI Ready tasks — security hardening, search wiring, and cleanup.

### ✅ Completed in this wave

| Task (current key) | Title | Evidence |
|------|-------|----------|
| **TSK-0297** | Delete 10 dead methods from ChatServices.cs | 10 methods + 6 regex fields removed. Build 0 errors, 526 tests pass. Commit 6281037 |
| **TSK-0300** | Add total auth self-lockout guardrail | Extended `TryValidateCrossSettingConstraints` + pre-toggle guard in `SetProviderEnabled`. Commit 6281037 |
| **TSK-0386** (was 3086) | Fix AdminController duplicate POST routes + rate limiting | Merged duplicate setup routes, added catch-all for missing Content-Type, `[EnableRateLimiting("login")]` on all setup endpoints. Commit 6281037 |
| **TSK-0387** (was 3087) | Add demotion/re-promotion paths to MemoryStateMachine | Core→Working demotion, Deprecated→Working re-promotion. 7 state transition tests pass. Commit 6281037 |
| **TSK-0390** (was 3090) | Add audit logging + rate limiting to SourceLinksController | AuditLogService injection + audit on Open endpoint + rate limiting. Commit 6281037 |
| **TSK-0364** | Add Unconsolidated guard to prevent instant deprecation | New records no longer instantly deprecated on first triage. Guard added to MemoryStateMachine.Evaluate. Pending commit |

### Priority 1 — High (Remaining)

| Task | Title | Key Files | Complexity |
|------|-------|-----------|------------|
| **TSK-0377** (was 3077) | Wire MemoryIndex into search query path | `MemoryApplicationService.cs`, `MemoryIndex.cs` | Medium — unblocked by TSK-0380 |
| **TSK-0381** (was 3081) | Decompose SqliteMemorySmithDatabase god class | `SqliteMemorySmithDatabase.cs` | Large |
| **TSK-0384** (was 3084) | Add CSRF/state validation to OAuthBridge | `OAuthBridge.cs` | Medium |
| **TSK-0388** (was 3088) | Add JsonPropertyName attributes to all model classes | `Models/*.cs` | Medium |
| **TSK-0391** (was 3091) | Harden file-backed persistence and transcript/session recovery | FileEventStore, FileMemoryStore, ChatTranscriptWriter, AgentSessionService | Medium |

### Priority 2 — Medium (Remaining)

| Task | Title | Key Files | Complexity |
|------|-------|-----------|------------|
| **TSK-0294** | Scrub dead search tool refs from README + wiki guides | `README.md`, `Data/Pages/guides/` | Small |
| **TSK-0296** | Consolidate `FixedTimeEquals` 3 copies → 1 shared helper | Middleware, `SecurityServices.cs` | Small |
| **TSK-0298** | Fix training harness `warmupSteps` default + docstring | `harness.py` | Small (Python) |
| **TSK-0299** | Fix SplitThinking + silent catch + validation clobbering | `ChatServices.cs` | Small |

## Test Baseline

| Metric | Value |
|--------|-------|
| Total tests | 533 |
| Passing | 526 (98.7%) |
| Failing | 1 pre-existing (`RepositoryMemoryFiles_PassApplicationValidationContract` — file rename migration issue) |
| Core affected tests | All passing — StateTransition 7/7, MemoryMaintenanceTasks 7/7, ConsolidationTaskRules 9/9, TagGovernance 31/31 |

## Risks & Assumptions

- TSK-0377 (Wire MemoryIndex into search) assumes the thread safety fix in TSK-0380 is sufficient
- TSK-0381 (SqliteMemorySmithDatabase decomposition) is the largest item; consider splitting into sub-tasks
- TSK-0298 is Python-only — requires different tooling than the C# tasks
- Task keys updated to normalized TSK-NNNN format (3000-series → 03xx per commit 8b239d9)
- 1 pre-existing test failure unrelated to Wave C changes

## Quick Wins Remaining (estimated <30 min each)

1. **TSK-0294** — grep for `unified_search` / `semantic_search` in docs, replace with `hybrid_search`
2. **TSK-0296** — consolidate `FixedTimeEquals`, delete 3 copies, add tests
3. **TSK-0299** — fix `Regex.Match`→`Matches`, add logger to catch, fix key overwrite
