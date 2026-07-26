# Swarm Agent 4 — Audit Findings Extraction Report
**Partition:** Delta6, DiffService, Duplication, IsLoopback, JsonExtraction, LastAdmin, MaintenanceRun, PackageBloat, RateLimiter, Remediation Plan
**Generated:** 2026-07-26
**Agent:** Swarm Agent 4 of 5

---

## Summary

| Metric | Count |
|--------|-------|
| Audits reviewed | 10 |
| Total unique findings extracted | 13 (F29, F30, F31, F32, F33, F43, F45, F46, F52, F53, F55, F56, F58) |
| Plus 1 consolidated remediation plan (W1-W6 workstreams) | Clusters F1-F22 into 6 workstreams |
| Findings already covered by existing tasks | 4 — F30 (partial, TSK-0045), F43 (extends TSK-0350), F46 (extends archived TSK-0046), F52 (corrects TSK-0290) |
| NEW findings needing task creation | 9 — F29, F31, F32, F33, F45, F53, F55, F56, F58 |
| Remediation plan workstreams | 6 (W1-W6), each references existing tasks |

---

## By-Audit Findings

---

### Audit 1: `memorysmith-delta6-audit-26-07-13-22-00-24.md`

- **Date:** 2026-07-13
- **Topic:** Deep-dive into ChatToolCatalog.cs tool-handler bodies; resource-exhaustion gap in `memorysmith_source_bundle`
- **Key Findings:**
  1. **F29** — `memorysmith_source_bundle`'s `limit` parameter is unclamped (unlike 3 sibling tools that all cap at 50-100), and its `ids` parameter has no count cap. Combined with per-source-link `maxFileBytes` up to 1MB, a single MCP call can read unbounded content (90% confidence, **Medium** severity).
     - **Files:** `MemorySmith.App/Services/ChatToolCatalog.cs` lines ~222-261, ~230-243
     - **Existing task ref:** None
     - **Recommendation:** Add `Math.Clamp(ReadInt(args, "limit", 10), 1, 50)` and `.Take(50)` on `ids.Split(...)`. Consider aggregate byte budget for the whole call.
- **Already Covered?** **No** — no existing task covers input-bounds auditing across tool handlers
- **Cross-references:** None to other audits in partition

---

### Audit 2: `memorysmith-diffservice-audit-26-07-19-01-29-01.md`

- **Date:** 2026-07-19
- **Topic:** Unbounded O(n·m) LCS table + uncatchable recursive crash risk in `MaintenanceDiffService`
- **Key Findings:**
  1. **F56** — `MaintenanceDiffService.BuildUnifiedDiff` allocates `O(before.Length × after.Length)` memory with no size cap, and its `AppendDiff` backtrack is recursive with no depth limit. A `StackOverflowException` in .NET is **uncatchable** and terminates the entire process (85% confidence, **High** severity).
     - **Files:** `MemorySmith.App/Services/MaintenanceAgentServices.cs`, `MaintenanceDiffService`, lines 409-466
     - **Call site:** `FileMaintenanceProposalStore.SaveAsync` line 598 — runs on every proposal save unconditionally
     - **Existing task ref:** TSK-0043 (general decomposition of this file) mentioned but doesn't cover this
     - **Recommendation:** (1) Add line-count size guard before attempting LCS diff; (2) Convert recursive `AppendDiff` to iterative loop with explicit stack
- **Already Covered?** **No** — TSK-0043 tracks decomposition but not this specific crash risk
- **Cross-references:** F55 (same file, same engagement's preceding finding)

---

### Audit 3: `memorysmith-duplication-audit-26-07-14-01-59-53.md`

- **Date:** 2026-07-13
- **Topic:** jscpd-based code duplication analysis with manual semantic verification (156 production-code clone pairs)
- **Key Findings:**
  1. **F30** — 7-8 mutation methods in `TaskDomainService.cs` repeat an identical `ThrowIfCancellationRequested → lock(_gate) → FindByIdOrKey → null-check` preamble inside thread-safety-critical `lock` blocks (100% confidence, **Medium** severity).
     - **Files:** `TaskDomainService.cs` lines 449-460, 495-506, 525-536, 558-569, 603-614, 642-653, 679-690
     - **Existing task ref:** Complements TSK-0045 (TaskDomainService layering split)
     - **Recommendation:** Extract shared `WithLockedTask` helper method
  2. **F31** — 8 action methods in `TasksController.cs` repeat identical `try { ... return updated is null ? NotFound() : Ok(updated); } catch (ArgumentException ex) { return BadRequest(ex.Message); }` wrapper (100% confidence, **Low-Medium** severity).
     - **Files:** `TasksController.cs` lines 70-208
     - **Existing task ref:** None
     - **Recommendation:** Replace with ASP.NET Core `ArgumentExceptionToBadRequestFilter` exception filter
  3. **F32** — `ResolveDataDeploymentRoot`/`NormalizeDataRelativePath` implemented verbatim in 3 separate classes across 2 files: `SemanticEmbeddingSearchService`, `OnnxTextEmbeddingProvider` (same file), and `CodeSearchService` (different file). Same architectural anti-pattern as F19 — path-resolution reinvented per-class (100% confidence, **Medium** severity).
     - **Files:** `SemanticEmbeddingSearchService.cs` lines 433-460 and 844-871; `CodeSearchService.cs` lines 2806-2844
     - **Existing task ref:** None
     - **Recommendation:** Move both methods into `MemorySmithConfigurationPaths.cs` as shared statics
  4. **F33** — Batch chunk-insert SQL parameter-binding boilerplate duplicated between `CodeSearchService` main build path (plain `INSERT`) and shard-merge path (`INSERT OR IGNORE`). The two copies have one real behavioral difference that a careless merge would erase (95% confidence, **Low-Medium** severity).
     - **Files:** `CodeSearchService.cs` lines 1541-1585 and 1867-1908
     - **Existing task ref:** None
     - **Recommendation:** Extract shared `InsertChunksAsync` with `bool ignoreDuplicates` parameter
- **Already Covered?** F30: **Partial** (complements TSK-0045). F31/F32/F33: **No**
- **Cross-references:** F32 notes same architectural pattern as F19 (from earlier audit series). Note on `MemoryApplicationService.cs:1419-1443` being a jscpd false positive.

---

### Audit 4: `memorysmith-isloopback-audit-26-07-16-20-02-55.md`

- **Date:** 2026-07-16
- **Topic:** Shared `IsLoopback` helper returns `true` when `address is null`, causing 3 security gates to fail open simultaneously
- **Key Findings:**
  1. **F43** — `MemorySmithRequestGuardMiddleware.IsLoopback(IPAddress? address)` returns `true` when `address is null`. Three independent security gates inherit this default: the general remote-API lockdown middleware, `BootstrapGate`'s first-admin authorization, and `SecurityServices.IsLoopbackRequest` (which gates `OpenLocalEditorCompatibility`, which defaults to `true`). A null `RemoteIpAddress` — realistic behind misconfigured reverse proxies — causes all three to fail open (90% confidence, **High** severity).
     - **Files:** `MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs` lines 55-68; `BootstrapGate.cs:27`; `SecurityServices.cs:319-323`; `MemorySmithOptions.cs:145`
     - **Existing task ref:** TSK-0350 (Backlog, Medium) — but framed as test-context problem only, misses the production-reachable null-IPAddress variant
     - **Recommendation:** Change default from fail-open to fail-closed: `address is null → false`. Re-scope TSK-0350 to cover both null-`HttpContext` and null-`IPAddress`. Add tests for all 3 call sites.
- **Already Covered?** **Partial** — TSK-0350 captures null-HttpContext variant but misses the broader production-reachable null-IPAddress path. The audit explicitly says this **corrects** TSK-0350 (was under-scoped)
- **Cross-references:** Cross-referenced with F52 (same `ForwardedHeaders` absence root cause, different failure mode)

---

### Audit 5: `memorysmith-jsonextraction-audit-26-07-19-22-27-48.md`

- **Date:** 2026-07-19
- **Topic:** `ExtractJsonObjectPayload` uses naive brace-matching that breaks on common LLM output shapes
- **Key Findings:**
  1. **F58** — `ExtractJsonObjectPayload` uses (1) code-fence detection that only triggers if fence is the literal first 3 characters, and (2) "first `{` to last `}`" substring extraction with no brace-depth or string-literal awareness. Both consumers (`ParseTaskOutput`, `ParseProposalReview`) catch `JsonException` and fall back silently, discarding real LLM review output (85% confidence, **Medium-High** severity).
     - **Files:** `MemorySmith.App/Services/MaintenanceAgentServices.cs`, `ExtractJsonObjectPayload`, lines 1856-1877
     - **Existing task ref:** None — confirmed this exact pattern is not duplicated elsewhere
     - **Recommendation:** (1) Replace `StartsWith("```")` with regex that finds fenced block anywhere; (2) Replace brace-matching with proper depth counter that tracks string-literals
- **Already Covered?** **No**
- **Cross-references:** None in partition

---

### Audit 6: `memorysmith-lastadmin-audit-26-07-19-01-09-06.md`

- **Date:** 2026-07-19
- **Topic:** Unguarded last-admin role removal via `DELETE /api/admin/users/{userId}/roles/{roleName}`
- **Key Findings:**
  1. **F53** — `AdminController.RemoveRole` has zero protection against removing `Admin` from the last remaining admin. Confirmed unguarded at both controller (`AdminController.cs:121-133`) and SQLite storage layer (`SqliteMemorySmithDatabase.RemoveRoleAsync` lines 294-306, unconditional `DELETE`). Sibling `SetProviderEnabled` in the same controller has an explicit self-lockout guard for the analogous concern (92% confidence, **High** severity).
     - **Files:** `MemorySmith.App/Controllers/AdminController.cs` lines 121-133; `SqliteMemorySmithDatabase.cs` lines 294-306
     - **Existing task ref:** None
     - **Recommendation:** Add guard checking `adminCount <= 1` before removing Admin role. Implement as single transaction (not check-then-act) per established pattern from this engagement's F36/F48 findings. Add concurrency test.
- **Already Covered?** **No**
- **Cross-references:** References F11, F36, F48 for concurrency patterns. References F36 for the OAuth bootstrap race.

---

### Audit 7: `memorysmith-maintenancerun-audit-26-07-19-01-24-26.md`

- **Date:** 2026-07-19
- **Topic:** `MaintenanceAgentService.RunAsync` has no run-exclusivity guard
- **Key Findings:**
  1. **F55** — `MaintenanceAgentService.RunAsync` has no check for an already-in-progress run. `MaintenanceActiveRunStore.Begin` unconditionally overwrites its single slot. Two independent entry points (HTTP endpoint + background scheduled trigger) share no coordination. Concurrent runs cause overwritten status snapshots, an inaccurate `GetActiveRun()` status, and potential topic-map cache file races (90% confidence, **Medium-High** severity).
     - **Files:** `MemorySmith.App/Services/MaintenanceAgentServices.cs` lines 1475+, `MaintenanceActiveRunStore` lines 37-71
     - **Entry points:** `MaintenanceAgentController.cs` (unguarded `[HttpPost]`), `MaintenanceAgentServices.cs:2106` (background hosted service)
     - **Existing task ref:** TSK-0043 (general decomposition) doesn't mention this
     - **Recommendation:** Make `Begin` enforce exclusivity (return `null` if `_current` is non-null), have `RunAsync` return `Skipped` result (reuses existing pattern)
- **Already Covered?** **No**
- **Cross-references:** F56 (same file, same engagement)

---

### Audit 8: `memorysmith-packagebloat-audit-26-07-17-19-48-24.md`

- **Date:** 2026-07-17
- **Topic:** Package bloat and supply-chain risk via dependency analysis
- **Key Findings:**
  1. **F45** — `Lucene.Net` + `Lucene.Net.Analysis.Common` + `Lucene.Net.Highlighter` (all `4.8.0-beta00017`) pulled in for exactly two utility classes: `StandardAnalyzer` (tokenizer) and `Highlighter`. No `IndexWriter`, `IndexSearcher`, or actual Lucene index is created anywhere — the entire search engine half is unused while the project hand-rolls search logic directly against SQLite (80% confidence, **Medium** severity).
     - **Files:** `MemorySmith.App/MemorySmith.App.csproj`; usage in `MemoryApplicationService.cs` only
     - **Existing task ref:** None
     - **Recommendation:** Not urgent, but consider hand-rolling small tokenizer/highlighter or using purpose-built package. Flag for backlog.
  2. **F46** — `Nerdbank.MessagePack` (1.2.30), `MessagePack` (3.1.7), `MessagePack.Annotations` (3.1.7), and `Newtonsoft.Json` (13.0.4) — 4 package references with **confirmed zero usage** in production code (95% confidence, **Medium** severity).
     - **Files:** `MemorySmith.App/MemorySmith.App.csproj`
     - **Existing task ref:** TSK-0046 (Archived) — named only `Nerdbank.MessagePack`; this finding provides the concrete evidence it needed and surfaces 3 additional unused packages
     - **Recommendation:** Revisit TSK-0046 archive decision. Delete all 4 `PackageReference` entries. Check `Newtonsoft.Json` isn't needed as transitive dependency of Swashbuckle before removing.
- **Already Covered?** F45: **No**. F46: **Partial** — extends archived TSK-0046 with concrete evidence
- **Cross-references:** None in partition

---

### Audit 9: `memorysmith-ratelimiter-audit-26-07-18-18-02-51.md`

- **Date:** 2026-07-18
- **Topic:** Login rate-limiter null-address fallback reintroduces the exact bug TSK-0290 was written to fix
- **Key Findings:**
  1. **F52** — The login rate-limiter's partition key (`MemorySmithSecuritySetup.cs:81`) falls back to `"unknown"` when `RemoteIpAddress` is null, reproducing the identical "shared bucket, one mistake locks out everyone" mechanism that TSK-0290 (Done, Critical) was specifically filed to eliminate. Confirmed `ForwardedHeaders` is never configured anywhere in the codebase, so null-address is a realistic condition for any reverse-proxy deployment (90% confidence, **High** severity).
     - **Files:** `MemorySmith.App/Hosting/MemorySmithSecuritySetup.cs` lines 79-88
     - **Existing task ref:** TSK-0290 (Done, Critical) — this finding shows its fix is incomplete
     - **Recommendation:** (1) Configure `ForwardedHeaders` properly (foundational); or (2) Fall back to `httpContext.Connection.Id` instead of literal `"unknown"` (narrower fix). Add missing test case for null-IP-address partition isolation.
- **Already Covered?** **Partial** — corrects TSK-0290. The fix shipped is real for normal cases but leaves a scoped recurrence
- **Cross-references:** F43 (same root cause — null `RemoteIpAddress` under unconfigured `ForwardedHeaders`, manifests as fail-locked vs fail-open respectively)

---

### Audit 10: `memorysmith-remediation-plan-26-07-12-15-35-28.md`

- **Date:** 2026-07-12
- **Topic:** Consolidated remediation plan clustering F1-F22 into 6 workstreams (W1-W6)
- **Key Findings (Workstreams):**
  1. **W1 — Auth/Security Consolidation (P0):** Closes F4, F11, F19. Tasks: W1.1 PathSecurity.IsUnderRoot, W1.2 SignInMethodResolver.GetEffectiveState, W1.3 OAuthBridgeController hardening. Est. 3-5 eng-days.
  2. **W2 — Config-Surface Trust Sweep (P1):** Closes F21, F22. Tasks: W2.1 MaxNestingDepth startup warning, W2.2 MaxParallelOpenAIRequests implement or delete, W2.3 SettingsOverridePath binding cleanup, W2.4 Full options sweep. Est. 2-3 eng-days + 1 day/future-finding.
  3. **W3 — Wire MemoryIndex into Search (P1):** Closes F2, F3. Tasks: W3.1 MemoryIndex locking, W3.2 ByConflict index, W3.3 Replace O(N) backlink scans, W3.4 Wire into search query path. Est. 3-4 eng-days.
  4. **W4 — Scoring & State-Machine Correctness (P2):** Closes F1, F10, F17. Tasks: W4.1 Fix reference-count dominance, W4.2 Fix Deprecated-recovery dead zone, W4.3 Clean up TSK-0364 override pattern. Est. 1-2 eng-days.
  5. **W5 — God-Class Decomposition (P2):** Closes F12, F15. Tasks: W5.1 Decompose SqliteMemorySmithDatabase (TSK-3081), W5.2 Re-sequence other decompositions by size. Est. 8-12 eng-days across 4 files.
  6. **W6 — Process Hardening (P3):** Closes F18, F9, F16, F5. Tasks: W6.1 Loosen rank assertions, W6.2 No silent skips norm, W6.3 BOM cleanup, W6.4 Document SyncRelationships asymmetry. Est. 1-2 eng-days.
- **Existing Tasks Referenced:** TSK-0042, TSK-0043, TSK-0045, TSK-0191, TSK-0192, TSK-0276, TSK-0285, TSK-0290, TSK-0300, TSK-0316, TSK-3077, TSK-3081, TSK-0344, TSK-0350, TSK-0351, TSK-0352, TSK-0364
- **Already Covered?** This is a planning document, not a findings document. All workstreams reference existing or newly-proposed tasks. The plan itself is actionable as-is.
- **Cross-references:** This is the synthesis plan that was created *before* the audits in my partition (dates are older: July 12 vs July 13-19). It covers F1-F22 from earlier audit waves; the findings in my partition (F29-F58) are all newer than this plan.

---

## New Findings (Not Yet Tasked)

### F-AG4-01: `memorysmith_source_bundle` unclamped `limit`/`ids` (F29)
- **Title:** Unbounded input parameters on `memorysmith_source_bundle` MCP tool
- **Description:** Three sibling tools in `ChatToolCatalog.cs` clamp `limit` to 50-100; this one doesn't. The `ids` parameter has no count cap. Combined with per-source-link `maxFileBytes` up to 1MB, a single call can read unbounded file content.
- **Severity:** Medium
- **Source Audit:** `memorysmith-delta6-audit-26-07-13-22-00-24.md`
- **Recommendation:** Add `Math.Clamp(ReadInt(args, "limit", 10), 1, 50)` on query path and `.Take(50)` on `ids.Split(...)` path.
- **Suggested Priority:** Medium

### F-AG4-02: `MaintenanceDiffService` uncatchable stack overflow (F56)
- **Title:** Unbounded O(n·m) LCS diff + recursive backtrack can crash entire process
- **Description:** `MaintenanceDiffService.BuildUnifiedDiff` has no size cap on its DP table (O(n·m) memory) and uses unbounded recursion for backtrack. `StackOverflowException` in .NET is uncatchable and terminates the process. Runs on every proposal save with no upstream content size cap.
- **Severity:** High
- **Source Audit:** `memorysmith-diffservice-audit-26-07-19-01-29-01.md`
- **Recommendation:** (1) Add line-count size guard before LCS; (2) Convert recursive `AppendDiff` to iterative loop with explicit stack. Half-day effort.
- **Suggested Priority:** High

### F-AG4-03: `TasksController.cs` repeated try/catch wrapper (F31)
- **Title:** 8 action methods duplicate identical exception-to-400 wrapper
- **Description:** 8 methods in `TasksController.cs` repeat `try { ... return NotFound()/Ok(); } catch (ArgumentException ex) { return BadRequest(ex.Message); }`. Textbook exception-filter candidate.
- **Severity:** Low-Medium
- **Source Audit:** `memorysmith-duplication-audit-26-07-14-01-59-53.md`
- **Recommendation:** Extract `ArgumentExceptionToBadRequestFilter` exception filter. 2-3 hours.
- **Suggested Priority:** Low

### F-AG4-04: Data-root path resolution duplicated 3x (F32)
- **Title:** `ResolveDataDeploymentRoot`/`NormalizeDataRelativePath` duplicated in 3 classes across 2 files
- **Description:** Same path-resolution logic verbatim in `SemanticEmbeddingSearchService`, `OnnxTextEmbeddingProvider`, and `CodeSearchService`. Same architectural anti-pattern as F19. `MemorySmithConfigurationPaths.cs` already exists as natural home but doesn't have this logic.
- **Severity:** Medium
- **Source Audit:** `memorysmith-duplication-audit-26-07-14-01-59-53.md`
- **Recommendation:** Move both methods into `MemorySmithConfigurationPaths.cs` as shared statics. 2 hours.
- **Suggested Priority:** Medium

### F-AG4-05: Chunk-insert SQL duplication with behavioral difference (F33)
- **Title:** SQL chunk-insert boilerplate duplicated with `INSERT` vs `INSERT OR IGNORE` distinction
- **Description:** 14-parameter SQL binding loop duplicated between `CodeSearchService` build path (plain `INSERT`) and merge path (`INSERT OR IGNORE`). A careless consolidation would silently erase the behavioral difference.
- **Severity:** Low-Medium
- **Source Audit:** `memorysmith-duplication-audit-26-07-14-01-59-53.md`
- **Recommendation:** Extract shared `InsertChunksAsync` with `bool ignoreDuplicates` parameter. 2-3 hours.
- **Suggested Priority:** Low

### F-AG4-06: Lucene.Net beta dependency for two utility classes (F45)
- **Title:** Full Lucene.Net engine pulled in for tokenizer + highlighter only
- **Description:** 3 Lucene.Net packages (all beta) loaded for `StandardAnalyzer` and `Highlighter` only. No actual Lucene indexing/searching is done — the project hand-rolls its own search against SQLite. Disproportionate dependency footprint + beta supply-chain risk.
- **Severity:** Medium
- **Source Audit:** `memorysmith-packagebloat-audit-26-07-17-19-48-24.md`
- **Recommendation:** Consider hand-rolling small tokenizer/highlighter or using purpose-built package. Backlog candidate — 1-2 days if pursued.
- **Suggested Priority:** Low (backlog)

### F-AG4-07: Zero-admin lockout via `RemoveRole` (F53)
- **Title:** Unguarded last-admin role removal with no recovery path
- **Description:** `DELETE /api/admin/users/{userId}/roles/Admin` succeeds even on the last admin. No guard at controller or storage layer. Sibling `SetProviderEnabled` has analogous protection. No clear recovery path from zero-admin state.
- **Severity:** High
- **Source Audit:** `memorysmith-lastadmin-audit-26-07-19-01-09-06.md`
- **Recommendation:** Add admin-count check (≤ 1) before allowing Admin role removal. Single transaction. Add concurrency test. 2-3 hours.
- **Suggested Priority:** High

### F-AG4-08: Maintenance run has no exclusivity guard (F55)
- **Title:** Overlapping maintenance runs can corrupt status display and cause file races
- **Description:** `RunAsync` has no mutual exclusion check. Two independent entry points (HTTP + background) can execute concurrently. `MaintenanceActiveRunStore` has a single slot and can report wrong status. Topic-map cache file is subject to write races.
- **Severity:** Medium-High
- **Source Audit:** `memorysmith-maintenancerun-audit-26-07-19-01-24-26.md`
- **Recommendation:** Make `Begin` enforce exclusivity (return null when busy). Have `RunAsync` return `Skipped` result. 1-2 hours.
- **Suggested Priority:** High

### F-AG4-09: JSON extraction fragile against real LLM output (F58)
- **Title:** `ExtractJsonObjectPayload` silently discards LLM review output on common response shapes
- **Description:** Naive "first `{` to last `}`" extraction with `StartsWith("```")` code-fence detection breaks on (a) any leading prose before a fenced block, and (b) JSON string values containing brace characters. Both consumers catch `JsonException` silently, discarding real review content.
- **Severity:** Medium-High
- **Source Audit:** `memorysmith-jsonextraction-audit-26-07-19-22-27-48.md`
- **Recommendation:** Replace with regex for fenced-block-anywhere + proper brace-depth counter with string-literal awareness. Half-day including tests.
- **Suggested Priority:** Medium

---

## Findings That Extend/Correct Existing Tasks

### F-AG4-10: IsLoopback null-address fail-open (F43) — Extends TSK-0350
- **TSK-0350** (Backlog, Medium) captures only the null-`HttpContext` variant and frames it as a test-context problem. F43 reveals a null-`IPAddress` on a real, non-null `HttpContext` is a production-reachable condition (misconfigured reverse proxy) that fails open through the same shared helper — affecting not just `BootstrapGate` but the general remote-API middleware and on-by-default `OpenLocalEditorCompatibility`.
- **Recommendation:** Re-scope TSK-0350 to cover both null-`HttpContext` and null-`IPAddress` cases. Raise priority from Medium given `OpenLocalEditorCompatibility` defaults to `true`. Change helper from fail-open to fail-closed.
- **Suggested Priority:** High (was Medium)

### F-AG4-11: Rate-limiter null fallback (F52) — Corrects TSK-0290
- **TSK-0290** (Done, Critical) fixed the global-rate-limiter bug by partitioning per-IP, but the `?? "unknown"` fallback when `RemoteIpAddress` is null recreates the exact shared-bucket mechanism for every null-address caller. Same root cause as F43 (no `ForwardedHeaders` configuration).
- **Recommendation:** Two options: (1) Configure `ForwardedHeaders` properly (foundational); (2) Fall back to `httpContext.Connection.Id` instead of `"unknown"`. Add missing null-IP test case.
- **Suggested Priority:** High

### F-AG4-12: Unused packages (F46) — Extends archived TSK-0046
- **TSK-0046** (Archived, Medium) named only `Nerdbank.MessagePack` as a suspected-unused candidate. F46 confirms zero usage for all 4 packages (including `MessagePack` + `MessagePack.Annotations` + `Newtonsoft.Json`) and provides the concrete evidence the archived task needed.
- **Recommendation:** Revisit archive decision. Delete all 4 `PackageReference` entries. Check `Newtonsoft.Json` isn't a Swashbuckle transitive dependency before removing.
- **Suggested Priority:** Medium

### F-AG4-13: TaskDomainService lock preamble (F30) — Complements TSK-0045
- **TSK-0045** tracks a larger TaskDomainService layering split (Backlog, High). F30 identifies a tactical, low-risk refactor (extract `WithLockedTask` helper) that would make that larger split strictly easier by reducing 7-8 identical call sites to 1.
- **Recommendation:** Land the helper extraction before TSK-0045's larger refactor.
- **Suggested Priority:** Medium (sequencing note)

---

## Cross-Cutting Patterns

### Pattern 1: Null `RemoteIpAddress` — an unaddressed systemic gap
Three findings (F43, F52, and implicitly F29's severity caveat) all trace back to the same root cause: **this codebase has no `ForwardedHeaders` configuration story.** Any deployment behind a reverse proxy (a completely ordinary hosting topology for a self-hosted web app) will see null `RemoteIpAddress` for every request. This causes:
- **F43:** 3 security gates fail open (authorization bypass)
- **F52:** Rate-limiter degrades to shared-bucket (availability/lockout risk)
- **F29 (caveat):** The `SensitiveRead` gating the audit relies on for severity assessment would also be affected if the middleware mis-identifies remote callers as loopback
- **Recommendation:** Configure `ForwardedHeaders` properly with documented `KnownProxies`/`KnownNetworks`. This single fix closes two independent findings and prevents future bugs of the same class.
- **Audits involved:** Delta6, IsLoopback, RateLimiter

### Pattern 2: Same file, multiple independent findings
**`MaintenanceAgentServices.cs`** is the source of 3 findings in this partition:
- F55 (no run-exclusivity guard)
- F56 (uncatchable diff recursion crash)
- F58 (fragile JSON extraction)
This reinforces the remediation plan's W5 classification (god-class decomposition — TSK-0043).

### Pattern 3: Existing tasks were under-scoped or archived without resolution
Three findings (F43→TSK-0350, F46→TSK-0046, F52→TSK-0290) identify existing tasks whose scope or priority was set before the full evidence was available. Each finding offers concrete evidence to justify re-scoping.

### Pattern 4: Check-then-act race windows
F53 (last-admin removal) explicitly warns against implementing the guard as check-then-act with an `await` gap, pointing to F36/F48 as the established concurrency-test pattern in this codebase. This is a recurring design issue across the audits, not an isolated case.

### Pattern 5: Unbounded resource consumption without safety valves
F29 (unclamped MCP tool input), F55 (unbounded concurrent runs), and F56 (unbounded diff memory/recursion) all represent the same class of bug: a resource-consumption path with no upper bound. The remediation plan's W2 (config-surface trust sweep) partially addresses this for config properties, but runtime resource bounds are a separate, cross-cutting concern.

---

## Supersession Notes

- The **remediation plan** (`memorysmith-remediation-plan-26-07-12-15-35-28.md`) is dated July 12 — it covers F1-F22 from earlier audit waves. All findings in this partition (F29-F58) are dated July 13-19, so they are **newer** than the plan and not accounted for in it. The plan's W1-W6 workstreams do not cover any of the findings extracted here.
- No audit in this partition appears to be superseded by a newer audit on the same topic — each covers distinct subject matter.
