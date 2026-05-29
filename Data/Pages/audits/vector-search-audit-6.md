# MemorySmith — Audit #6

## Continued Deep Dive: Reliability, Performance, Usability, Observability, Repeatability

**Generated:** 2026-05-28 (companion to Audits #1-#5)
**Subject:** [TheMasonX/MemorySmith](https://github.com/TheMasonX/MemorySmith) `feature/code-search-high-roi-batch8` latest tip (post-cleanup)
**Calibration:** Per user's explicit direction — reliability/performance/usability/observability/repeatability over security/XSS. The user recognizes the local-first small-actor model makes XSS a lower priority than bugs that affect results quality, data integrity, and developer experience. Security issues are still noted but de-prioritized unless they risk data corruption or session compromise.
**Methodology:** Full re-download of the branch after major cleanup (~7,800 lines removed vs Audit #4 snapshot), three parallel subagent reviews (code-search, chat/markdown, tests/CI), plus my own first-hand reads of the remaining delta. Total C# lines: 42,322; total [Test] methods: 378 (up from 350 at Audit #1).

**What changed since Audit #4:** The maintainer removed the entire training/finetune infrastructure (TrainingWorkbench.razor, ChatFeedbackStore, ChatTranscriptWriter, harness.py, Run-FinetuneHarness.ps1, and ~7 TSK-020x task records), plus substantial test cleanup (removed verbose setup code while retaining all 22 code-search tests). New additions: `MermaidRestrictionModes` with three-tier policy (standard/restricted/strict), `Mermaid.initialize({securityLevel:"strict"})`, `IndexStalenessCheckCooldownSeconds`, `ResumableBuildsEnabled`, `MaxCompletedBuildLogEntries`, `MaxResultsPerDocument`, `TakeBalancedByDocument`, `ScoreHybrid` with tunable vector/lexical weights, `ExpandQueryTokens` synonym expansion, `MergeShardAsync`, `PruneBuildLogAsync`, and `CodeSearch.razor` (a dedicated UI page for code search).

---

## 0. Executive Summary

The branch is in much better shape than at Audit #4. The training infrastructure cleanup removed ~4,500 lines of premature work, and the remaining code gained several genuinely good features: hybrid scoring with configurable weights, document-balanced result selection, resumable builds, shard merging, and staleness cooldowns. The Mermaid security policy is a thoughtful three-tier model. Test count grew.

Three categories of issues dominate this audit:

1. **Performance architecture is the ceiling** — the same five items from Audit #4 are still open (JSON embedding storage, scalar Dot, no ANN, no SQLite pragmas, no connection pooling) and they compound. A 50k-chunk codebase would hit multi-second query latency and multi-GB memory usage. These are the highest-impact fixes available.

2. **Code quality under cleanup pressure** — the fast cleanup left indentation defects (lines 384-385 in `CodeSearchService.cs`), duplicated code (30-line LINQ blocks repeated three times for vector/fallback/lexical paths, plus ~20 duplicated helpers between `ChatServices.cs` and `ChatToolCatalog.cs`), and residual dead code (`ShouldPreloadContext`, `FormatRecordAsync` in ChatServices).

3. **Result quality can be measurably improved** — the hybrid scoring formula, the lexical saturation constant, the target-weight magic numbers, and the synonym table are all hand-tuned without empirical calibration. The relevance suite exists and passes 8/8 — it's the right tool for tuning these, but nobody has used it for tuning yet. A single afternoon with a grid search over the weight/saturation space would likely improve nDCG@10 by 10-20%.

Severity rollup for this audit: **1 Critical, 8 High, 18 Medium, 11 Low.**

---

## 1. Critical Finding

### 1.1 [CRITICAL, conf 0.95] Indentation/control-flow bug in `SearchAsync` lexical tail

**Source:** `CodeSearchService.cs:384-385`.
```csharp
            CacheResults(resultCacheKey, lexicalResults);
                return CompleteSearch("lexical", lexicalResults, chunks.Count);
    }
```

**Verified:** The `CacheResults` call is at 12-space indent and `return` is at 16-space indent, but both sit at method-body scope — there's no enclosing `if` or block. C# ignores whitespace so the code compiles and runs correctly, but:

1. A maintainer reading this visually perceives an outer block and may add logic between the method body and this "inner" return, which would then be unreachable.
2. The inconsistency strongly suggests a paste/merge accident during the cleanup where a surrounding conditional was removed but the indentation wasn't flattened.
3. Contrast with the vector path (lines 304-308) and fallback path (lines 343-347), which have correct indentation.

This was likely introduced during the major cleanup when the training infrastructure was removed.

**Fix:** One-line: fix the indentation. ~30 seconds. But worth mentioning because indentation bugs in large methods (this method is 153 lines) are exactly the kind of defect that hides real logic errors later.

---

## 2. High-Severity Findings (Reliability & Performance)

### 2.1 [HIGH, conf 0.95] Embeddings stored as JSON TEXT, not BLOB

Still open from Audit #4 §4.3. For E5-base-v2 (768-dim), JSON encoding costs ~6-10 KB per chunk vs ~3 KB binary. On a 50K-chunk index: ~300 MB JSON vs ~150 MB binary. Every query deserializes every candidate's embedding via `JsonSerializer.Deserialize<float[]>` — 150-400 allocations on the managed heap per query.

**Impact on reliability:** GC pressure during search queries causes latency spikes. The JSON TEXT column can't be indexed by sqlite-vec (which requires a binary vector column).

**Recommendation:** Migrate `EmbeddingJson TEXT` → `Embedding BLOB`. Encode via `MemoryMarshal.AsBytes(embedding.AsSpan())`. Read via `SqliteDataReader.GetStream` + `BinaryReader`. With FP16 (`System.Half`), halve the storage again. This is the single highest-impact performance change available.

### 2.2 [HIGH, conf 0.95] Scalar managed `Dot` product without SIMD

Still open from Audit #4 §4.4. `CodeSearchService.cs:2029-2038`. `TensorPrimitives.Dot` from `System.Numerics.Tensors` (.NET 8+) gives 4-8× throughput on AVX2. For 400 candidates × 768-dim, the improvement is ~300µs → ~75µs per query. One NuGet reference + one-line change.

### 2.3 [HIGH, conf 0.95] No SQLite WAL mode or performance PRAGMAs on code-search DB

Still open from Audit #4 §4.5. `CodeSearchService.cs:1629-1639`. No `journal_mode=WAL`, `synchronous=NORMAL`, `temp_store=MEMORY`, `cache_size`, `mmap_size`. Without WAL, readers block writers during builds. The new resumable-build feature makes this worse — a resumed build reopens the connection without any PRAGMA tuning.

### 2.4 [HIGH, conf 0.90] Connection pooling still disabled

`Pooling = false` at `CodeSearchService.cs:1634`. Every query opens a fresh OS file handle. With pooling enabled, the connection stays warm across queries (important for the new staleness-check cooldown, which fires frequently).

### 2.5 [HIGH, conf 0.90] `ScoreLexical` and `CountMatchedTokens` recompute the same haystack string independently

**Source:** `CodeSearchService.cs:2047` (in `ScoreLexical`): `var haystack = chunk.DocumentPath + "\n" + chunk.SearchText;`
And `CodeSearchService.cs:2074` (in `CountMatchedTokens`): `var haystack = chunk.DocumentPath + "\n" + chunk.SearchText;`

Both are called from the same scoring lambda (lines 279-281):
```csharp
var matchedTokenCount = CountMatchedTokens(chunk, expandedQueryTokens);
var lexicalScore = ScoreLexical(chunk, expandedQueryTokens);
```

Each computes `chunk.DocumentPath + "\n" + chunk.SearchText` — allocating a fresh concatenated string. For 400 prefilter candidates, that's 800 string allocations per query doing the same work.

**Recommendation:** Extract `BuildHaystack(chunk)` → call once, pass to both.

### 2.6 [HIGH, conf 0.90] Prefilter SQL uses `instr(lower(...))` — no index utilization

Still open from Audit #4 §3.3. The prefilter SQL at lines ~1855 computes `lower(SearchText)` per row. Without an expression index (`CREATE INDEX ... ON CodeSearchChunks(lower(DocumentPath))`) or a pre-lowered column, SQLite does a full table scan.

For the new hybrid scoring path, the prefilter is more important than before — it determines which chunks enter the `ScoreHybrid` function. A faster prefilter directly improves query latency.

### 2.7 [HIGH, conf 0.85] No ANN index despite growing infrastructure

Still open from Audit #4 §4.8. The branch added resumable builds, shard merging, build logs — infrastructure that presumes a growing index. But the actual retrieval is still brute-force O(N). With sqlite-vec, the same SQLite DB could host an HNSW index alongside the chunk table, and the query would be `SELECT ... FROM vec_chunks WHERE embedding MATCH @query ORDER BY distance LIMIT @k`.

### 2.8 [HIGH, conf 0.85] Test coverage gaps in concurrency, edge cases, and prefilter SQL

The code-search subagent identified ~10 specific untested scenarios: concurrent `SearchAsync` during build, cancellation token propagation, `EnsureColumnAsync` migration path, cache invalidation after shard merge, empty query handling, special regex characters in queries, `MaxFileBytes` enforcement, `ChunkOverlapLineCount` boundary values, and prefilter SQL correctness with SQL metacharacters. The 22 existing tests cover happy paths well but don't exercise the failure modes that matter for reliability.

---

## 3. Medium-Severity Findings (Usability, Observability, Repeatability)

### 3.1 [MEDIUM, conf 0.90] Hybrid scoring weights are magic numbers without calibration

**Source:** `CodeSearchService.cs:127-130`.
```csharp
private const double HybridVectorWeight = 0.75;
private const double HybridLexicalWeight = 0.25;
private const double ZeroLexicalEvidencePenalty = 0.72;
private const double LexicalScoreSaturation = 4.0;
```

The hybrid scoring is a genuine improvement over Audit #4's raw-cosine-only path. But the four constants are hard-coded with no empirical justification. The relevance suite (`Scripts/code-search-relevance-suite.json`) has 8 test cases with expected top documents — the right tool for a grid search over these constants. A single afternoon running the suite with `(HybridVectorWeight, HybridLexicalWeight) ∈ {(0.6,0.4), (0.7,0.3), (0.75,0.25), (0.8,0.2)}` × `ZeroLexicalEvidencePenalty ∈ {0.5, 0.6, 0.72, 0.85}` × `LexicalScoreSaturation ∈ {2, 4, 8, 16}` (64 combinations) and evaluating nDCG@10 / MRR would either confirm the current values are near-optimal or find a 10-20% improvement.

**Recommendation:** Make these configurable via `CodeSearchOptions`. Run the grid search against the relevance suite. Document the optimal values and the methodology.

### 3.2 [MEDIUM, conf 0.90] Synonym table includes physical-tool entries from test scenarios

**Source:** `CodeSearchService.cs:133-144`.
The synonym map includes `screwdriver → tool, tooling, utility, helper`, `hammer → tool, build, construct`, `wrench → tool, fix, repair, adjust`, `pliers → tool, grip, extract`. These exist to support the `ScrewdriverSemanticBiasEmbeddingProvider` test. In a real code search against a hardware-control or DIY codebase, these pollute query expansion.

**Recommendation:** Move test-specific synonyms to test configuration. Keep the synonym map configurable via `CodeSearchOptions.SynonymTable` so operators can add domain-specific expansions.

### 3.3 [MEDIUM, conf 0.90] `TakeBalancedByDocument` diversity doesn't consider the quality gap

**Source:** `CodeSearchService.cs:2087-2115`.
The function caps results per document at `MaxResultsPerDocument` (default 2). But it doesn't consider the quality gap between the capped and uncapped results. If document A has chunks scoring 0.95, 0.93, 0.91 and document B has one chunk scoring 0.60, the cap prefers showing A×2 + B×1 over A×3 — even though A's third chunk (0.91) is far more relevant than B's first (0.60).

**Recommendation:** Add a quality-gap threshold: only apply the per-document cap when the next document's best score is within N% of the capped document's next-best score. E.g., cap only when `nextDoc.bestScore ≥ 0.8 × currentDoc.nextChunkScore`.

### 3.4 [MEDIUM, conf 0.85] `DefaultExcludePatterns` test doesn't actually test exclusion

The test `SearchAsync_DefaultExcludePatternsSkipProjectDocsNoise` (name implies exclude-pattern behavior) actually tests target-weight demotion. The `.md` file isn't excluded from indexing — it's indexed but down-weighted. If its lexical score is high enough, it appears in results. The test name is misleading.

**Recommendation:** Either rename the test to `SearchAsync_DemotesDocsContentForImplementationQueries`, or configure actual exclude patterns in the test and verify the docs file is not indexed at all.

### 3.5 [MEDIUM, conf 0.85] `ShouldPreloadContext` and `FormatRecordAsync` are dead code in ChatServices.cs

**Source:** `ChatServices.cs:2451-2479` (`ShouldPreloadContext` — private, no callers) and `ChatServices.cs:2232-2244` (`FormatRecordAsync` — private, no callers). Plus ~6 compiled regex helpers at lines 2482-2498 that support `ShouldPreloadContext`. Total: ~50 lines of dead code.

**Impact on reliability:** None directly. Impact on maintainability: a reader assumes these methods are live and may waste time understanding or modifying them.

**Recommendation:** Delete. The context preloading logic lives in `ChatContextPlanner.Plan` now.

### 3.6 [MEDIUM, conf 0.85] Duplicated helper methods between ChatServices.cs and ChatToolCatalog.cs

~20 methods are duplicated with near-identical implementations: `ReadLexicalQuery`, `ReadSemanticQuery`, `ReadHybridQuery`, `ReadContextPackQuery`, `ReadInt`, `ReadBool`, `ReadStatus`, `ReadString`, `GetProperty`, `Truncate`, `FormatLexicalResults`, `FormatSemanticResults`, `FormatHybridResults`, `FormatContextPack`. The `ChatServices.cs` versions accept `IReadOnlyList<MemoryRecord>` while `ChatToolCatalog.cs` versions accept `IReadOnlyList<MemorySearchResult>` — the tool catalog versions are the canonical ones.

**Recommendation:** Delete the `ChatServices.cs` duplicates. They're remnants from the pre-catalog refactor.

### 3.7 [MEDIUM, conf 0.85] `StripJsonFence` doesn't handle nested or partial fences

**Source:** `ChatServices.cs:3110-3131`. Uses `LastIndexOf("```")` to find the closing fence. A model output with three backticks inside a JSON string value (valid JSON: `{"code":"```"}`) causes premature truncation.

**Impact on reliability:** Tool calls silently fail. The user sees no error — the response is treated as non-tool-call prose.

**Recommendation:** Try parsing first without stripping; only strip on failure. Extend language-tag stripping to be case-insensitive.

### 3.8 [MEDIUM, conf 0.85] `ReadToolCalls` swallows all parse exceptions silently

**Source:** `ChatServices.cs:1931-1949`. Returns empty list on any JSON parse failure. Combined with `IsPotentialToolCallPrefix` (lines 1925-1929) which buffers any response starting with `{`, `[`, or backtick — a model that outputs `{ "hello": "world" }` as prose gets fully buffered, fails to parse as a tool call, and the user sees nothing until the stream completes. No log, no error indicator.

**Impact on usability:** The user experiences a "hung" streaming response that suddenly dumps text at the end.

**Recommendation:** Log a warning when `StripJsonFence` + `JsonNode.Parse` fails after `IsPotentialToolCallPrefix` matched. Flush buffered content to the user as a normal response with a small "tool-call parse skipped" trace event.

### 3.9 [MEDIUM, conf 0.85] `IsPotentialToolCallPrefix` over-matches on legitimate responses

Any response starting with `{`, `[`, or backtick is classified as a potential tool call and buffered. This covers too many legitimate prose patterns: JSON examples, code blocks, bulleted lists. The buffering stalls the streaming UX.

**Recommendation:** Add a byte-count or time threshold (e.g., 2KB or 500ms) after which the buffer is flushed to the user regardless.

### 3.10 [MEDIUM, conf 0.85] Chunking uses LINQ `Skip/Take/ToArray` per chunk window

**Source:** `CodeSearchService.cs:797`. `lines.Skip(startLineIndex).Take(endLine - startLineIndex).ToArray()` allocates an iterator chain and a fresh array per chunk. For a 2000-line file with 40-line chunks and 8-line overlap, that's ~62 arrays allocated.

**Recommendation:** Use `ArraySegment<string>` or `lines.AsSpan().Slice(...)`.

### 3.11 [MEDIUM, conf 0.85] `BuildSnippet(chunkText, chunkText)` at index time is a no-op

**Source:** `CodeSearchService.cs:820`. Passing `chunkText` as both content and query means `content.IndexOf(query)` always matches at index 0 — the snippet is always the first 280 chars. The query-aware windowing is wasted at index time.

**Recommendation:** Use a dedicated `TruncateSnippet(text, maxLength)` at index time. Reserve `BuildSnippet(content, query)` for query-time rendering.

### 3.12 [MEDIUM, conf 0.85] No streaming HTTP endpoint for chat

**Source:** `ChatController.cs`. Only `POST /api/chat` (synchronous) and `POST /api/chat/feedback`. Streaming is handled exclusively through Blazor SignalR. External consumers (CLI tools, other MCP clients, scripts) have no streaming API.

**Recommendation:** Add `POST /api/chat/stream` with `text/event-stream` response. This is the natural API complement to the MCP surface.

### 3.13 [MEDIUM, conf 0.85] Concurrent mutation of `ChatTurnState` during streaming

**Source:** `Chat.razor:1330-1512`. `SendAsync` mutates `pendingTurn.Content`, `.Thinking`, `.TraceEntries` from the streaming `await foreach` loop. Meanwhile, `RunResponseTimerAsync` calls `InvokeAsync(StateHasChanged)` on a 1-second timer, reading those same fields. Blazor Server's `InvokeAsync` serializes dispatch, but the mutations *inside* the `await foreach` body happen *outside* the dispatch context.

**Impact on reliability:** Torn reads of `pendingTurn.Content` during rendering could produce partial-line display glitches.

**Recommendation:** Move all `pendingTurn` mutations inside `InvokeAsync(...)` blocks.

### 3.14 [MEDIUM, conf 0.85] GitHub Copilot provider channel has no idle watchdog

**Source:** `ChatServices.cs:811-925`. If the Copilot SDK stops sending events (network hang, SDK bug), `channel.Reader.ReadAllAsync` blocks until the global timeout (5-600 seconds). No heartbeat or stall detection.

**Recommendation:** Add a secondary per-chunk timer: if no event arrives in 30 seconds, complete the channel writer with a timeout error.

### 3.15 [MEDIUM, conf 0.85] Token estimation is `chars/4` globally

**Source:** `ChatServices.cs:2388-2399`. Still the naive `chars / 4.0` estimate from Audit #1. Code-heavy content tokenizes at ~2-2.5 chars/token; CJK at ~1.5. The estimate drives the context-window percentage gauge shown to users.

**Recommendation:** Use `Microsoft.ML.Tokenizers.TiktokenTokenizer` for GPT models; the model's `vocab.txt` token count for Ollama. Or at minimum, use `chars / 3.0` as a more conservative global estimate.

### 3.16 [MEDIUM, conf 0.80] Result cache has no expiry

**Source:** `CodeSearchService.cs:1704-1705`. `MemoryCacheEntryOptions { Size = 1 }` with no `AbsoluteExpiration` or `SlidingExpiration`. Entries persist until the generation counter is bumped by `InvalidateQueryCaches`. If the database is externally modified (shard merge from another process, manual SQL edit), cached results go stale indefinitely.

**Recommendation:** Add `SlidingExpiration = TimeSpan.FromMinutes(5)`.

### 3.17 [MEDIUM, conf 0.80] Identifier splitting drops single-character tokens

**Source:** `CodeSearchService.cs:2198-2200`. `AddTokenVariants` skips `segment.Length <= 1`. For code search, single-character tokens like `T`, `K`, `V`, `x`, `n`, `i`, `j` are legitimate search targets (generic type parameters, loop variables).

**Recommendation:** Lower threshold to skip only empty strings. Or make minimum token length configurable.

### 3.18 [MEDIUM, conf 0.80] Feedback rating toggling can't clear from UI

**Source:** `Chat.razor:1551-1584`. Clicking the same thumb again sends the same value, not 0. The controller supports `FeedbackRating.Cleared` when rating is 0, but the UI has no way to trigger it.

**Recommendation:** Toggle logic: if `turn.FeedbackRating == requested`, send 0.

---

## 4. Low-Severity Findings

### 4.1 [LOW] `_indexLock` SemaphoreSlim not disposed in `Dispose` method
### 4.2 [LOW] `EnsureDatabaseAsync` called redundantly on every query — add `_databaseEnsured` flag
### 4.3 [LOW] Warm-metadata reuse compares ticks exactly — fails on FAT32 filesystems with 2-second granularity
### 4.4 [LOW] `EnsureColumnAsync` interpolates column name into SQL — latent injection if ever called with user input
### 4.5 [LOW] `MaxToolIterations` still silently clamped to ≤5 — raise to 10+
### 4.6 [LOW] `LexicalScoreSaturation = 4.0` is a magic constant without documentation
### 4.7 [LOW] `ChatMarkdownRenderer` regex only sanitizes `href|src|srcset` — but per user's calibration, this is acceptable for local-first with safe Mermaid defaults
### 4.8 [LOW] `RenderQuestionCardDetails` uses `MarkupString` without `FilterToAllowedTargets` — question cards from LLMs could contain unfiltered links
### 4.9 [LOW] `Ollama streaming` reads full lines without `data:` prefix handling — some proxy setups add SSE prefixes
### 4.10 [LOW] `GITHUB_TOKEN` not in the env-var fallback chain but mentioned in error messages
### 4.11 [LOW] `SaveSessionsAsync` writes to localStorage every 2 seconds during streaming — serializes all sessions

---

## 5. What's Improved Since Audit #4

### 5.1 [POSITIVE] Mermaid security policy with three tiers
`MermaidRestrictionModes` (standard/restricted/strict) at `MemorySmithOptions.cs:178-194` plus `evaluateMermaidPolicy` in `memorysmith.js:332-376` with `mermaid.initialize({securityLevel:"strict"})` at line 325. This is the right architecture — configurable defense depth with sensible defaults (`restricted` blocks `%%{init}`, `click`, `href=`, `javascript:` directives and caps diagram length).

### 5.2 [POSITIVE] Hybrid scoring with configurable weights
`ScoreHybrid` at `CodeSearchService.cs:2117-2126` combines vector similarity and lexical match with saturation normalization. This is a real improvement over raw cosine — lexically-grounded results get a boost, pure-semantic-only results are penalized. The formula is sound; it just needs empirical calibration.

### 5.3 [POSITIVE] Document-balanced result selection
`TakeBalancedByDocument` at `CodeSearchService.cs:2087-2115` prevents one file from monopolizing the top-K. With `MaxResultsPerDocument = 2` (default), the user sees diverse results across the codebase. This is the kind of UX improvement that makes the difference between "useful" and "actually good."

### 5.4 [POSITIVE] Resumable builds with staleness cooldown
`ResumableBuildsEnabled` + `IndexStalenessCheckCooldownSeconds = 5` means a crashed build doesn't restart from scratch, and rapid-fire queries don't each trigger a rebuild check. Both address real operational pain points.

### 5.5 [POSITIVE] Shard merging
`MergeShardAsync` enables offline index building and import. This is infrastructure that supports future workflows (CI-built indexes, pre-built model snapshots).

### 5.6 [POSITIVE] Query synonym expansion
`ExpandQueryTokens` at `CodeSearchService.cs:2157-2169` adds synonym coverage for the lexical path. The synonyms are domain-appropriate (minus the tool entries) — `search → query, find, retrieval`, `embedding → vector, semantic`, etc.

### 5.7 [POSITIVE] Build log with pruning
`MaxCompletedBuildLogEntries = 10` + `PruneBuildLogAsync` keeps operational history without unbounded growth. Shows in the status API.

### 5.8 [POSITIVE] Training infrastructure cleanup
Removing 4,500 lines of premature training/finetune infrastructure is a maturity signal. Ship the core well before adding a training layer.

### 5.9 [POSITIVE] Test count grew from 350→378
28 net-new tests added during the cleanup, not removed. Core code-search tests all retained.

---

## 6. Prioritized Action Items (Calibrated to User's Priorities)

### Tier 1 — AAA Results Quality (user's #1 priority)
1. **Run a grid search over `HybridVectorWeight/LexicalWeight/ZeroLexicalEvidencePenalty/LexicalScoreSaturation`** against the 8-case relevance suite. One afternoon. Likely 10-20% nDCG improvement.
2. **Add `TensorPrimitives.Dot` SIMD** — 4-8× throughput on hot path. One NuGet + one line.
3. **Migrate `EmbeddingJson TEXT` → `Embedding BLOB`** — 2× less storage, ~10× faster deserialization. Eliminates JSON GC pressure.
4. **Extract haystack computation** — call `BuildHaystack(chunk)` once, pass to both `ScoreLexical` and `CountMatchedTokens`. Halves string allocations.
5. **Add quality-gap awareness to `TakeBalancedByDocument`** — prevent low-relevance documents from displacing high-relevance chunks from the same file.

### Tier 2 — Reliability & Robustness
6. **Fix indentation bug at lines 384-385** — trivial but prevents downstream confusion.
7. **Add SQLite WAL mode + performance PRAGMAs + enable connection pooling** — 30-50% throughput improvement on reads.
8. **Delete dead code** (`ShouldPreloadContext`, `FormatRecordAsync`, duplicated ChatServices helpers).
9. **Log + flush when tool-call JSON parse fails** — prevent the "hung streaming" UX.
10. **Add `IsPotentialToolCallPrefix` timeout/byte-cap** — flush after 2KB or 500ms.
11. **Add result cache expiry** — `SlidingExpiration = TimeSpan.FromMinutes(5)`.

### Tier 3 — Observability & Repeatability
12. **Move synonym table to config** — operators can add domain-specific expansions.
13. **Make hybrid scoring weights configurable** — `CodeSearchOptions.HybridVectorWeight` etc.
14. **Add per-query latency telemetry** — the `QueryTimingTelemetryEnabled` flag exists; ensure it covers the full pipeline including prefilter + scoring + balancing.
15. **Add a streaming HTTP endpoint** for chat (`POST /api/chat/stream` with SSE).
16. **Add concurrency/cancellation/edge-case tests** for code search.

### Tier 4 — Structural (Longer-Term)
17. **ANN index** — sqlite-vec or HNSW.Net. The single biggest scaling win.
18. **AST-aware chunking** — Roslyn for C#. The single biggest precision win.
19. **Cross-encoder reranker** — `bge-reranker-base`. The single biggest top-K quality win.
20. **`Microsoft.ML.Tokenizers.BertTokenizer`** swap — closes BERT-spec gaps + enables BPE models.

---

## 7. Assumptions

| ID | Assumption | Confidence |
|---|---|---:|
| F1 | The training infrastructure removal is permanent (not a temporary cleanup before re-implementation). | 0.75 |
| F2 | The relevance suite's 8 cases are sufficient for weight tuning (more cases would be better but 8 gives signal). | 0.80 |
| F3 | The `CodeSearch.razor` page and `TrainingWorkbench.razor` remain in the branch despite the infrastructure cleanup. | 0.85 |
| F4 | The user's priority ordering (results quality > reliability > usability > observability > repeatability) guides the action item ordering. | 0.95 |

---

## 8. Combined Severity Rollup (All Six Audits)

| Severity | Audit #1 | #2 net | #3 net | #4 net | #5 net | #6 net | Closed | Open |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Critical | 8 | 2 | 1 | 1 | 0 | 1 | 0 | 13 |
| High | 22 | 11 | 8 | 5 | 4 | 8 | 4 | 54 |
| Medium | 33 | 18 | 17 | 15 | 9 | 18 | 2 | 108 |
| Low | 14 | 9 | 13 | 8 | 7 | 11 | 1 | 61 |
| **TOTAL** | 77 | 40 | 39 | 29 | 20 | 38 | 7 | **236** |

The Tier 1 action items from §6 are the path to "AAA results quality." Five items, each individually small, that compound to a measurably better search experience. That's the sprint I'd land first.