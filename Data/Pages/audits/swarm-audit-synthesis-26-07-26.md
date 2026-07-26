# 5-Agent Swarm Audit Synthesis

**Generated:** 2026-07-26
**Method:** 5-agent homogeneous swarm (subagent-swarm skill), partitions by alphabetical file grouping
**Audits Reviewed:** 49
**Total Unique Findings:** ~85+ requiring new tasks
**New Tasks Created:** 22 (TSK-0394 through TSK-0416)
**Existing Tasks Extended/Corrected:** ~15

## Executive Summary

A 5-agent swarm reviewed 49 audit documents from `Data/Pages/audits/` spanning 2026-07-11 through 2026-07-26. The audits cover code quality, security, static analysis, delta findings, corpus cross-references, and focused deep-dives.

### Key Numbers

| Metric | Count |
|--------|-------|
| Audits reviewed | 49 |
| New Critical-priority findings | 2 |
| New High-priority findings | 18 |
| New Medium-priority findings | ~40 |
| New Low-priority findings | ~25 |
| Existing tasks needing scope extension | ~15 |
| Tasks created this session | 22 |

### Top Critical Findings

1. **TSK-0394** — OpenAI-compatible provider API key structurally unreachable (env var mismatch + setup/provider read mismatch)
2. **TSK-0395** — Provider/model mismatch in `GetConfiguration` and 503 error paths

### Top High Findings (New Tasks Created)

| Task | Finding | Area |
|------|---------|------|
| TSK-0396 | MaintenanceDiffService uncatchable stack overflow (process crash) | Maintenance |
| TSK-0397 | MemoryIndex read-side unsynchronized (blocks TSK-3077) | Search |
| TSK-0398 | IsLoopback null-IP fail-open across 3 security gates | Auth/Security |
| TSK-0399 | Unguarded last-admin removal via RemoveRole | Admin |
| TSK-0400 | OpenAI provider silently saved as Ollama (data corruption) | Chat/Provider |
| TSK-0401 | SplitThinking bug reintroduced in OpenAICompatibleChatProvider | LLM |
| TSK-0402 | GitHub OAuth first-admin TOCTOU race | Auth/OAuth |
| TSK-0403 | Login failure classification bug (disabled users mislogged) | Auth/Audit |
| TSK-0404 | FindDependencyCycles recursion crash risk (extends TSK-0321) | Maintenance |
| TSK-0405 | RequestMetadata HMAC key corruption silently weakens audit | Security |
| TSK-0406 | TSK-0383 unrequested concurrency regression (sequential subscribers) | Events |
| TSK-0407 | Admin lockout guard checks only enabled, not runtime-usable | Admin |
| TSK-0408 | FileMemoryStore silent corruption skip + narrow SanitizeId | Storage |
| TSK-0409 | IsWeeklyWindow scheduling gap (untasked for 2 months) | Scheduling |
| TSK-0410 | MaintenanceAgentService.RunAsync no run-exclusivity guard | Maintenance |

### Medium Findings (New Tasks Created)

| Task | Finding | Area |
|------|---------|------|
| TSK-0411 | API-key callers blocked from agent session tools | API/Sessions |
| TSK-0412 | ChatToolCatalog.BuildTools CCN 116 - decompose | Tools |
| TSK-0413 | Session cap TOCTOU race (check-then-act) | Sessions |
| TSK-0414 | ApproveAsync crash window (file-changed-before-status) | Maintenance |
| TSK-0415 | McpController 9-dependency god class decomposition | API |
| TSK-0416 | JSON extraction fragile against real LLM output | Maintenance/LLM |

## Cross-Cutting Patterns

### Pattern 1: Silent Fallback / Fail-Open Design (10+ instances)
The codebase consistently prefers "pick a default and keep going" over surfacing invalid input:
- Config normalizers silently coerce unknown strings to defaults
- Non-object JSON collapses to empty settings
- IsLoopback(null) returns `true` (fail-open)
- API-key auth never builds an identity
- HMAC key corruption silently generates fresh key
- File stores silently skip corrupt records
- URL source existence marked `true` without fetching

**Recommendation:** Policy document distinguishing "safe to degrade" vs "must fail closed."

### Pattern 2: "Half-Fixed" Remediation (5 instances)
Fixes resolve the letter of a finding but not its substance:
1. **Scoring weights** — coefficients fixed to sum 1.0, but `References.Count` normalization never done
2. **MemoryIndex locking** — writer lock added, reader lock never
3. **Request-guard exempt paths** — narrower fix landed, broader restructuring didn't
4. **TSK-0289** — OAuth authorization gap fixed, concurrency gap not
5. **TSK-0383** — subscriber fault isolation fixed, concurrency model silently regressed

**Recommendation:** Acceptance criteria should explicitly ask "what adjacent invariants does this fix NOT address?"

### Pattern 3: Duplicated Security/Boundary Logic (5+ instances)
- Path-containment security checks duplicated across 3+ services
- File-store persistence policy duplicated 3x
- Self-lockout guards check inconsistent sources of truth
- Provider equivalence rules duplicated across controllers, services, UI

### Pattern 4: Thread-Safety / Concurrency Gaps (4 instances)
- MemoryIndex: no read locking (F2)
- Session cap: check-then-act (F48)
- First-admin race: TOCTOU (F36)
- ApproveAsync: crash window (F54)

### Pattern 5: God Class / Decomposition Debt (5+ instances)
Growing faster than resolved — each new feature area adds another:
- `MaintenanceAgentServices.cs` (2,187 lines) — TSK-0043
- `ChatToolCatalog.cs` (1,603 lines) — TSK-0192
- `CodeSearchService.cs` (3,116 lines) — not yet tracked
- `McpController.cs` (394 lines, 9 deps) — NEW (TSK-0415)
- `SqliteMemorySmithDatabase.cs` (1,455 lines) — TSK-0157

**Recommendation:** Gate: any service >500 lines or >5 unrelated dependencies triggers decomposition review.

### Pattern 6: Untested Deliberately-Testable Code (3 instances)
Code structured for testability (`internal static` + `InternalsVisibleTo`) but with zero tests:
- Path-validation helpers in `MemorySmithContentEndpoints.cs`
- `AutoValidateAntiforgeryTokenFilter` (TSK-0039, shipped, zero tests)
- `IMemoryChangePublisher` concurrency change (TSK-0383, zero tests)

### Pattern 7: Stale Audit Numbering Convention
Several older documents reference `TSK-30xx` numbers that don't exist. `TSK-3048` and `TSK-3090` have no equivalents. Any finding only referencing a 30xx-style task should be treated as untracked unless independently verified.

## Top Recommendations for Sprint Plan

### Immediate (Current Sprint)
1. **TSK-0394** — Fix OpenAI-compatible provider API key (Critical)
2. **TSK-0395** — Fix provider/model mismatch in GetConfiguration (Critical)
3. **TSK-0399** — Fix unguarded last-admin removal (High)
4. **TSK-0400** — Fix OpenAI→Ollama data corruption (High)
5. **TSK-0397** — Fix MemoryIndex read locking (High, blocks TSK-3077)
6. **TSK-0409** — Fix IsWeeklyWindow scheduling gap (High, 2 months untasked)

### Next Sprint
7. **TSK-0396** — Fix MaintenanceDiffService crash risk (High)
8. **TSK-0401** — Fix SplitThinking bug reintroduced (High)
9. **TSK-0402** — Fix first-admin TOCTOU race (High)
10. **TSK-0405** — Fix HMAC key silent corruption (High)
11. **TSK-0410** — Fix maintenance run exclusivity (High)

### Backlog
12. Remaining 11 new Medium/Low tasks
13. 15 existing tasks needing scope extension (TSK-0283, TSK-0350, TSK-0290, etc.)
14. Policy documents: fail-open policy, "half-fixed" verification checklist

## Audits Reviewed

### Agent 1 (Authentication, Security, Chat)
- `memorysmith-agentsession-apikey-audit-26-07-18-04-32-03.md`
- `memorysmith-agentsession-audit-26-07-18-01-55-45.md`
- `memorysmith-approveasync-audit-26-07-19-01-19-01.md`
- `memorysmith-audit-26-07-19-17-55-14.md`
- `memorysmith-audit-26-07-21-08-29-51.md`
- `memorysmith-audit-26-07-23-07-28-25.md`
- `memorysmith-auditcorpus-discovery-26-07-19-22-54-17.md`
- `memorysmith-chatmodelprofile-audit-26-07-17-19-52-23.md`
- `memorysmith-chatservices-extraction-audit-26-07-15-20-36-11.md`
- `memorysmith-cleanup-programcs-audit-26-07-18-13-50-06.md`

### Agent 2 (Code Quality, Duplication, Corpus)
- `memorysmith-code-dupe-refactor-audit-7-14-26.md`
- `memorysmith-code-dupe-static-analysis-audit-7-14-26.md`
- `memorysmith-codebase-audit-26-07-11-03-39-33.md`
- `memorysmith-codesearch-audit-26-07-13-04-51-34.md`
- `memorysmith-corpus-crossref-audit-26-07-20-18-17-58.md`
- `memorysmith-corpus-sweep2-audit-26-07-21-23-47-26.md`
- `memorysmith-corpus-sweep3-audit-26-07-23-20-48-48.md`
- `memorysmith-corpus-sweep4-audit-26-07-24-02-08-54.md`
- `memorysmith-delta-audit-26-07-11-07-55-39.md`
- `memorysmith-delta-audit-26-07-13-17-08-48.md`

### Agent 3 (Delta Audits, Mid-July)
- `memorysmith-delta-audit-26-07-13-other.md`
- `memorysmith-delta-audit-26-07-14-17-54-42.md`
- `memorysmith-delta-audit-26-07-18-08-50-04.md`
- `memorysmith-delta-audit-26-07-18-20-33-41.md`
- `memorysmith-delta-audit-26-07-18-next-slice.md`
- `memorysmith-delta-audit-next-slice-26-07-18-20-46-20.md`
- `memorysmith-delta2-audit-26-07-11-12-57-48.md`
- `memorysmith-delta3-audit-26-07-11-13-33-06.md`
- `memorysmith-delta4-audit-26-07-11-21-38-26.md`
- `memorysmith-delta5-audit-26-07-13-12-54-40.md`

### Agent 4 (Delta6, DiffService, Focused Audits)
- `memorysmith-delta6-audit-26-07-13-22-00-24.md`
- `memorysmith-diffservice-audit-26-07-19-01-29-01.md`
- `memorysmith-duplication-audit-26-07-14-01-59-53.md`
- `memorysmith-isloopback-audit-26-07-16-20-02-55.md`
- `memorysmith-jsonextraction-audit-26-07-19-22-27-48.md`
- `memorysmith-lastadmin-audit-26-07-19-01-09-06.md`
- `memorysmith-maintenancerun-audit-26-07-19-01-24-26.md`
- `memorysmith-packagebloat-audit-26-07-17-19-48-24.md`
- `memorysmith-ratelimiter-audit-26-07-18-18-02-51.md`
- `memorysmith-remediation-plan-26-07-12-15-35-28.md`

### Agent 5 (Sprint 60, Standards, Static Analysis, TSK-0346)
- `memorysmith-sprint60-wavec-audit-26-07-11-08-46-00.md`
- `memorysmith-standards-spec-review-26-07-15-11-56-27.md`
- `memorysmith-staticanalysis-audit-26-07-14-22-54-08.md`
- `memorysmith-staticanalysis2-audit-26-07-14-23-38-09.md`
- `memorysmith-taggovernance-audit-26-07-17-03-05-00.md`
- `memorysmith-topicmap-exhaustive-audit-26-07-19-11-50-25.md`
- `memorysmith-tsk0346-complete-26-07-22-18-44-19.md`
- `memorysmith-tsk0346-verification-26-07-22-06-02-22.md`
- `tool-catalog-duplication-audit-26-07-26-10-41-00.md`

## Tasks Created This Session

| Key | Title | Priority | Type |
|-----|-------|----------|------|
| TSK-0394 | Fix OpenAI-compatible provider API key unreachable | Critical | Bug |
| TSK-0395 | Fix Provider/model mismatch in GetConfiguration and 503 error paths | Critical | Bug |
| TSK-0396 | Fix MaintenanceDiffService unbounded diff + crash risk | High | Bug |
| TSK-0397 | Fix MemoryIndex read-side locking before TSK-3077 | High | Bug |
| TSK-0398 | Re-scope TSK-0350: Fix IsLoopback null-IP fail-open | High | Bug |
| TSK-0399 | Fix unguarded last-admin removal via RemoveRole | High | Bug |
| TSK-0400 | Fix OpenAI provider silently saved as Ollama | High | Bug |
| TSK-0401 | Fix SplitThinking bug reintroduced in OpenAICompatibleChatProvider | High | Bug |
| TSK-0402 | Fix GitHub OAuth first-admin TOCTOU race | High | Bug |
| TSK-0403 | Fix login failure classification bug | High | Bug |
| TSK-0404 | Fix FindDependencyCycles recursion crash risk | High | Bug |
| TSK-0405 | Fix RequestMetadata HMAC key corruption silently weakens audit | High | Bug |
| TSK-0406 | Fix TSK-0383 unrequested concurrency regression | High | Bug |
| TSK-0407 | Fix admin lockout guard: check runtime usability | High | Bug |
| TSK-0408 | Fix FileMemoryStore silent corruption skip | High | Bug |
| TSK-0409 | Fix IsWeeklyWindow scheduling gap | High | Bug |
| TSK-0410 | Fix MaintenanceAgentService.RunAsync no run-exclusivity guard | High | Bug |
| TSK-0411 | Fix API-key callers blocked from agent session tools | Medium | Bug |
| TSK-0412 | Fix ChatToolCatalog.BuildTools CCN 116 | Medium | Refactor |
| TSK-0413 | Fix Session cap TOCTOU race | Medium | Bug |
| TSK-0414 | Fix ApproveAsync crash window | Medium | Bug |
| TSK-0415 | Fix McpController 9-dependency god class | Medium | Refactor |
| TSK-0416 | Fix JSON extraction fragile against real LLM output | Medium | Bug |

## Existing Tasks Needing Scope Extension

| Task | Current Status | Extension Needed |
|------|---------------|------------------|
| TSK-0283 | Backlog | Broaden to cover GetConfiguration model resolution, error-path provider strings, order-dependent fallback, hard-coded whitelist, UI-side equivalence |
| TSK-0350 | Backlog | Re-scope from null-HttpContext to cover null-IPAddress; raise priority from Medium to High |
| TSK-0290 | Done | Incomplete — null-address fallback recreates shared-bucket bug; needs ForwardedHeaders or Connection.Id fallback |
| TSK-0046 | Archived | Re-open with concrete evidence; 4 unused packages confirmed, not 1 |
| TSK-0300 | Backlog | Extend to cover runtime provider availability (not just IsEnabled) |
| TSK-0321 | Backlog | Extend scope to cover recursion crash risk (not just performance) |
| TSK-0383 | Done | Revert concurrency model regression; add test |
| TSK-0364 | InProgress | Add invariant test for assign-then-override fragility |
| TSK-0039 | Backlog | Update status (filter exists); write missing tests |
| TSK-0345 | Backlog | Update description (remove resolved `/api/admin/setup/status` clause) |
| TSK-0276 | Backlog | Severity correction (MaxNestingDepth); add startup diagnostic |
| TSK-0192 | Backlog | Tool search readers + task schema duplication fold-in |

## Tags

`swarm-audit`, `audit-synthesis`, `5-agent-swarm`, `2026-07-26`, `new-tasks`, `task-creation`, `sprint-planning`, `security-audit`, `code-quality`, `static-analysis`