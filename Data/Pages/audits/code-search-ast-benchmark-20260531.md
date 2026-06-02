# Code Search AST Benchmark & Reliability Report (2026-05-31 / 2026-06-01)

**Author:** Claude Sonnet 4.6 via npx mcp-remote
**Scope:** AST chunking quality evaluation, pre/post comparison, index lifecycle reliability
**Sessions:** 2026-05-31 (AST quality) + 2026-06-01 (restart, partial build, lifecycle)
**Builds tested:** Pre-AST (1,912 chunks/171 files) → Post-AST full (5,433/173) → Post-restart partial (2,434/75)
**Related:** `audits/claude-search-quality-deepdive-20260529`, TSK-0183, TSK-0185, TSK-0186

---

## 1. Index State Across Sessions

| State | Files | Chunks | Avg chunks/file | Source |
|---|---|---|---|---|
| Pre-AST (2026-05-29) | 171 | 1,912 | 11.2 | Sliding-window chunking |
| Post-AST full build (2026-05-31) | 173 | 5,433 | 31.4 | AST method/symbol chunking |
| Post-restart partial build (2026-06-01) | 75 | 2,434 | 32.5 | Build cancelled at 55% |

The 2.84× chunk increase (1,912 → 5,433) is entirely from AST — each C# method, constructor, property, constant, and config section is now a separate independently-embedded chunk. The post-restart partial build reflects a background rebuild that was silently cancelled at 55% with no error.

**Assumption:** `rebuildIfStale=false` was passed to all code search calls in both sessions. The post-restart partial build was triggered by a single `rebuildIfStale=true` call that timed out at the MCP layer while the build continued in background.

---

## 2. AST Quality Improvements — Pre/Post Comparison

### 2.1 CP3: Authorization Policy Lookup (The Precision Problem, Now Fixed)

**Query:** `CanReadSourceBundle authorization permission check`

**Pre-AST (sliding windows, 40-line chunks):**

| Rank | File | Score | Verdict |
|---|---|---|---|
| 1 | `Program.cs` L289–328 | 0.840806 | ⚠️ Adjacent auth context |
| 2 | `SecurityServices.cs` L1–40 | 0.796868 | ⚠️ Service header |
| 3 | `McpController.cs` L161–200 | 0.794423 | ⚠️ Tool filtering |
| **4** | **`SourceLinksController.cs` L1–26** | **0.790391** | **✅ Exact: `[Authorize(Policy = CanReadSourceBundle)]`** |

The most precise answer was at rank 4. A 0.050 score gap separated the broad auth files from the specific declaration.

**Post-AST full build (method-level chunks):**

| Rank | File | Lines | Score | Verdict |
|---|---|---|---|---|
| **1** | **`McpController.cs`** | **217–218** | **0.825836** | **✅ `private async Task<bool> CanReadSourceBundleAsync() => (await _authorization.AuthorizeAsync(...CanReadSourceBundle)).Succeeded`** |
| **2** | **`SecurityServices.cs`** | **25–25** | **0.763863** | **✅ `public const string CanReadSourceBundle = "CanReadSourceBundle";`** |
| 3 | `McpController.cs` | 132–183 | 0.718576 | ✅ `DelegateToCatalogAsync` — adjacent |
| 4 | `SecurityServices.cs` | 210–222 | 0.670711 | ✅ `MemorySmithPermission` enum |

**Assessment (confidence: 95%):** Completely resolved. The 2-line method body is #1 with a 0.107 gap to rank 3. The constant definition is a single-line chunk at #2. Score gap between correct answers and noise is now ~3× wider than pre-AST. The result is more correct than the pre-AST "correct" answer — `CanReadSourceBundleAsync()` in `McpController.cs` is where the permission is checked in the MCP context, not just where the `[Authorize]` attribute appears.

**Post-restart partial build (75/173 files):**
Same top-2 results, identical scores. `McpController.cs` and `SecurityServices.cs` were indexed early (within the first 43% of files). The correct answer survives partial indexing for this query.

---

### 2.2 CP1: Hybrid Search RRF (False Positive Eliminated)

**Pre-AST top-5 included a false positive at rank 2:** `ChatToolCatalog.cs` L129–168 — the lexical search handler chunk riding alongside the hybrid tool definition in the same sliding window.

**Post-AST top-5 (full build):**

| Rank | File | Lines | Score | Verdict |
|---|---|---|---|---|
| 1 | `ChatToolCatalog.cs` | 125–1160 | 0.779352 | ✅ `BuildTools()` — 1,035-line chunk ⚠️ |
| 2 | `MemoryApplicationService.cs` | 1156–1178 | 0.744292 | ✅ `BuildHybridMatchReason` (23 lines) |
| 3 | `MemoryApplicationService.cs` | 896–945 | 0.737863 | ✅ `RankHybridResults` (50 lines) |
| **4** | **`MemoryApplicationServiceTests.cs`** | **792–830** | **0.709905** | **✅ New: `HybridSearchAsync_FusesLexicalAndSemanticRanksWithRrf`** |
| **5** | **`SearchBenchmarks.cs`** | **16–16** | **0.698654** | **✅ New: benchmark fixture** |

**Post-restart partial build (CP1 on 75/173 files):**

| Rank | File | Score | vs Full |
|---|---|---|---|
| 1 | `ChatToolCatalog.cs` | 0.717187 | −0.062 |
| 2 | `MemoryApplicationService.cs` | 0.682429 | −0.062 |
| 3 | `MemoryApplicationService.cs` | 0.658116 | −0.080 |
| — | `MemoryApplicationServiceTests.cs` | ABSENT | Test project not indexed |

**Assessment (confidence: 90%):** The false positive is eliminated by AST — the lexical handler no longer pollutes results because `BuildTools()` is a single chunk and the lexical handler has no separate chunk. Tests and benchmarks surfaced for the first time at ranks 4 and 5. The partial index correctly surfaces ranks 1–3 but loses the test file entirely — `MemorySmith.Tests` was not reached before the build cancelled. Score degradation on partial index: −0.062 to −0.080 across results.

---

### 2.3 New Findings Unique to AST Chunking

#### JSON Config Files Are Indexed

`appsettings.json` appeared in code search results, surfacing authoritative current configuration:
- `QueryPrefix: "query: "` and `DocumentPrefix: "passage: "` — E5 instruction-tuning prefixes
- `MaxInputTokens: 512` — embedding context window
- `MaxIndexedTextCharacters: 6000` — truncation limit
- `PoolingMode: "Mean"` — confirmed mean pooling
- `ZeroLexicalEvidencePenalty` and `LexicalScoreSaturation` — penalty tuning values
- `ExecutionProvider: "Cpu"` — confirmed CPU inference

This gives agents access to actual config values without needing KB record lookups, which may lag behind code changes.

#### `ScoreHybrid()` Implementation Visible

Query `no lexical evidence penalty score reduction false positive` returned the exact 10-line `ScoreHybrid()` method at rank 2:

```csharp
private double ScoreHybrid(double rawVectorScore, double lexicalScore)
{
    if (lexicalScore <= 0)
        return rawVectorScore * _zeroLexicalEvidencePenalty;
    var normalizedLexical = lexicalScore / (lexicalScore + _lexicalScoreSaturation...
```

The penalty formula is now directly accessible via code search.

#### `MaxResultsPerDocument = 2` Confirmed (Prior Criticism Retracted)

Query `MaxResultsPerDocument diversification per file limit top-k` returned `MemorySmithOptions.cs` L276:
```csharp
public int MaxResultsPerDocument { get; set; } = 2;
```

The prior benchmark report criticised "same-file flooding at 40% of results for limit=5." This criticism was incorrect — `MaxResultsPerDocument = 2` is intentional and correctly caps same-file results. For `limit=5`, showing 2 results from one file (40%) is the designed maximum. **Retracting the prior criticism.**

#### `indexedAtUtc` Field Now on Results

Post-AST code search results include `indexedAtUtc` per chunk — the timestamp when that chunk was last written to the index. For the partial build, this makes the coverage boundary visible:
- `McpController.cs` indexed at `2026-06-02T00:33:57Z` (17s into build)
- `SecurityServices.cs` indexed at `2026-06-02T00:36:17Z` (2m 37s into build)
- Test files: not present (build cancelled before MemorySmith.Tests target)

Agents can use this to determine which files have fresh vs stale index coverage.

---

## 3. The Long-Method Chunk Problem

`ChatToolCatalog.cs:BuildTools()` spans lines 125–1160 — **1,035 lines** as a single AST chunk. It is an iterator method containing `yield return` statements for all 19+ tool descriptors.

**Impact:**
- For direct queries about `BuildTools()`: scores 0.886 (strong). Direct method name in query produces high lexical evidence (18.891) that compensates for embedding dilution.
- For queries about a specific tool within the catalog: scores ~0.779 — lower than the pre-AST focused chunk for the same file (0.803). The embedding dilutes across all tool definitions.
- For the partial-index version: 0.717 — further degraded without test files.

**Root cause:** `BuildTools()` cannot be split at the AST method level because it is structurally one method. Sub-chunking at statement boundaries would require a secondary pass for methods exceeding a line threshold.

**Suggested mitigation:** For methods exceeding ~200 lines, apply a secondary statement-boundary chunking pass. This would produce ~5 focused chunks of `BuildTools()` each covering 3–4 tool definitions. This is precedented in tree-sitter-based code chunkers.

**Severity: Low.** Direct queries work. The dilution only affects tangential queries targeting one section of the catalog. With 19 tools, the practical impact is mild.

---

## 4. Index Lifecycle Reliability — Critical Findings

### 4.1 Index Does Not Survive Service Restart

After the service restarted, `code_search_status` reported `indexedFileCount: 0, indexedChunkCount: 0`. The SQLite database at `Data/Graph/code-search/code-search.db` persists on disk but the index loaded into memory starts empty.

**Evidence:** Pre-restart build had 5,433 chunks. Post-restart status showed 0 immediately. Memory search (ONNX + Lucene) was unaffected — only the code search index was empty.

**Implication for agents:** After any service restart, `code_search` with `rebuildIfStale=false` returns zero results. Calling `code_search_status` first is mandatory to detect this state.

**Open question OQ-LC1:** Is the SQLite database preserved across restarts? If `code-search.db` is on disk and persists, loading it on startup would avoid a full rebuild. The current behaviour suggests either the DB is cleared on startup or the in-memory index is not populated from the DB automatically.

---

### 4.2 `rebuildIfStale=true` Always Times Out for Large Indexes

With 5,433 chunks and 173 files, a full AST rebuild takes approximately 8 minutes (confirmed: 452s embedding + overhead). The MCP tool call timeout is ~4 minutes. Therefore `rebuildIfStale=true` will always time out before completion on this corpus.

**Confirmed behaviour:**
1. Call `code_search(rebuildIfStale=true)` → MCP timeout at ~4 minutes
2. Build continues in background — confirmed by `code_search_status` showing `state: "indexing"` at 49% after the timeout
3. Build completes (or is cancelled) — monitored via subsequent `code_search_status` calls

**Recommended agent workflow for post-restart code search:**

```
1. code_search_status()
   → if indexedFileCount == 0: index is empty after restart
   
2. code_search(query=..., rebuildIfStale=true)
   → EXPECT 4-minute timeout — this is normal, NOT an error
   → build is now running in background

3. Poll: code_search_status() every 30–60 seconds
   → watch build.state: "idle" AND indexedFileCount == totalFileCount
   → if build.state: "canceled" before completion: rebuild needed
   
4. code_search(query=..., rebuildIfStale=false)
   → returns results from freshly built index
```

This workflow should be documented in tool descriptions. Currently, an agent receiving a 4-minute timeout on a legitimate rebuild call has no way to distinguish it from a hang.

**Open question OQ-LC2:** Should `rebuildIfStale=true` trigger an async rebuild and return immediately with current (possibly stale or empty) results, rather than blocking? A non-blocking rebuild trigger with an `isIndexStale: true` flag in the response would remove the timeout problem entirely.

---

### 4.3 Silent Partial Build Cancellation

The background rebuild was cancelled at 55% (95/173 files processed, 75 committed to DB) with `lastError: null`. The service state transitioned to `build.state: "canceled"` with no error logged.

**Timeline:**
- `2026-06-02T00:33:40Z` — rebuild started
- `2026-06-02T00:37:56Z` — status polled, 49% complete
- `2026-06-02T00:38:47Z` — build cancelled, 55% complete, `completedAtUtc` set

**Possible causes (confidence values):**
- CancellationToken triggered by MCP request cancellation from the timed-out call (60%): the MCP timeout at ~00:37:40 may have propagated a CancellationToken to the rebuild task
- Internal resource threshold (25%): memory or I/O limit hit during AST parsing of large files
- Deliberate build timeout (15%): a max-build-duration setting cut the job

**Impact of partial index:**
- 75 of 173 files indexed — 43% coverage
- `MemorySmith.Tests` project entirely absent (alphabetically later, not reached before cancellation)
- Queries targeting test files return no test results — implementation-only results substitute
- Score degradation: −0.062 to −0.080 across all measured queries

**The agent has no automatic warning.** `code_search` results include `status.indexedFileCount: 75` and `status.build.state: "canceled"` in the response body, but there is no top-level `indexCoverage: "partial"` flag or warning. An agent must explicitly check `status.indexedFileCount` vs `status.build.totalFileCount` to detect partial coverage.

**Open question OQ-LC3:** Should `code_search` add a top-level `warning: "Index coverage is partial (43%). Results may be incomplete."` when `build.state == "canceled"` or `indexedFileCount < build.totalFileCount`? This single field would surface partial-index risk without requiring agents to parse the nested status block.

**Open question OQ-LC4:** Would the build resume from checkpoint on a second `rebuildIfStale=true` call, or restart from scratch? Evidence suggests restart from scratch (`reusedFileCount: 0` on the cancelled build), but a checkpointed resume would significantly reduce the rebuild time for large indexes.

---

## 5. E5 vs Nomic — Pre-AST Benchmark Context

From `research/vector-search/e5-vs-nomic-embed-text-20260528` (pre-AST, 1,746 chunks):

| Metric | E5 | Nomic | Winner |
|---|---|---|---|
| Cold rebuild elapsed | 1,056s | 1,310s | E5 (24% faster) |
| Avg embedding call | 3,341ms | 4,153ms | E5 |
| Warm query latency (avg across 6 queries) | 320ms | 121ms | **Nomic (62% faster)** |
| Relevance suite (8 cases) | 8/8 | 8/8 | Tie |

**Key insight:** Nomic is 62% faster at query time but 24% slower to rebuild. With AST pushing rebuild time to ~8 minutes (vs ~18 minutes pre-AST at pre-existing E5 rate of 3,341ms/call), the rebuild cost is now even more pronounced for any model switch. The post-AST E5 avg embedding call is 561–598ms — significantly faster than the pre-AST 3,341ms, suggesting AST chunks are shorter (fewer tokens per call) and therefore faster to embed.

**Assumption:** The Nomic benchmark was run on the pre-AST index (1,746 chunks). AST chunking at ~31 chunks/file vs ~11 chunks/file will affect both models. Nomic's query latency advantage may still hold post-AST; its rebuild disadvantage (24% slower) scales linearly with chunk count.

---

## 6. Memory Search Freshness Verification

Query `chat agent provider num_ctx context window governance` run on 2026-06-01:

- `project-wiki-chat-agent-provider` ranked #1, score 0.032787 (lexical rank 1, semantic 0.860)
- `lastUpdated: 2026-06-01T20:25:33Z` — updated hours before this test
- Snippet includes `num_ctx for context window governance` — the new field is in the indexed content

**Assessment (confidence: 98%):** Memory search reflects same-day record updates correctly. The `num_ctx` governance field appears in both the snippet and the matchReason lexical content list. ONNX memory search is healthy and fresh.

Two records updated today at identical timestamp `2026-06-01T20:25:33Z`:
- `project-wiki-chat-agent-provider`
- `project-wiki-agent-instructions-source-of-truth` (Agent Smith workflow contract, `.github/agents/smith.agent.md`)

The identical timestamp indicates a batch update — likely the maintenance agent processed both in the same run.

---

## 7. Service Crash — Large Page Read

During this session, sequential `page_get` calls on large audit pages (`audits/code-search-security-ui-observability-audit-7-20260529` at `maxCharacters=12000`, followed by `audits/audit-8-benchmarks-training-eval-harness-20260530` at `maxCharacters=8000`) caused a full service outage. All subsequent tool calls timed out until the service was manually restarted.

**Evidence:** The crash followed two consecutive large page reads. The search quality deepdive page (`audits/claude-search-quality-deepdive-20260529`) is 29,306 chars — successfully fetched at `maxCharacters=6000` without issue. The audit 7 page may be substantially larger.

**Hypothesis (confidence: 70%):** Very large page content (potentially 50k–100k characters for comprehensive audit reports) may exhaust the page rendering or JSON serialization pipeline, causing an unhandled OOM or timeout in the shared service layer. Unlike `page_get` truncation (which works correctly up to `maxCharacters=20000`), extremely large pages may hit a limit beyond the parameter.

**Open question OQ-SVC1:** Is there a server-side hard cap on page content size beyond the `maxCharacters` parameter? If page storage files can grow to 50k+ characters (as audit reports evidently do), the service needs either a hard cap with a structured error response, or chunked streaming to prevent full-content memory allocation.

**Open question OQ-SVC2:** Do the large audit pages exceed a safe character threshold? If so, consider splitting them at natural section boundaries during authoring, or implementing a `page_get_section(slug, sectionIndex)` tool.

---

## 8. Corrected Findings from Prior Report

| Prior claim | Correction |
|---|---|
| `MaxResultsPerDocument` flooding is excessive | Retracted — `= 2` is confirmed intentional by single-line property chunk |
| CP3 correct answer at rank 4 is a persistent issue | Resolved by AST — now rank 1 as a 2-line method chunk |
| `page_delete` timing out is a bug | Clarified — permission was intentionally not granted |
| Rebuild "continues after MCP timeout" (assumed positive) | Partially correct — continues but can be silently cancelled at partial completion |

---

## 9. Summary Scorecard

### Code Search — What Improved with AST

| Finding | Confidence |
|---|---|
| CP3 precision: exact method now #1 (was #4) | 95% |
| CP1 false positive eliminated (lexical handler gone) | 92% |
| Test/benchmark files surface alongside implementation | 90% |
| Config values (appsettings.json) now accessible | 93% |
| Single-line declarations (constants, properties) independently searchable | 99% |
| `ScoreHybrid()` implementation directly accessible | 93% |
| `MaxResultsPerDocument = 2` confirmed intentional | 99% |
| `indexedAtUtc` per-chunk freshness signal is new | 99% |

### Index Lifecycle — Issues Found

| Issue | Severity | Confidence |
|---|---|---|
| Index empty after service restart (requires full rebuild) | High | 95% |
| `rebuildIfStale=true` always times out for AST index (~8 min build vs 4 min MCP timeout) | High | 95% |
| Silent partial build cancellation at 55%, `lastError: null` | High | 88% |
| Partial index serves results without top-level coverage warning | Medium | 92% |
| Test project absent from partial index | High (for test queries) | 95% |
| Score degradation on partial index: −0.06 to −0.08 | Medium | 90% |
| Large page read can crash the service | High | 70% |

### Open Questions

- **OQ-LC1:** Is the SQLite code-search DB preserved across restarts? If so, why isn't it loaded on startup to avoid full rebuild?
- **OQ-LC2:** Should `rebuildIfStale=true` be non-blocking (trigger async rebuild, return current results with `isIndexStale` flag)?
- **OQ-LC3:** Should `code_search` add a top-level `warning` when `build.state == "canceled"` or coverage is partial?
- **OQ-LC4:** Does `rebuildIfStale=true` restart the build from scratch, or resume from the checkpoint? If restart, a checkpoint mechanism would halve average rebuild time for interrupted builds.
- **OQ-SVC1:** Is there a server-side content size limit for `page_get` beyond `maxCharacters`? Large audit pages may exceed safe thresholds.
- **OQ-AST-1:** Is there a line-count threshold for sub-method chunking? `BuildTools()` at 1,035 lines is the extreme case; methods over ~200 lines would benefit from statement-boundary sub-chunking.

---

*Written by Claude Sonnet 4.6 via npx mcp-remote | 2026-05-31 + 2026-06-01*
*Non-destructive: all tests read-only. Test probe page `research/write-probe-20260531b` is safe to delete.*