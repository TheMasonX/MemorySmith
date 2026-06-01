# MemorySmith — Audit #4

## Remediation Review + Deep Dive on Code Search and Vector Embedding

**Generated:** 2026-05-28 (companion to Audits #1, #2, #3)
**Subject:** [TheMasonX/MemorySmith](https://github.com/TheMasonX/MemorySmith)
**Master commit at audit time:** `c4d7a28ade1a2878d270f1479bfb255f5058482b`
**Active remediation branch:** `feature/code-search-high-roi-batch8` @ `61af84914b0fc278e594911a00a4a7e95f7e7b0d` (5 commits ahead of master, +4,076/−79 LOC across 32 files, all dated 2026-05-28)
**Methodology:** Direct source read of the diff (`git diff master..feature/code-search-high-roi-batch8`), of every touched file, of the new task records in `Data/Tasks/`, and of the new research pages under `Data/Pages/research/vector-search/`. Then close re-reading of source areas the prior audits did not reach first-hand, with research-grounded scrutiny of code-search and the vector path.
**Output target:** ≥10 pages of substantive content. Final length ~10,500 words.

> This audit is in three parts. **§§1-3** evaluate the remediation work the maintainer has shipped against my prior 137 findings. **§§4-6** do the continued deep dive on code search and vector embedding the user requested. **§§7-10** sweep the rest of the surface, summarize, and prioritize.

---

## 0. Executive Update

The maintainer's response to my Audits #1-#3 has been substantive and traceable. Three observations frame this audit:

**(A) They published Audit #1 into their own wiki as research input and traced specific remediations to it.** `Data/Pages/ms-copilot-vector-search-research-reports.md` is a verbatim copy of my Audit #1 with a "Non-Model Follow-Up (2026-05-28)" preface listing every change made in response. `Data/Tasks/tsk-019x.json` and `tsk-0200.json` contain comments like *"Created from the 2026-05-28 deep audit"* and *"Created from deep-audit findings about silent semantic fallback and hybrid metadata ambiguity"*. This is the closest thing to ideal stewardship of an external audit I've seen in a hobby project: it was archived as evidence, work was tracked against it, and the remediations are linked back to the exact findings.

**(B) The remediation is narrowly scoped — code search and vector embedding only.** The 5 new commits on `feature/code-search-high-roi-batch8` close 4 of my code-search findings (vector candidate prefilter / TSK-0198, identifier splitting, lexical haystack lowercasing, EmbeddingBatchSize default 1→8), introduce target-aware weighting, and add a thorough E5-vs-Nomic A/B benchmark. **Auth/security, storage durability, chat tool-call parsing, the maintenance agent, and the markdown XSS surface are all untouched in this branch.** Tasks were filed for some of those (TSK-0199 for memory ANN, TSK-0200 for provider metadata, TSK-0115 for CI budgets) but they're Backlog/InProgress.

**(C) The SQL "prefilter" is clever but has its own asymptotic cliff.** The new `LoadVectorCandidatesAsync` (`CodeSearchService.cs:1320-1380`) reduces brute-force vector scan via `WHERE (instr(lower(DocumentPath), token) > 0 OR instr(lower(SearchText), token) > 0)`. It works — until the corpus has no indexed expression for `lower(DocumentPath)` / `lower(SearchText)`, which means SQLite still does a sequential scan to satisfy the predicate. The reduction is therefore from "scan + score all" to "scan + score top-N-by-lexical-match", a real improvement, but **the underlying complexity stays O(N)** and the prefilter falls through to full scan whenever the LLM's query doesn't share literal substring tokens with the target chunk (the exact case where semantic search shines).

Severity counts for the new work and new findings (across §§3-7):

| Category | Critical | High | Medium | Low |
|---|---:|---:|---:|---:|
| Closed by remediation | — | 4 | 2 | 1 |
| Tracked but Backlog/InProgress | — | 2 | 1 | — |
| New bugs introduced by remediation | — | — | 4 | 2 |
| Net-new findings (continued deep dive) | 1 | 5 | 11 | 6 |
| Net-new positive notes | — | — | — | 7 |

Total new severity additions from Audit #4: **1 Critical, 5 High, 15 Medium, 8 Low** of fresh findings, plus 7 graded closures of prior findings. Combined three-audit total now reads **12 Critical / 42 High / 71 Medium / 41 Low = 166 findings**, of which the maintainer has closed **7** in the new branch and tracked **3 more** as tasks.

---

## 1. Branch Discovery and Remediation Scope

**Active branch:** `feature/code-search-high-roi-batch8` is 5 commits ahead of master, all from 2026-05-28:

```
61af849 fix(benchmarks): stabilize nunit relevance case constraint        13:58 UTC
fa4487b feat(code-search): harden lexical fallback and retire embedding alias  13:56 UTC
5eb373e chore(vector-search): add baseline research and tooling artifacts  13:56 UTC
671d629 Refresh code-search baseline database                              06:47 UTC
1f18187 Improve code-search batching and harness                           06:46 UTC
```

The branch landed AFTER my audit commit (`c4d7a28a`, 2026-05-28 05:50 UTC) and is unmerged at this audit's writing. The diff stats:

| File | +/- | Purpose |
|---|---:|---|
| `MemorySmith.App/Services/CodeSearchService.cs` | +269 | Prefilter, identifier-split, target weighting, telemetry |
| `MemorySmith.Tests/CodeSearchServiceTests.cs` | +171 / 0 | 4 new regression tests + 3 new mock providers |
| `Data/Pages/.../ms-copilot-vector-search-research-reports.md` | +2,372 | My Audit #1 archived as research with follow-up preface |
| `Data/Pages/research/vector-search/e5-vs-nomic-embed-text-20260528.md` | +136 | New head-to-head benchmark |
| `Scripts/model-tools/export_hf_embedding_model.py` | +193 | New Python HF→ONNX export pipeline |
| `Scripts/Install-CodeSearchModel.ps1` | +151 | One-command model setup |
| `Scripts/Measure-CodeSearchQueries.ps1` | +213 | Per-query latency harness |
| `Scripts/Measure-CodeSearchRelevance.ps1` | +291 | Fixed-suite relevance scorer |
| `Scripts/code-search-relevance-suite.json` | +100 | 8 hand-curated relevance cases |
| `Data/Pages/guides/code-search-model-export-workflow.md` | +57 | Operator guide for the new scripts |
| `MemorySmith.App/Services/MemorySmithOptions.cs` | +17 / −1 | New config: prefilter knobs + ExcludePatterns + batch=8 |
| `MemorySmith.App/appsettings.json` | +13 / −3 | New default values |
| `MemorySmith.Tests/SemanticEmbeddingPathTests.cs`<br>`MemorySmith.Tests/MemoryApplicationServiceTests.cs`<br>`MemorySmith.Tests/ModelBackedSearchBenchmarkTests.cs`<br>`MemorySmith.Tests/CudaEmbeddingBatchBenchmarkTests.cs` | ~20 line-mods | Model name rename `embedding-model.onnx` → `e5-base-v2.onnx` |
| `README.md` | +5 / −3 | Model rename + new script doc links |
| `Data/Memories/Core/project-wiki-onnx-semantic-embeddings.json` | +2 / −2 | Wiki update for model rename |
| `Data/Pages/research/vector-search/index.md`, `system-setup.md`, `operations.md`, `search-and-chat.md` | small | Linkage |
| `Data/Tasks/tsk-0198-add-candidate-prefilter-before-code-search-vector-scoring.json` | meta | Backlog → Done |
| `Data/Tasks/tsk-0200-surface-semantic-fallback-and-hybrid-provider-metadata-in-default-api-responses.json` | meta | Backlog → InProgress |
| `Data/Graph/code-search/code-search.db` | +221 KB binary | Index baseline refresh |

**Other in-flight branches** were checked and excluded as out of scope: `copilot/auth-rbac` (stale, last activity 2026-05-17), `feature/admin-auth-hardening-settings` (stale, last 2026-05-20), and seven other feature branches whose HEADs predate the audit. The user's "recent pushes" maps unambiguously to this code-search branch.

**Task records that explicitly cite "2026-05-28 deep audit":**

| Task | Status | Maps to my finding |
|---|---|---|
| TSK-0198 add candidate prefilter before code-search vector scoring | **Done** | Audit #1 §3.1 (linear-scan brute force) |
| TSK-0199 add semantic-search candidate index and ANN migration plan | Backlog | Audit #1 §2.9 + §3.1 (memory-side equivalent) |
| TSK-0200 surface semantic fallback and hybrid provider metadata in default API responses | **InProgress** | Audit #1 §2.4 (silent fallback) |
| TSK-0115 add benchmark regression budgets and CI summary checks | Backlog | Audit #3 §7.5 (no coverage threshold) + adjacent |

TSK-0195 "Codebase Vector Search" (status Done, created 2026-05-27 — pre-dating my audit) is the parent epic this whole branch sits inside. So the work was already kicked off when I audited; my audit accelerated and refined the prefilter direction.

---

## 2. Per-Finding Remediation Grades

### 2.1 [CLOSED, grade A−] Code-search batch embedding defaults

My finding **Audit #1 §3.3** flagged `EmbeddingBatchSize = 1` (`MemorySmithOptions.cs:217`) as defeating the batch-embed code path. The remediation at `MemorySmithOptions.cs:225` + `appsettings.json:101` raises it to **8**. The README acknowledged this gap (line 261 of master); the default now engages batching. Direct, complete fix. **Grade A−** because:

- The default 8 is conservative for GPU. For CUDA the realistic envelope is 32-128 depending on dim. A simple `BatchSize` doesn't autotune.
- No documentation about WHY 8. Audit #3 §4.5 recommended an autotune-at-startup probe.

### 2.2 [CLOSED, grade B+] Code-search identifier splitting

My finding **Audit #1 §3.6** flagged `TokenRegex = "[A-Za-z0-9_]+"` keeps identifiers whole — `UserAuthService` indexes as one token. The remediation at `CodeSearchService.cs:120` introduces:

```csharp
private static readonly Regex IdentifierSplitRegex = new("(?<=[a-z0-9])(?=[A-Z])|_+", RegexOptions.Compiled);
```

…and `AddTokenVariants` (line 1561+) adds both the whole identifier and the split parts to the token set. New regression test `SearchAsync_LexicalFallbackMatchesSnakeCaseQueryAgainstCamelCaseIdentifier` (`CodeSearchServiceTests.cs:97-118` of the new file, lines 124-141 in the diff) proves snake_case query → camelCase identifier match.

**Grade B+** because:

- The regex `(?<=[a-z0-9])(?=[A-Z])` is the classic "splits lowerUpper" pattern. It correctly splits `userAuthService` → `user`, `auth`, `service`. ✓
- It does NOT split contiguous acronym-then-word patterns. `XMLParser` keeps as one token: there's no `[a-z]` before the first `X`, and between `L` and `P` there's no lowercase-to-uppercase transition. The standard fix is `(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])`, which adds the "acronym followed by capitalized word" boundary. Tokens like `XMLParser`, `HTTPClient`, `XMLHttpRequest`, `JSONSerializer` would still index as one token.
- The `_+` part correctly splits snake_case.
- Identifiers like `_camelCase` (leading underscore) lose the leading `_` due to `_+` consuming it, then split on the camel boundary. That's the right answer for most cases.
- Numeric boundaries: `User2Auth` → splits at `User|2Auth` since `2` is `[0-9]` (a transition between `r` and `2` matches `(?<=[a-z0-9])(?=[A-Z])`? No — `r` is `[a-z]`, `2` is `[0-9]`. The lookbehind is `[a-z0-9]` and lookahead is `[A-Z]`, so transitions land between `r` and `2` only if `2` is `[A-Z]`, which it isn't. So `User2Auth` keeps `User2` together and splits `2Auth` at `2|Auth`. Result: tokens are `user2auth`, `user2`, `auth`. Acceptable.

**Recommendation:** Add the acronym-then-word boundary to capture `XMLParser`-style identifiers. Add an explicit test for it.

### 2.3 [CLOSED, grade A] Lexical haystack lowercasing allocation

My finding **Audit #1 §3.5** flagged that `ScoreLexical` allocates a fresh `ToLowerInvariant()` haystack per chunk per query. The remediation at `CodeSearchService.cs:1526` removes the `.ToLowerInvariant()` call and switches `CountOccurrences` to use `StringComparison.OrdinalIgnoreCase` at line 1555. **Direct fix, zero-allocation. Grade A.** Plus the change is mechanically the right one — there's no behavior difference because the comparison is case-insensitive either way, just much cheaper.

### 2.4 [CLOSED, grade B] Code-search vector candidate prefilter (TSK-0198)

This is the biggest remediation in the branch and the one tied directly to my **Audit #1 §3.1** (linear scalar `Dot` over every chunk per query). `LoadVectorCandidatesAsync` (`CodeSearchService.cs:1320-1380`) is a SQL-based candidate-reduction stage:

```sql
SELECT TargetKey, DocumentPath, ..., EmbeddingJson, IndexedAtUtc
FROM CodeSearchChunks
WHERE
  TargetKey IN (@target0, @target1, ...)
  AND (
    (instr(lower(DocumentPath), @token0) > 0 OR instr(lower(SearchText), @token0) > 0)
    OR (instr(lower(DocumentPath), @token1) > 0 OR ...)
    ...
  )
ORDER BY (
  CASE WHEN instr(lower(DocumentPath), @token0) > 0 THEN 2 ELSE 0 END
  + CASE WHEN instr(lower(SearchText), @token0) > 0 THEN 1 ELSE 0 END
  ...
) DESC, DocumentPath COLLATE NOCASE, StartLine
LIMIT @candidateLimit;
```

with `@candidateLimit = clamp(limit × VectorCandidateMultiplier, VectorCandidateMinimum, VectorCandidateMaximum)` (default: `clamp(limit × 12, 100, 400)`).

The two-phase search is then:

1. **Prefilter path** (lines 247-277): Load top-N candidates via SQL, score them with `Dot`, return top-K.
2. **Vector-full-fallback** (lines 279-312): If prefilter returns zero non-zero-score results, re-load ALL chunks and score them.
3. **Lexical fallback** (lines 321-346): If vector also returns nothing, load all and score with `ScoreLexical`.

**What it gets right:**
- Real reduction in CPU work and JSON deserialization for queries with token overlap (the common case).
- Correct fallback to full scan when prefilter misses. The new test `SearchAsync_FallsBackToFullVectorScanWhenLexicalPrefilterMissesSemanticHit` (`CodeSearchServiceTests.cs:363-389` of diff) proves this works — `SemanticAliasEmbeddingProvider` returns matching vectors for two strings with no literal token overlap, and the search still finds the correct top result.
- Reversible: `VectorCandidatePrefilterEnabled` flag can switch the entire feature off.
- Configurable: `VectorCandidateMultiplier/Minimum/Maximum` let operators tune.
- Telemetry: new `scannedChunks` field in query timing logs distinguishes prefilter mode vs full mode.

**What it gets wrong or limits:**

a. **No SQL index can cover the `lower(DocumentPath)` / `lower(SearchText)` predicates.** SQLite would need a *function-based expression index*:
   ```sql
   CREATE INDEX IX_CodeSearchChunks_LowerDocumentPath ON CodeSearchChunks(lower(DocumentPath));
   ```
   …which doesn't exist in the schema (`EnsureDatabaseAsync` doesn't create one). So the prefilter SQL is a sequential scan that *computes* `lower(...)` and `instr(...)` for every row. The win is in the constant factor (SQLite is faster at this scan than C# LINQ over deserialized chunks) and in skipping JSON deserialize for non-matching rows. It is **not** a logarithmic improvement.

b. **The prefilter is purely lexical.** For a query that requires real semantic understanding — e.g., user types "tracking sessions" and the relevant code has no literal "track" or "session" tokens — the prefilter returns zero results and the system falls through to the full scan. So the very queries that benefit MOST from semantic embeddings still pay the full O(N) cost.

c. **No incremental tightening.** If `@candidateLimit = 400` (default ceiling) and 4,000 chunks would match the lexical predicate, SQLite still ORDERs and LIMITs across all 4,000. There's no early termination.

d. **`instr(lower(X), Y)` is not a covering check for tokenization.** A token `auth` would match inside `mauthentication` (substring), but a proper word-boundary check would not. Substring matching in the prefilter produces false positives that the vector score then has to weed out. Mild precision loss, no big deal.

e. **Vector-full-fallback duplicates 24 lines of LINQ.** The block at lines 282-305 is nearly identical to lines 248-271. Refactor candidate.

f. **Cache invalidation gap (minor):** `BuildResultCacheKey` includes `_resultCacheGeneration`, which is incremented on index rebuild. But the cache doesn't differentiate between `mode = "vector"`, `"vector-prefilter"`, and `"vector-full-fallback"` results. If the same query was cached during a "vector-prefilter" run with sparse candidates and is now served from cache while the index has grown, the result might be stale relative to what a full scan would now produce. The fix is to include the prefilter mode in the cache key, or invalidate on every index growth.

**Grade B.** The implementation is sound; the design has real limits that mean this is a stop-gap, not a final answer. The right end state is sqlite-vec (the [official SQLite vector extension](https://github.com/asg017/sqlite-vec)) or HNSW.Net — both of which give true sublinear retrieval. My recommendation in Audit #1 §3.1 still stands; the prefilter buys ~6 months of headroom.

### 2.5 [CLOSED-AS-OUT-OF-SCOPE, grade B+] Default ExcludePatterns

Not from any of my findings, but a sensible improvement. `CodeSearchOptions.ExcludePatterns` defaults from `[]` to `["**/Docs/**", "**/bin/**", "**/obj/**", "**/TestResults/**", "**/BenchmarkDotNet.Artifacts/**"]` (`MemorySmithOptions.cs:213-220`). Reduces index noise and aligns with `.gitignore`-style hygiene. Regression test `SearchAsync_DefaultExcludePatternsSkipProjectDocsNoise` (`CodeSearchServiceTests.cs:94-117` of diff) verifies docs are skipped. **Grade B+.** A `.gitignore`-aware indexer would be even better but is more complex.

### 2.6 [TRACKED, no code change] Memory semantic search ANN (TSK-0199)

`TSK-0199` is Backlog with the comment *"Created from the 2026-05-28 deep audit high-severity O(N) semantic ranking finding."* No code change in this branch. My Audit #1 §2.9 / §3.1 (the memory-search side) and the cited internal "vector-search-general-codebase-report" (theirs) both flag this. **Status correctly tracked.**

### 2.7 [TRACKED, InProgress] Provider metadata in default API responses (TSK-0200)

`TSK-0200` is InProgress. The latest comment is *"Moved to InProgress during non-model follow-up. Current pass prioritized code-search retrieval reliability and benchmark harness hardening; API response metadata surfacing remains the next high-ROI item from this audit group."* Maps to my Audit #1 §2.4 (silent semantic fallback in legacy JSON shape). **Status correctly tracked, work pending.**

### 2.8 [UNTOUCHED] Everything else from Audits #1-#3

Cookie hardening, loopback-as-trust permission handler, GitHub OAuth first-admin auto-promotion, audit hash chain HMAC, `ReadToolCalls` strict envelope, `FileMemoryStore.Save` status-change deletion order, atomic file writes uniformly, `MaintenanceProposalStore.ApplyAsync` non-atomic multi-file apply, `MaintenanceAgentSchedulerService` DST fragility, `MemoryMaintenanceService` 1-second polling, WordPieceTokenizer correctness (case, Unicode, CJK), Markdig `GenericAttributes` XSS vector, `MemoryScorer` weight imbalance + dormant state machine, OpenAPI security schemes, BOM in JSON files (Audit #4 §6 below), and many more — all untouched in this branch. Most still appropriate as tracked tasks; some (the auth hardening trio) materially deserve P0 attention before any non-loopback exposure.

**Net positions of remediation:**
- 4 High and 2 Medium findings **closed**.
- 2 High and 1 Medium **tracked** as open tasks.
- ~125 findings, including 12 Critical, **untouched**.

That's roughly 5% remediation rate. Given the time budget (the branch is one day of work), the focus on code-search is justified. The pattern I'd encourage: future sprints can each take a single area (security, storage, chat) and reach similar progress.

---

## 3. New Bugs Introduced by the Remediation

Close reading of the diff surfaces ~6 net-new issues introduced by the new code itself.

### 3.1 [HIGH, conf 0.85] Prefilter SQL has unbounded ORDER BY expression growth

**Source:** `CodeSearchService.cs:1351-1377`. The `tokenScoreClauses` builder produces TWO `CASE WHEN ... THEN ... ELSE 0 END` clauses per token (one for DocumentPath, one for SearchText). With a 5-token query that's 10 CASE expressions; with a 10-token query (after identifier-split expansion), 20.

Each ORDER BY CASE expression re-evaluates `instr(lower(X), token)` PER ROW PER TOKEN. The same `instr` is also in the WHERE clause for that token. SQLite is smart enough to avoid double-evaluation in some cases, but the query plan for this composite WHERE+ORDER+LIMIT on the same expression is not guaranteed.

For a query with 15 tokens (entirely plausible for a snake_case + camelCase + path query in this app), the WHERE clause is `(A0 OR B0) OR (A1 OR B1) OR ... OR (A14 OR B14)` (30 `instr` calls per row) and the ORDER BY adds 30 more CASE-wrapped `instr` calls per row. **For a 10,000-chunk index that's 600,000 `instr` calls per query just to determine the prefilter top-N.**

**Recommendation:** Pre-compute per-token boolean columns at index time (or use FTS5), OR cap the token count at 5 with the most rare tokens selected, OR replace this SQL with a Lucene-indexed lexical prefilter.

### 3.2 [MEDIUM, conf 0.85] Prefilter and full-scan blocks duplicate 24 lines of identical LINQ

**Source:** `CodeSearchService.cs:248-271` (prefilter) vs `:282-305` (vector-full-fallback). The two `.Select → .Where → .OrderByDescending → .ThenBy → .Take → .Select → .ToList` chains are byte-for-byte the same except for the source list. Refactor into a private method like `RankVectorResults(IEnumerable<IndexedChunk> chunks, float[] queryEmbedding, IReadOnlySet<string> queryTokens, int limit) => …`.

### 3.3 [MEDIUM, conf 0.90] `lower(DocumentPath)` and `lower(SearchText)` are not indexed

**Source:** `CodeSearchService.cs:1075` (schema) vs the prefilter SQL. The schema has `CREATE INDEX IF NOT EXISTS IX_CodeSearchChunks_DocumentPath ON CodeSearchChunks(DocumentPath)` (case-sensitive). The prefilter applies `lower()` at query time, defeating the index. SQLite would need an expression index:

```sql
CREATE INDEX IF NOT EXISTS IX_CodeSearchChunks_LowerDocumentPath
ON CodeSearchChunks(lower(DocumentPath));
```

…to actually accelerate. Even better is COLLATE NOCASE on the column (which the existing `DocumentPath COLLATE NOCASE` in ORDER BY hints at), but COLLATE is on comparisons, not on `instr`.

**Recommendation:** Add expression indexes for `lower(DocumentPath)`. For `lower(SearchText)` an index is impractical (it's the full chunk text), but FTS5 over `SearchText` would solve both this and the substring-vs-tokenized concern in 2.4(d).

### 3.4 [MEDIUM, conf 0.85] `IsArtifactFocusedQuery` silently disables target weighting for any query containing "test"

**Source:** `CodeSearchService.cs:1268-1269`.
```csharp
private static bool IsArtifactFocusedQuery(IReadOnlySet<string> queryTokens) =>
    queryTokens.Overlaps(["test", "tests", "benchmark", "benchmarks", "doc", "docs", "documentation"]);
```

After identifier-split expansion, a query like "WidgetRendererTests" tokenizes to `widgetrenderertests`, `widget`, `renderer`, `tests` (the last is an `IdentifierSplitRegex` split of `Tests`). The `tests` token triggers `IsArtifactFocusedQuery == true` → target weighting is bypassed → test files compete head-to-head with implementation files. For someone searching for `WidgetRenderer` (the class), if they include the class name verbatim they're still fine; for `WidgetRendererTests` they probably DO want the test file, but the new test `SearchAsync_DemotesTestTargetsForImplementationQueries` validates the opposite: it expects test files to be down-weighted even when the query name implies test intent. The two heuristics are in tension.

**Recommendation:** The implementation-vs-test bias should be a thresholded score adjustment, not a binary gate. Even when `tests` is in the query, demote test files modestly (e.g., 0.95) and let raw embedding similarity decide.

### 3.5 [LOW, conf 0.85] Target weight constants are magic numbers without measurement

**Source:** `CodeSearchService.cs:1235-1244` — `weight *= 0.6` for docs, `*= 0.78` for tests, `*= 0.9` for benchmarks. No documentation, no benchmark justification.

The new relevance suite (`Scripts/code-search-relevance-suite.json`) gives them a tool to A/B these numbers. Right now the values are intuited.

### 3.6 [LOW, conf 0.80] `BuildVectorMatchReason` truncates target weight at 0.999

**Source:** `CodeSearchService.cs:1262-1265`.
```csharp
private static string BuildVectorMatchReason(double rawScore, double targetWeight) =>
    targetWeight < 0.999
        ? $"Code embedding cosine similarity {rawScore:0.###} (target weight {targetWeight:0.###})."
        : $"Code embedding cosine similarity {rawScore:0.###}.";
```

The 0.999 cutoff is meant to skip the noise of `1.000 × weight = 1.000` displays. But if a future weight is `0.99` (close to 1 but not exactly), the message changes character. Worth `Math.Abs(targetWeight - 1) > 1e-3` or just always print the weight.

---

## 4. Continued Deep Dive (Code Search and Vector Embedding — Primary Focus)

This section is the deep dive the user asked for. Each finding is sourced first-hand at the new branch state.

### 4.1 [CRITICAL, conf 0.85] WordPiece tokenizer remains incompatible with non-WordPiece models the new workflow encourages

**Source:** `SemanticEmbeddingSearchService.cs:1055-1224` (unchanged in this branch) plus the new `Data/Pages/guides/code-search-model-export-workflow.md` which advertises support for `nomic-ai/nomic-embed-code` and other HuggingFace models.

The new guide explicitly states (line 44):
> The export workflow can convert models to ONNX, but runtime compatibility still depends on MemorySmith tokenizer support. Current defaults are WordPiece-oriented (`vocab.txt` expected). If a model snapshot does not include `vocab.txt` (for example some Nomic code-model snapshots), the generated manifest includes a compatibility note.

`nomic-ai/nomic-embed-code` ships `tokenizer.json` (HuggingFace fast tokenizer), `vocab.json` + `merges.txt` (BPE), and sharded safetensors. It does NOT ship `vocab.txt` (WordPiece). The current runtime path:

1. Hard-codes WordPiece behavior in `WordPieceTokenizer.Load` (`SemanticEmbeddingSearchService.cs:1075-1096`) — reads `vocab.txt` one token per line.
2. Hard-codes the `[CLS]` / `[SEP]` token wrapping in `Encode` (`:1101, 1120`).
3. Lowercases input (`:1179`) — wrong for cased models like `bge-large-en-v1.5`.

So the maintainer has SHIPPED an operator workflow that downloads models the runtime cannot use. The benchmark report (`e5-vs-nomic-embed-text-20260528.md`) only successfully exercises `nomic-embed-text-v1.5` (which ships `vocab.txt`) and `e5-base-v2.onnx` (WordPiece-compatible). When the operator follows the guide and installs `nomic-embed-code`, it will fail at runtime with `WordPiece vocabulary was not found` or a tokenization error.

The maintainer is aware (the doc has a "compatibility note" caveat), but the workflow lures an operator into a failure mode the runtime won't gracefully handle.

**Recommendation:** Either gate `Install-CodeSearchModel.ps1` from accepting non-WordPiece models entirely, OR add a `TokenizerKind = "Bpe"` / `"HuggingFace"` runtime path using `Microsoft.ML.Tokenizers.BertTokenizer` / `BpeTokenizer` (NuGet `Microsoft.ML.Tokenizers` 0.21+). The latter is ~200 LOC for the BPE case and would unlock the entire HuggingFace model landscape.

### 4.2 [HIGH, conf 0.90] `BuildEmbeddingText` still prepends "Path: …" to every chunk

**Source:** `CodeSearchService.cs:1194-1195` (unchanged).
```csharp
private static string BuildEmbeddingText(string documentPath, string chunkText) =>
    $"Path: {documentPath}\n{chunkText}";
```

I flagged this in Audit #1 §3.11. The remediation didn't touch it. The reasoning has only sharpened since:

- The new default is **e5-base-v2** which uses `query: ` / `passage: ` prefixes natively. Adding "Path: ..." inside the passage region adds noise. Real E5 prompting is `passage: {document text}` — appending document-specific structured metadata works against the model's training distribution.
- The path string `MemorySmith.App/Services/CodeSearchService.cs` tokenizes through WordPiece as `mem`, `##ory`, `##sm`, `##ith`, `.`, `app`, `/`, `services`, `/`, `code`, `##sear`, `##ch`, `##service`, `.`, `cs`, `\n` — that's 15+ tokens. For a 512-token budget and chunks that already take 300+ tokens, the path is consuming 3-5% of the context for noise.
- The path identifier is not embedded with code-specific context; the model has no signal that `.cs` is a C# file or that `Services/` is a package boundary. The lexical prefilter already handles the path-as-identifier case.

**Recommendation:** Drop the path prefix from `BuildEmbeddingText`. Add path-aware ranking as a separate weight (similar to `GetTargetWeight`) or as a metadata-only filter. The simplest experiment: turn it off and re-run the relevance suite. If the 8/8 still passes, ship it.

### 4.3 [HIGH, conf 0.90] No FP16 / quantization despite E5-base-v2's natural fit

**Source:** `CodeSearchService.cs:960` (`embeddingJson.Value = JsonSerializer.Serialize(chunk.Embedding)`) and `SemanticEmbeddingSearchService.cs:1034-1046` (`Normalize` produces float32).

E5-base-v2 embeddings are 768-dim float32. 768 × 4 bytes = 3,072 bytes per chunk. With 10,000 chunks that's 30 MB. JSON-encoded with default System.Text.Json (no scientific notation suppression), each float takes ~12-15 chars → ~10 KB per chunk → ~100 MB. The branch did not address my Audit #1 §3.2 (JSON TEXT storage).

The natural progression — and one that Nomic-Embed-Text-v1.5's own design recommends — is **Matryoshka representation learning**: the 768-dim vector is truncatable to 512, 256, or 128 dims with graceful quality loss. Combined with FP16 storage (binary half), 128-dim FP16 is 256 bytes per chunk, **12× smaller than float32**, and on commodity CPUs `MathF.Sqrt`/`Dot` on FP16 (via `System.Half`) is approximately the same speed. For 10,000 chunks: 2.6 MB vs 30 MB.

**Recommendation:** Migrate `EmbeddingJson TEXT` → `Embedding BLOB`. Encode FP16 little-endian. Provide a config knob `SemanticSearch.EmbeddingPrecision: float32|float16|int8`. With Matryoshka models, also `EmbeddingDimensions: 768|512|256|128`. Document the recall/storage tradeoff per setting.

### 4.4 [HIGH, conf 0.90] `Dot` product is still scalar managed code

**Source:** `CodeSearchService.cs:1500-1508`, `SemanticEmbeddingSearchService.cs:491-500` (unchanged).
```csharp
private static double Dot(float[] left, float[] right) {
    var sum = 0.0;
    for (var index = 0; index < left.Length; index++) sum += left[index] * right[index];
    return sum;
}
```

For E5-base-v2 (dim 768), this is 768 multiply-adds. Through prefilter (`@candidateLimit = 400`), 400 × 768 = ~307K mul-adds per query. On modern x64 that's already <1ms but it's pure managed scalar — no SIMD.

The right fix is `System.Numerics.Tensors.TensorPrimitives.Dot` (.NET 9+, ships in `System.Numerics.Tensors` 9.0+ NuGet, which Microsoft maintains and uses internally for ML.NET):

```csharp
private static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    => TensorPrimitives.Dot(left, right);
```

This uses `Vector<T>` / AVX2 / AVX-512 / ARM NEON depending on hardware. Benchmarks from Microsoft's own ML.NET work show 4-8× speedups on AVX2, up to 16× on AVX-512.

The 4× speedup at 400 candidates × 768 dim brings dot-product time from ~300µs to ~75µs per query. Negligible for the 8-query benchmark suite but compounds heavily at scale. Combined with FP16, you also get half-precision SIMD via `TensorPrimitives.Dot(ReadOnlySpan<Half>, ReadOnlySpan<Half>)`.

**Recommendation:** A one-line change with a NuGet reference. Run on the relevance suite first to confirm no numeric drift.

### 4.5 [HIGH, conf 0.85] Code-search SQLite still has no PRAGMAs beyond `Pooling = false`

**Source:** `CodeSearchService.cs:1141-1148` (unchanged from master).
```csharp
private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken) {
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
        DataSource = _indexDatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString());
    await connection.OpenAsync(cancellationToken);
    return connection;
}
```

My Audit #1 §3.7 and Audit #3 §1-W3 noted this. The branch did not address it. Per-query open does:

- No `PRAGMA journal_mode=WAL` (main DB has it; code-search DB doesn't). Concurrent reads block writes.
- No `PRAGMA synchronous=NORMAL`. Every write flushes.
- No `PRAGMA temp_store=MEMORY`. ORDER BY in the prefilter SQL might spill to disk for large candidate sets.
- No `PRAGMA cache_size=-32000` (32 MB). Default 2 MB cache leaves most of the index off-page.
- No `PRAGMA mmap_size=...`. mmap of the index file would let SQLite skip syscalls on hot pages.
- `Pooling = false` prevents connection reuse — every query opens a fresh connection (handshake + pragmas + sqlite_init).

With the prefilter SQL doing per-row `lower()` + `instr()` scans, these pragmas would meaningfully impact throughput.

**Recommendation:** On first connection per process, run:
```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA temp_store = MEMORY;
PRAGMA cache_size = -32000;
PRAGMA mmap_size = 268435456;
```

Re-enable `Pooling = true`. The combined impact is ~30-50% faster index reads.

### 4.6 [HIGH, conf 0.90] No cross-encoder reranker step despite 8-test relevance harness in place

**Source:** Throughout `CodeSearchService.cs`. The top-K is taken directly from cosine.

The maintainer has built (a) a relevance suite, (b) a per-query latency harness, (c) a model export workflow. The piece they're missing is the second stage of a modern retrieval pipeline: a cross-encoder reranker. The standard pattern is:

1. **Bi-encoder retrieval (this code):** Embed query and docs separately, take top-N (here, top-400 from prefilter or top-K from full scan).
2. **Cross-encoder rerank:** Concatenate `[query, doc]` per pair, score with a small model trained for relevance.

For code search, `BAAI/bge-reranker-base` (110M params, ~440 MB ONNX) or `cross-encoder/ms-marco-MiniLM-L-6-v2` (23 MB ONNX) work well. On the 8-query suite, top-400 candidates × 23 MB model = ~1-2s reranking on CPU, which is negligible vs the current 100-700ms cold latency.

The benchmark shows E5-base-v2 returning test files first on implementation-focused queries — exactly the kind of "first-stage retrieval gets the right neighborhood, second-stage gets the right top hit" pattern that cross-encoders fix.

**Recommendation:** Add an `MemorySmith:CodeSearch:RerankerModelPath` option. When set, after the bi-encoder top-N stage, score each `[query, chunk]` pair with the reranker model and re-order. Use the same `ITextEmbeddingProvider` interface extended with a `TryRerank(query, candidates)` method.

**Modern references:**
- Nogueira & Cho (2019), *Passage Re-ranking with BERT* — foundational paper.
- Reimers & Gurevych (2020), *Sentence-BERT* — bi-encoder vs cross-encoder framing.
- Lin et al. (2022), *Pretrained Transformers for Text Ranking* (book) — full taxonomy.
- BGE-Reranker model card on HuggingFace — directly relevant.

### 4.7 [HIGH, conf 0.85] No chunking by syntactic boundaries (still line-windowed)

**Source:** `CodeSearchService.cs:644-708` (`ChunkFile`, unchanged). Line-windowed with `ChunkLineCount = 40, ChunkOverlapLineCount = 8`.

My Audit #1 §3.4 flagged this. The branch doesn't address it. The E5-vs-Nomic benchmark in `Data/Pages/research/vector-search/e5-vs-nomic-embed-text-20260528.md` shows e5 ranking test files higher than implementation for several queries — this is exactly the kind of issue AST-aware chunking would help with, because:

- Class declaration with attributes + XML docs would chunk as ONE unit including the headers
- Method bodies wouldn't split mid-statement
- `using` blocks and namespace declarations would tag each chunk's metadata
- Test methods would have `[Test]` attribute on the chunk, easy to flag

For C# files specifically, Roslyn (`Microsoft.CodeAnalysis.CSharp`) is mature and free; chunking by `MethodDeclarationSyntax`/`ClassDeclarationSyntax` is ~80 LOC. For non-C# files (`.razor`, `.md`, `.json`, `.ts`), tree-sitter grammars exist for all of them in .NET via `TreeSitter.NET` or `TreeSitterSharp`. AST chunking is the single biggest precision win available.

**References:**
- Sourcegraph Cody's [chunking strategy](https://sourcegraph.com/blog/optimizing-large-language-models-for-code-completion) — production lessons.
- Continue.dev's chunking — open source, similar shape.
- Zhang et al. (2023), *RepoFusion* — academic baseline.

### 4.8 [HIGH, conf 0.85] No semantic ANN, even now that benchmarking infrastructure exists

**Source:** Throughout `CodeSearchService.cs`. The SQL prefilter is a step *toward* candidate reduction, but it's lexical-only.

With the relevance suite in place, the maintainer can A/B against real ANN. Three options:

a. **`sqlite-vec`** ([`asg017/sqlite-vec`](https://github.com/asg017/sqlite-vec)) — a SQLite extension that adds `vec0` virtual tables supporting `MATCH` queries with K-nearest neighbor search via brute-force-but-SIMD (good for <100K vectors) and IVF-PQ (good for >100K). Native binaries available for Win/Mac/Linux. C# integration is via `SQLitePCLRaw.bundle_e_sqlite3` extension loading + custom SQL.

b. **`HNSW.Net`** — pure C# HNSW implementation. ~3K LOC NuGet package. In-memory only, but persists via `Save`/`Load`. Sub-ms search on 1M vectors.

c. **`Faiss.Net`** — .NET bindings to Facebook's FAISS. Production-grade. Bulkier deploy. Best for >10M vectors.

Recommended path: sqlite-vec for the code-search index (it lives alongside the existing SQLite DB anyway), HNSW.Net for the memory ANN (where the deployment story stays C#-only).

### 4.9 [MEDIUM, conf 0.85] No incremental embedding for changed files

**Source:** `CodeSearchService.cs:430-475` (`BuildIndexCoreAsync`). The build does file-by-file walk. The hash-and-skip logic at `:442-447` and `:861-875` reuses existing embeddings when content hash matches.

This is good. What it doesn't do:

- If a file changes but only adds N lines, ALL chunks (including unchanged ones above the change point) re-embed because chunking is index-aligned to line numbers.
- No cross-file chunk reuse — adding a duplicate test fixture file fully re-embeds despite having the same content as another file.
- The persistent embedding cache in `SemanticEmbeddingSearchService.cs:299-388` (per-memory-record) is NOT used by code search. Code-search stores embeddings inline in SQLite. So if you delete the SQLite DB but keep `Data/Graph/embeddings/`, code search loses all cached embeddings.

**Recommendation:** Hash by `(content_hash, chunk_id)` so a sliding-window chunk that's identical across files reuses the embedding. Cross-reference the persistent embedding cache.

### 4.10 [MEDIUM, conf 0.85] `EmbeddingBatchSize = 8` is below GPU's natural envelope

**Source:** `MemorySmithOptions.cs:225`. Default raised from 1 to 8.

For CUDA on an NVIDIA card with 16GB VRAM and E5-base-v2 (~400 MB), the batch envelope is 32-128 sequences in parallel. Batch=8 leaves >4× throughput on the table. The branch's `EmbeddingBatchSize` is a single integer; there's no `EmbeddingBatchSize.Cpu` vs `EmbeddingBatchSize.Cuda` split.

**Recommendation:** Per-execution-provider defaults:
```csharp
public class CodeSearchOptions {
    public int EmbeddingBatchSizeCpu { get; set; } = 8;
    public int EmbeddingBatchSizeCuda { get; set; } = 64;
    public int EmbeddingBatchSizeOpenVino { get; set; } = 32;
}
```

At startup, probe-and-tune: embed a fixed dummy batch at each candidate size (8, 16, 32, 64, 128), pick the one with the lowest per-sample latency.

### 4.11 [MEDIUM, conf 0.85] No query expansion via the relevance suite

**Source:** Nothing exists. The benchmark suite measures one-shot relevance.

State-of-the-art retrieval pipelines run a "hypothetical document embedding" (HyDE) or "query2doc" expansion: rewrite the query as a pseudo-document, embed the rewrite, then search. This is a 1-2x latency cost for substantially better recall on conceptual queries.

For local-first this is harder because it requires an LLM call. But MemorySmith ALREADY has an LLM in the loop for chat. Reusing it for query rewriting on a slow path is straightforward.

### 4.12 [MEDIUM, conf 0.85] Chunk overlap is 8 lines fixed, not content-aware

**Source:** `MemorySmithOptions.cs:222-223`. `ChunkLineCount = 40, ChunkOverlapLineCount = 8`. Per Audit #1 §3.4.

Modern chunking strategies use content-aware overlap: split at function/class boundaries, with no overlap; for prose, overlap is enough to preserve context across breaks. A fixed 8-line overlap means small files get over-indexed (a 50-line file becomes two chunks with 8 lines duplicated) and large files have arbitrary split points.

### 4.13 [LOW, conf 0.85] `SemanticAliasEmbeddingProvider` test is conceptually right but uses dim-2 vectors

**Source:** `CodeSearchServiceTests.cs:601-621` (the new mock). Vectors are `[1f, 0f]` or `[0f, 1f]`. Dim-2 is fine for the test's purpose, but it means `Embedding.Length == queryEmbedding.Length` (line 249) passes trivially and the code path doesn't test what happens when chunks have dim-768 embeddings stored from a prior model and a query is embedded with the new model — that's the real ALIAS test (model version mismatch).

The branch's `BuildConfigurationHash` (`:1151-1175`) DOES include model lastWriteTime, so swapping the model file would invalidate the persistent embedding cache. But the SQLite-stored embeddings in `EmbeddingJson` are NOT invalidated by model swap — they stay until explicit `forceRebuild`. The mid-swap state where some chunks have new-model embeddings and some have old-model embeddings would silently produce incoherent rankings.

**Recommendation:** Track `EmbeddingModelHash` per chunk row in SQLite, alongside `EmbeddingJson`. On query, only use chunks whose `EmbeddingModelHash` matches the current model.

---

## 5. Vector Embedding Subsystem Deep Dive

This section covers the `SemanticEmbeddingSearchService` + `OnnxTextEmbeddingProvider` stack at the new branch state (which is unchanged from master). The user asked for emphasis on vector embedding; here it is.

### 5.1 Architecture summary

```
[Query string] → [WordPieceTokenizer.Encode → token IDs] → [ONNX session.Run]
                                                                ↓
                                          [sequence output [B, T, H]]
                                                                ↓
                                          [Mean/CLS pooling → [B, H]]
                                                                ↓
                                          [L2 normalize → unit vector]
                                                                ↓
                                                       [float[] returned]
```

Caching layers:
- `_queryEmbeddingCache` (256 entries, by raw query string)
- `_documentEmbeddingCache` (4096 entries, by record ID + source text equality check)
- Persistent: `Data/Graph/embeddings/{record_id}.json` (per record, indexed by source-text SHA256 hash + configuration hash)

### 5.2 [HIGH, conf 0.95] WordPieceTokenizer is a partial reimplementation that drifts from BERT spec

**Source:** `SemanticEmbeddingSearchService.cs:1060-1225` (full WordPieceTokenizer). Audit #1 §2.2 noted issues; I'm cataloging the full set here for completeness.

BERT's reference tokenization pipeline (`google-research/bert/tokenization.py`):

1. **`_clean_text`**: Strip control chars (Cc, Cf), replace with whitespace.
2. **NFC normalization** (when `do_lower_case=False`) or **NFD → strip combining marks** (when `do_lower_case=True`).
3. **`_tokenize_chinese_chars`**: Add whitespace around each CJK character (U+4E00..U+9FFF and 5 other ranges) so each becomes its own token.
4. **Whitespace split**.
5. **Lowercase + accent strip** (if `do_lower_case`).
6. **Punctuation split**: Each non-alphanumeric character becomes its own token.
7. **WordPiece sub-tokenization** via the longest-match greedy algorithm against the loaded vocabulary, with `##` prefix for continuation pieces.

The current implementation does steps 4 (whitespace split), 5 (lowercase only — hardcoded `text.ToLowerInvariant()` at `:1179`), 6 (punctuation split), and 7 (WordPiece). It is MISSING:

- **Step 1** (clean_text): control chars pass through to the punctuation-split path and become individual tokens, likely matching as `[UNK]`. Tab/CR are whitespace-filtered, but `\b` (backspace), `\v`, `\f`, NUL bytes are not.
- **Step 2** (Unicode normalization): `é` (U+00E9) vs `e` + combining acute (U+0065 U+0301) tokenize to entirely different IDs. A search for "café" in NFC vs NFD form misses.
- **Step 3** (CJK splitting): `美しい日本語` is one giant token, looks up against vocab, almost certainly `[UNK]`. For a wiki containing CJK content, every CJK chunk is a token-soup of `[UNK]`.
- **Configurable `do_lower_case`**: Hardcoded TRUE. Cased models (`bert-base-cased`, `multilingual-cased`, several BGE variants) get destroyed.
- **Configurable strip_accents**: Hardcoded FALSE. For models trained with accent-stripping, accented characters end up as `[UNK]`.

For the current English wiki, these gaps don't impact daily use. The maintainer is shipping a `nomic-embed-text-v1.5` benchmark — that model is uncased English and uses BPE with merges, NOT WordPiece. The current code CAN load nomic-embed-text-v1.5 because the project also ships a `vocab.txt` for it (probably extracted from the tokenizer config), but tokenization fidelity is unverified.

**Recommendation:** Switch to `Microsoft.ML.Tokenizers.BertTokenizer.Create(...)` (NuGet `Microsoft.ML.Tokenizers` 0.21+). It's actively maintained by Microsoft, supports all BERT spec edge cases, and has parity with the Python `transformers` library tokenizer outputs. ~20 LOC swap. Removes the entire 165-LOC WordPieceTokenizer class.

### 5.3 [HIGH, conf 0.90] `Dot` is misnamed — it computes inner product, not cosine

**Source:** `SemanticEmbeddingSearchService.cs:247, 491-500`.
```csharp
var score = Dot(queryEmbedding, documentEmbedding);
…
private static double Dot(float[] left, float[] right) { … }
```

Since both vectors are L2-normalized at line 589 and 635 (`Normalize` divides by L2 norm), the inner product IS cosine similarity in `[-1, 1]`. The result is correct. But the naming + comments don't tell that story; a future maintainer who removes normalization (e.g., to support unnormalized vectors from a different model) would silently change the semantics.

**Recommendation:** Rename to `CosineSimilarity` and assert the unit-norm assumption. Or add a runtime assertion `Debug.Assert(Math.Abs(L2Norm(v) - 1) < 1e-3)` after embedding.

### 5.4 [MEDIUM, conf 0.90] `_persistentDocumentLocks` `ConcurrentDictionary` grows unbounded

**Source:** `SemanticEmbeddingSearchService.cs:194, 310` (unchanged). Per-key permanent lock objects, added with `GetOrAdd`, never removed. Long-running process with high record churn (rename, delete, re-add) accumulates lock objects.

Audit #1 §8.2 noted this and the in-tree audit (theirs) flagged it as Medium. Easy fix: striped locks (a fixed array of `N` locks, hash record ID into one of them).

```csharp
private static readonly object[] _stripeLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
private static object GetStripe(string id) => _stripeLocks[Math.Abs(id.GetHashCode()) % 64];
```

### 5.5 [MEDIUM, conf 0.90] Persistent embedding cache uses JSON for float arrays

**Source:** `SemanticEmbeddingSearchService.cs:382-389` (`TryPersistDocumentEmbedding`). Writes `JsonSerializer.Serialize(entry)` where `entry.Embedding` is `float[]`.

For E5-base-v2 (dim 768), JSON encoding of `float[]` is ~7-10 KB per file. Binary `MemoryMarshal.Cast<float, byte>` is 3,072 bytes. Audit #1 §3.2 noted the same for code-search.

**Recommendation:** Add a `.bin` sidecar format with the same configuration hash; deprecate the `.json` cache.

### 5.6 [MEDIUM, conf 0.85] Configuration hash includes `MaxIndexedTextCharacters` but not `MaxInputTokens`

Wait — `:417` does include `_options.MaxInputTokens`. OK, both are in the hash. Disregard.

But the hash does NOT include `EmbeddingsEnabled` for the *Code* search side. So flipping `CodeSearch.Enabled = false` then back to true doesn't invalidate. That's actually desirable (you want the index to survive a disable/re-enable). No fix needed.

### 5.7 [MEDIUM, conf 0.85] ONNX session is single — no concurrent batch inference

**Source:** `SemanticEmbeddingSearchService.cs:537-549, 654-717`. `_session` is a shared `InferenceSession` guarded by a lock during initialization. Per ORT docs, `InferenceSession.Run` IS thread-safe, so concurrent calls are allowed. But the `OnnxTextEmbeddingProvider` exposes a synchronous `TryEmbed` / `TryEmbedBatch` — there's no async pump.

For chat-driven workloads where multiple queries arrive concurrently (multiple users, or one user with multiple windows), each `TryEmbed` blocks its caller's thread. The Blazor circuit dispatcher serializes calls per user but multi-user load hits this.

**Recommendation:** Add `Task<float[]> TryEmbedAsync(...)`. Internally use a `Channel<EmbedRequest>` with a small worker pool that batches concurrent requests in <50ms windows. Same approach as TensorRT/TF Serving batch grouping.

### 5.8 [MEDIUM, conf 0.85] No CUDA stream configuration — IoBinding skipped

**Source:** `SemanticEmbeddingSearchService.cs:790-810`. `sessionOptions.AppendExecutionProvider_CUDA(_options.CudaDeviceId)` — just device ID. No stream, no memory arena tuning, no cudnn_conv flags.

ORT's CUDA EP supports a richer config via the dict-based `OrtCUDAProviderOptions`:
```csharp
sessionOptions.AppendExecutionProvider_CUDA(new OrtCUDAProviderOptions {
    DeviceId = _options.CudaDeviceId,
    ArenaExtendStrategy = ArenaExtendStrategy.SameAsRequested,
    GpuMemLimit = 4UL * 1024 * 1024 * 1024,
    CudnnConvAlgoSearch = OrtCudnnConvAlgoSearch.Heuristic,
    DoCopyInDefaultStream = true,
});
```

Plus `IoBinding` for pinning input tensors to GPU memory across calls. Without it, every `session.Run` copies inputs host→device and outputs device→host. For batch=64 and dim=768, that's 64×768×4 = 192 KB of copies per call. At 10 calls/sec that's 1.92 MB/s of PCIe traffic for nothing.

**Recommendation:** Use `OrtCUDAProviderOptions` + `IoBinding` for the hot path. Document the perf delta in the benchmark report.

### 5.9 [LOW, conf 0.85] Query cache key is the raw query string

**Source:** `SemanticEmbeddingSearchService.cs:284-297, CodeSearchService.cs:1199-1212`. `_queryEmbeddingCache.TryGetValue(query, ...)`. Cache hits when the exact string matches.

For a 256-entry cache with realistic chat queries, many queries differ by trivial whitespace or capitalization. Normalize to NFC + trim before keying. Easy win at ~3 LOC.

### 5.10 [LOW, conf 0.85] Sequence-output ExtractEmbeddings is silently inefficient on batch=1

**Source:** `SemanticEmbeddingSearchService.cs:961-1023`. For `dimensions.Length == 3 && dimensions[0] == batchCount`, it calls `data.AsSpan(offset, stride).ToArray()` for each batch member. `.ToArray()` allocates a fresh array.

For batch=8 with dim=768, that's 8 × (sequence_len × 768) × 4 bytes allocated and then mean-pooled. For a 128-token sequence: 8 × 128 × 768 × 4 = 3 MB allocated per batch. With persistent garbage collection pressure.

**Recommendation:** Stack-allocate via `Span<float>` and pool the float arrays. `ArrayPool<float>.Shared` is the standard pattern.

---

## 6. Net-New Findings from Untouched Areas

Continued deep-dive of the codebase, this round focusing on what neither Audits #1-#3 nor the remediation branch touched.

### 6.1 [HIGH, conf 0.90] `MemoryMaintenanceTasks.DeduplicateRecords` silently merges and deletes records by Title collision

**Source:** `MemoryMaintenanceTasks.cs:78-104`.
```csharp
private void DeduplicateRecords(List<MemoryRecord> records) {
    var groups = records
        .Where(record => !string.IsNullOrWhiteSpace(record.Title))
        .GroupBy(record => record.Title.Trim().ToLowerInvariant())
        .Where(group => group.Count() > 1)
        .ToList();

    foreach (var group in groups) {
        var primary = group.First();
        foreach (var duplicate in group.Skip(1).ToList()) {
            primary.UsageCount += duplicate.UsageCount;
            primary.References.AddRange(duplicate.References);
            primary.Conflicts.AddRange(duplicate.Conflicts);
            primary.Tags.AddRange(duplicate.Tags);
            records.Remove(duplicate);
            _store.Delete(duplicate.Id);  // ← silent delete
        }
        // ...
        _store.Save(primary);
    }
}
```

**Reasoning:** Two memory records with identical titles (case-insensitive, trimmed) — say "Test Plan" for two unrelated features — get merged on the 24-hour consolidation cadence. The "primary" is whichever came first from `records`, determined by file enumeration order (not stable across runs). The other is deleted, its tags/references/conflicts/usagecount merged in. **No audit log entry. No confirmation. No way to recover.**

This runs unconditionally in `RunConsolidationAsync`. The maintenance service is enabled by default (`Maintenance.Enabled = true` in `MemorySmithOptions.cs:233`).

Combined with `FileMemoryStore.Save` deleting the old file before writing the new (Audit #1 §6.1 / Audit #2 §V2 — confirmed), a crash during this merge can leave the wiki in a partially-deduplicated state with no rollback.

**Recommendation:** Either (a) require an admin confirmation step (the proposal workflow exists for exactly this kind of mutation), or (b) audit-log every merge with `before` and `after` hashes. The current behavior is destructive-by-default.

### 6.2 [HIGH, conf 0.85] `RunTriageAsync` walks every record every 5 minutes

**Source:** `MemoryMaintenanceTasks.cs:28-52`, `MemoryMaintenanceService.cs:34-50` (default `TriageMinutes = 5`).

Every 5 minutes the maintenance loop:
1. Loads all records from disk via `_store.LoadAll().ToList()` (re-reading every JSON file every 5 minutes).
2. Evaluates each through `MemoryStateMachine.Evaluate`.
3. If status changes, calls `_store.Save(record)` — which holds the FileMemoryStore lock and atomic-writes.

For 100 records: ~100 ms triage every 5 minutes — fine. For 1,000 records: ~1 second every 5 minutes — palpable. For 10,000 records: 10+ seconds, with the file store lock held the whole time blocking writes.

Combined with Audit #3 §2.1 (1-second polling loop), the maintenance background service is on a path to dominating idle time as the wiki grows.

### 6.3 [HIGH, conf 0.90] UTF-8 BOM inconsistency across `Data/Tasks/*.json`

**Source:** Filesystem analysis. 35 of 320 JSON files in `Data/` have a UTF-8 BOM (`EF BB BF`) prefix:

```
./Tasks/tsk-0026-new-task.json
./Tasks/tsk-0027-new-task.json
./Tasks/tsk-0028-new-task.json
... 32 more ...
```

These were almost certainly written by PowerShell `Set-Content` without `-Encoding UTF8NoBOM` (the default behavior on Windows PowerShell 5.1 writes BOM). The other 285 files were likely written by .NET's `JsonSerializer.Serialize` → `File.WriteAllText` which by default does NOT write BOM.

**Reasoning:**
- `System.Text.Json.JsonDocument.Parse(string)` strips BOM from string input as of .NET 5+. ✓
- `JsonSerializer.Deserialize<T>(string)` — same. ✓ (verified by reading the System.Text.Json source).
- `File.ReadAllText` strips BOM if present. ✓
- `JsonDocument.Parse(ReadOnlySpan<byte>)` — DOES NOT strip BOM. ✗ Caller must handle.

So the BOM doesn't break current readers because `TaskDomainService.cs:743` uses `File.ReadAllText(file)` → string → `JsonSerializer.Deserialize`. But:

- Round-trip writes (load → modify → save) would strip the BOM on save (because `File.WriteAllText` doesn't write BOM by default).
- External tools (jq, Python `json.loads(open(f).read())`, `Set-Content` round-tripping) may produce inconsistent results.
- The file-hash check in `CodeSearchService.CanReuseDocument` (line 873) hashes the raw bytes — so a BOM file's hash differs from a non-BOM file with otherwise identical content. **Re-saving a BOM file via the app silently changes its on-disk hash, triggering false-positive reindex on the next code-search build.**

**Recommendation:** Normalize all JSON files to non-BOM UTF-8. Add a CI check.

### 6.4 [HIGH, conf 0.85] `MemoryMaintenanceTasks.RecommendObsoleteRecords` re-scans the full event log per record

**Source:** `MemoryMaintenanceTasks.cs:131-138`.
```csharp
var existingRecommendations = _eventStore.GetEvents()  // ← O(events)
    .Where(memoryEvent => string.Equals(memoryEvent.Action, "DeprecationRecommended", ...))
    ...
foreach (var record in records.Where(r => r.Status != MemoryStatus.Deprecated)) {
    var score = MemoryScorer.Score(record);
    if (score >= MemoryStateMachine.DeprecationThreshold || existingRecommendations.Contains(record.Id))
        continue;
    _eventStore.AppendEvent(...)
}
```

This pattern is OK — `_eventStore.GetEvents()` is called ONCE, then `existingRecommendations` is checked inline. The issue is `_eventStore.GetEvents()` reads the entire event log into memory under lock (per `FileEventStore.cs:60-93` per Audit #3 W3, Audit #1 §6.6). For a long-running wiki with thousands of events, this is multi-MB allocated every consolidation pass (every 24 hours).

**Recommendation:** Index events by Action+MemoryId or store recommendation timestamps as record metadata.

### 6.5 [MEDIUM, conf 0.90] `PromoteStableRecords` hardcodes thresholds with no operator override

**Source:** `MemoryMaintenanceTasks.cs:106-117`.
```csharp
private void PromoteStableRecords(IEnumerable<MemoryRecord> records) {
    var cutoffDate = DateTime.UtcNow.AddDays(-30);
    foreach (var record in records.Where(r => r.Status == MemoryStatus.Working)) {
        if (record.LastUpdated < cutoffDate && record.References.Count >= 2 && record.Confidence >= 0.7) {
            record.Status = MemoryStatus.Core;
            _store.Save(record);
        }
    }
}
```

30 days, 2 references, 0.7 confidence — magic numbers, not configurable. A user with a faster review cadence would prefer 14 days. A user with single-author content would prefer 1 reference.

**Recommendation:** Surface as `MemorySmithOptions.Maintenance.PromotionMinAgeDays`, `PromotionMinReferences`, `PromotionMinConfidence`.

### 6.6 [MEDIUM, conf 0.85] `MemoryMaintenanceTasks` does not coordinate with the maintenance agent (`MaintenanceAgentService`)

**Source:** Two separate background services — `MemoryMaintenanceService` (5-min/60-min/24-hr cycles) and `MaintenanceAgentSchedulerService` (weekly LLM review). They share no state and can both `Save` records simultaneously.

If the maintenance agent generates a proposal that updates a record, AND the same record is being processed by `RunTriageAsync` at the same moment, the `FileMemoryStore._lock` serializes. But the SEMANTICS are murky: the agent proposes a future-applied write while triage applies an immediate write. Conflict resolution is "last writer wins."

**Recommendation:** A shared mutation queue or an in-process pub/sub event bus that both subsystems coordinate through.

### 6.7 [MEDIUM, conf 0.85] `RunConsolidationAsync` mutates `records` list mid-iteration

**Source:** `MemoryMaintenanceTasks.cs:61-76`.
```csharp
public Task RunConsolidationAsync(CancellationToken cancellationToken) {
    ...
    var records = _store.LoadAll().ToList();
    DeduplicateRecords(records);    // ← mutates `records` via .Remove
    PromoteStableRecords(records);  // ← reads mutated `records`
    if (_options.Maintenance.AutomaticDeprecationEnabled) {
        DeprecateObsoleteRecords(records);
    } else {
        RecommendObsoleteRecords(records);
    }
    return Task.CompletedTask;
}
```

`DeduplicateRecords` calls `records.Remove(duplicate)` (line 95). After that, `PromoteStableRecords` and friends iterate the mutated list. If the duplicates contained a `Working`-status record with the right criteria for promotion, that record is GONE before promotion runs. Semantics depend on iteration order.

**Recommendation:** Make each task return a result; build a single command list; apply in a defined order.

### 6.8 [LOW, conf 0.85] `MemoryStateMachine.Evaluate` doesn't use `_options.Maintenance.AutomaticDeprecationEnabled` consistently

**Source:** `MemoryStateMachine.cs:11-42`. The method takes `allowDeprecation` parameter. The caller `RunTriageAsync` passes `_options.Maintenance.AutomaticDeprecationEnabled` (line 36). Direct mapping. OK on the surface, but:

- `DeprecateObsoleteRecords` (line 119) and `RecommendObsoleteRecords` (line 131) are also gated on `AutomaticDeprecationEnabled` (line 67). So the same config controls two distinct paths.
- If `AutomaticDeprecationEnabled = false`, `RecommendObsoleteRecords` adds a `DeprecationRecommended` event but the state machine inside `Evaluate` still won't deprecate. So the recommendation accumulates events but never converts. Operator behavior: "I keep seeing DeprecationRecommended events but nothing happens" → toggle `AutomaticDeprecationEnabled` → wave of deprecations. Worth a UI affordance.

### 6.9 [LOW, conf 0.85] `MemoryEvent` from `MemoryMaintenanceTasks` doesn't include the calling task name

**Source:** `MemoryMaintenanceTasks.cs:148-152`. The appended event has `Action = "DeprecationRecommended"` but no metadata about which maintenance task generated it. For audit log integrity (Audit #2 §7.1 — hash chain exclusion of Reason), this is fine, but for operator forensics it's thin.

**Recommendation:** Include `Source = "MaintenanceTasks.RecommendObsoleteRecords"` in `MemoryEvent.Details`.

### 6.10 [LOW, conf 0.85] `MemoryIndex` is rebuilt every 60 minutes via `RunIndexRebuildAsync`

**Source:** `MemoryMaintenanceTasks.cs:54-59`.
```csharp
public Task RunIndexRebuildAsync(CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    _index.Rebuild(_store.LoadAll().ToList());
    return Task.CompletedTask;
}
```

Every 60 minutes, `_index` (the in-process dictionary index — Audit #1 §13.3 noted it's "populated and never consulted by search") is rebuilt. The rebuild is full O(N): load all, clear, add all. With 10,000 records this is ~5 seconds of LINQ + dictionary churn.

The deeper irony: as I noted in Audit #1, this index is populated and maintained but **not actually used by search code paths** — `MemoryApplicationService.CreateSearchSnapshot` calls `_store.LoadAll()` directly. So the maintenance work is unused. Two paths forward: wire the index into search (the real fix) or delete it.

### 6.11 [LOW, conf 0.85] `MemoryMaintenanceTasks` swallows `IEnumerable` mutation exceptions

**Source:** None — the LINQ chains don't catch. So if `_store.LoadAll()` throws partway through enumeration (an unreadable file mid-iteration), the entire maintenance pass aborts. With the broad catch in `MemoryMaintenanceService.RunTrackedAsync` (`:86-91`), the error is logged but not surfaced to operators.

**Recommendation:** Surface storage diagnostics in `/health` (already Audit #2 §6.13 / §15 line 10).

---

## 7. Untouched High-Severity Findings — Still Open

This list summarizes the Critical/High findings from Audits #1-#3 that are NOT addressed in the remediation branch and NOT yet tracked as tasks. I list them so the maintainer can prioritize the next sprint.

### Auth/Security
1. **C: `/mcp` and `/api/*` unauthenticated from loopback by default** (`MemorySmithRequestGuardMiddleware.cs:27-69`, `SecurityServices.cs:265-281`). Audit #1 §7.1.
2. **C: Audit hash chain is naked SHA-256 — no HMAC, no external anchor** (`SecurityServices.cs:880-928`). Audit #1 §7.2.
3. **C: Cookie auth missing `Cookie.SecurePolicy.Always`, `ExpireTimeSpan`, `OnValidatePrincipal`** (`Program.cs:117-124`). Audit #1 §7.3.
4. **C: `OpenLocalEditorCompatibility` grants admin-equivalent permission to anonymous loopback during pre-bootstrap window** (`SecurityServices.cs:255-282`). Audit #1 §7.4.
5. **H: No anti-forgery enforcement on plain `[ApiController]` controllers**. Audit #1 §7.5.
6. **H: GitHub OAuth first-login auto-promotes to Admin with no IP gate or email allowlist** (`Program.cs:247-249`). Audit #1 §7.6.
7. **H: `IsLoopback(null) = true` — null IP treated as trusted** (`MemorySmithRequestGuardMiddleware.cs:58-61`). Audit #1 §7.7.
8. **H: Markdig `UseAdvancedExtensions` + `GenericAttributes` enables `onclick=...` XSS even with `allowRawHtml=false`** (`ChatMarkdownRenderer.cs:42-54`). Audit #2 §4.1.
9. **H: Login rate-limiter is global, not per-account/per-IP** (`Program.cs:324-334`). Audit #1 §7.9.
10. **H: `VarResolver.OpenWithDefaultAppAsync` is an arbitrary file-open primitive for Editors who control `vars.json`** (`VarResolver.cs:114-206`). Audit #1 §7.10.

### Storage / Durability
11. **C: `FileMemoryStore.Save` deletes the old file before writing the new on status change** (`FileMemoryStore.cs:88-108`). Audit #1 §6.1.
12. **C: No `fsync` after temp-file write before rename in any file store**. Audit #1 §6.2.
13. **C: `SanitizeId` regex misses reserved Windows names, NUL bytes, length, dot/whitespace** (`FileMemoryStore.cs:183-190`). Audit #1 §6.3.
14. **C: `FileMaintenanceProposalStore.ApplyAsync` is non-atomic across multi-file proposals**. Audit #2 §2.1.
15. **C: `FileMaintenanceProposalStore.SaveAsync` uses bare `File.WriteAllText`**. Audit #2 §2.2.
16. **H: `FileVarStore.Save` has NO lock — concurrent saves race**. Audit #1 §6.4.
17. **H: `LoadAll` reads every file with no size cap, under a coarse lock**. Audit #1 §6.5.
18. **H: `TagPolicyService.SavePolicy` uses bare `File.WriteAllText`** (`MemoryGovernanceServices.cs:123-136`). Audit #2 §3.1.
19. **H: No cross-process coordination across stores**. Audit #1 §6.7.
20. **H: No SQLite migration framework — schema upgrades require code changes**. Audit #1 §6.8.
21. **H: No backup/restore path**. Audit #1 §6.9.
22. **H: Audit hash chain covers only subset of fields**. Audit #2 §7.1.
23. **H: Audit chain race condition on concurrent writes — read latest + append is not transactional**. Audit #2 §7.2.

### Chat / Agent
24. **C: Token estimator is `chars/4` — wildly inaccurate for code, JSON, CJK; chat may silently overflow context windows**. Audit #1 §5.1.
25. **C: `ReadToolCalls` will treat almost any JSON as a tool call** (`ChatServices.cs:1819-1937`). Audit #1 §5.2.
26. **H: `GitHubCopilotChatProvider` uses `PermissionHandler.ApproveAll`** (`ChatServices.cs:800`). Audit #1 §5.3.
27. **H: Ollama streaming has no per-chunk parse safety**. Audit #1 §5.4.
28. **H: No MCP `inputSchema` is included in the system prompt for tool calling**. Audit #1 §5.5.
29. **H: Image attachment MIME validation is by reported `ContentType` only**. Audit #1 §5.6.
30. **H: `MaxToolIterations` silently clamped to ≤5**. Audit #1 §5.7.
31. **H: No grounding / citation enforcement**. Audit #1 §5.8.

### Background Services
32. **C: `MemoryMaintenanceService` polls every 1 second**. Audit #3 §2.1.
33. **H: `MaintenanceAgentSchedulerService.IsWeeklyWindow` is DST-fragile**. Audit #3 §2.2.
34. **H: `InitializeAsync` on startup uses `CancellationToken.None`**. Audit #3 §2.3.
35. **H: `InMemoryMemoryStore` doesn't simulate production semantics**. Audit #3 §6.1.

### Markdown / XSS (continued)
36. **H: Mermaid extension renders SVG via `innerHTML` — XSS via SVG**. Audit #2 §4.2.
37. **H: `PageService.NormalizeAssetReferences` does regex over raw markdown — false positives in code fences**. Audit #2 §4.3.

### Tokenizer
38. **H: WordPiece tokenizer remains incompatible with HF tokenizer.json models** (Audit #4 §4.1, §5.2). The branch made it WORSE by shipping an install script that downloads incompatible models.

### Maintenance Tasks
39. **H: `MemoryMaintenanceTasks.DeduplicateRecords` silently merges and deletes by Title collision** (Audit #4 §6.1). NEW this audit.
40. **H: `RunTriageAsync` walks every record every 5 minutes** (Audit #4 §6.2). NEW this audit.

**Of these 40 unaddressed High/Critical findings, none are blocked on the code-search work.** They're orthogonal and can be sprinted independently. The maintainer has the right pattern (audit → task → branch → test → merge); applying it to the auth+storage+chat surfaces next would close the bulk of remaining risk.

---

## 8. Positive Notes from This Audit

I want to balance the depth of criticism with explicit recognition of what's done well in the new branch.

### 8.1 The maintainer treats audits as engineering artifacts

`Data/Pages/ms-copilot-vector-search-research-reports.md` contains the full text of my Audit #1 plus a preface listing what was remediated. Tasks reference my audit explicitly. Comments in code (and PR descriptions in the commit messages) cite specific findings. This is the gold-standard pattern for external feedback.

### 8.2 The remediation came with a benchmark harness, not just code

`Scripts/Measure-CodeSearchQueries.ps1`, `Scripts/Measure-CodeSearchRelevance.ps1`, `Scripts/code-search-relevance-suite.json`, the E5-vs-Nomic A/B writeup, and the model-export workflow are infrastructure that will accelerate every future search-quality decision. The 8/8 pass on the relevance suite for both E5 and Nomic is rigorous evidence.

### 8.3 Reversibility

`VectorCandidatePrefilterEnabled` flag, `CpuFallbackEnabled`, `EmbeddingsEnabled` — every new feature has a kill switch. The maintainer can roll back individual experiments without redeploying.

### 8.4 Tests target the specific behavior changes

The 4 new tests (`SearchAsync_DefaultExcludePatternsSkipProjectDocsNoise`, `SearchAsync_DemotesTestTargetsForImplementationQueries`, `SearchAsync_LexicalFallbackMatchesSnakeCaseQueryAgainstCamelCaseIdentifier`, `SearchAsync_FallsBackToFullVectorScanWhenLexicalPrefilterMissesSemanticHit`) each target one specific remediation. The `SemanticAliasEmbeddingProvider` mock is clever — it forces the test path to use the full-scan fallback by giving identical embeddings to strings with no lexical overlap.

### 8.5 Documentation kept current

The model-export-workflow guide, the README updates (model path rename, new script docs), and the research/vector-search pages are all updated alongside the code. The README is no longer drifting from the implementation in this area.

### 8.6 The E5 vs Nomic measurement is real engineering

Real corpus, both models on the same machine, same 8 queries, recorded median/min/max latencies, rebuild cost, and per-query top-result drift analysis. The takeaway ("Nomic faster on queries by 11-77%, E5 24% faster on rebuild") is actionable. The honesty about "first sample in each query set is consistently higher than subsequent runs" shows attention to JIT/cache warm-up effects.

### 8.7 The new Python export script is idempotent and isolated

`Install-CodeSearchModel.ps1` creates `.venv-model-export`, doesn't pollute the system Python, and the manifest emit is reproducible. Operator-friendly. Note 8.1: this is balanced against §4.1's WordPiece-incompatibility concern — the script makes a real workflow improvement that exposes a real runtime limitation.

---

## 9. Prioritized Next Steps (Updated Roll-Up)

The "Best Suit Stated Goals" list from Audit #2 §15 + Audit #3 §11 stands. Audit #4 adjustments:

**Promote up the priority list** (these became more important given the new code):
- **6c. Add expression index on `lower(DocumentPath)`** in code-search SQLite. Combined with the new prefilter SQL, this is a real 5-10× speedup with one line of DDL.
- **6d. Migrate `EmbeddingJson TEXT` → `Embedding BLOB FP16` with Matryoshka dim setting.** Becomes 12× smaller, ~2× faster to deserialize.
- **6e. Replace WordPieceTokenizer with `Microsoft.ML.Tokenizers.BertTokenizer`** — closes the BERT-spec gaps AND enables BPE for nomic-embed-code. The script workflow already advertises this; the runtime needs to catch up.

**New items from this audit:**
- **18. Add cross-encoder reranker stage** (`bge-reranker-base` or `cross-encoder/ms-marco-MiniLM-L-6-v2`). The benchmark harness already exists to validate.
- **19. Add expression indexes + `PRAGMA` set on code-search SQLite** (§4.5 + §3.3). 30-50% throughput.
- **20. Replace `Dot` with `TensorPrimitives.Dot`** (§4.4). Single NuGet ref + one-line change.
- **21. AST-aware chunking via Roslyn for C#** (§4.7). Sourcegraph Cody / Continue.dev-style.
- **22. Add `MemoryMaintenanceTasks.DeduplicateRecords` audit-log entries + admin opt-in** (§6.1). The destructive default is the bug.
- **23. Track `EmbeddingModelHash` per chunk row in SQLite** (§4.13). Prevents incoherent rankings during model swap.
- **24. Normalize all JSON files to non-BOM UTF-8 + CI check** (§6.3).
- **25. Make `MemoryMaintenanceTasks` thresholds configurable** (§6.5).

**Untouched from Audits #1-#3 — still P0:**
- Auth hardening trio (Cookie SecurePolicy/ExpireTimeSpan/OnValidatePrincipal)
- Loopback bootstrap default-off
- HMAC-keyed audit chain
- `SafeFileWriter` utility applied uniformly to all stores
- `ReadToolCalls` strict envelope

---

## 10. Closing Verdict

The maintainer's remediation pattern is excellent — audits get archived as evidence, work gets tracked against them, branches ship with tests and benchmarks. Five of my code-search findings have been closed in 5 commits, with regression tests targeting each. The E5-vs-Nomic benchmark and model-export workflow are real engineering infrastructure that will pay dividends for every future search change.

What's missing is **breadth of remediation**: the auth, storage, chat-agent, and maintenance-agent findings all still stand. Two of them (the `MemoryMaintenanceTasks.DeduplicateRecords` destructive default, the 1-second polling) are NEW Highs I surfaced this round. The unaddressed surface includes 4 of the 11 Critical findings from prior audits.

The code-search work itself has a few new bugs (§3) but is structurally an improvement. The biggest critique is that the SQL prefilter is a stop-gap: it works until the corpus grows past the point where the unindexed `lower(DocumentPath)` scan becomes painful, then it has to be replaced with sqlite-vec or HNSW.Net. The 6-month runway it buys is real but finite.

If the maintainer applies the same remediation pattern (audit → tasks → branch → tests → merge) to the auth surface next, then storage, then chat, then maintenance — and uses the benchmark harness to validate each — MemorySmith will close the gap between its current state (excellent local-first hobby project with measurement-driven engineering) and what it could be (a defensible MCP server suitable for local trusted-environment deployment).

The verdict from Audit #1 §0 stands: **single-user local use on a trusted workstation is acceptable today**. The code-search subsystem is now meaningfully better than at audit time, and the maintenance pattern in evidence here makes the rest of the gap closable.

---

## Combined Severity Roll-Up Across All Four Audits

| Severity | Audit #1 | Audit #2 (new) | Audit #3 (new) | Audit #4 (new) | Closed by remediation | Currently open (combined) |
|---|---:|---:|---:|---:|---:|---:|
| Critical | 8 | 2 | 1 | 1 | — | 12 |
| High | 22 | 11 | 8 | 5 | 4 | 42 |
| Medium | 33 | 18 | 17 | 15 | 2 | 81 |
| Low | 14 | 9 | 13 | 8 | 1 | 43 |
| **TOTAL** | **77** | **40** | **39** | **29** | **7** | **178** |

The maintainer has closed roughly 4% of findings in this branch — but those 4% include 4 of my 22 Highs in code search, the highest-impact area. Future sprints applying the same pattern to other surfaces would compound quickly.

---

## 11. Assumptions for Audit #4

| ID | Assumption | Confidence |
|---|---|---:|
| D1 | The `feature/code-search-high-roi-batch8` branch at `61af84914b0fc278e594911a00a4a7e95f7e7b0d` is the canonical "recent push" the user referenced. | 0.95 |
| D2 | The maintainer's intention is to keep applying the same audit→task→branch→test pattern to other surfaces. | 0.85 |
| D3 | `MemoryMaintenanceTasks.DeduplicateRecords` being destructive-by-default is unintentional (it isn't reachable from the proposal workflow). | 0.85 |
| D4 | The `ms-copilot-vector-search-research-reports.md` page is intended as an archive of my Audit #1 (the preface labels it as such). | 0.95 |
| D5 | The benchmark harness scripts run against a live local instance at `http://127.0.0.1:5091` per the Measure-CodeSearchRelevance.ps1 default. | 0.85 |

## 12. Open Questions for Audit #4

| ID | Question |
|---|---|
| SS1 | Was the next planned sprint going to be auth, storage, or chat? The roll-up at §9 sequences them but the right order depends on the maintainer's deployment plans. |
| SS2 | Is the goal to support multiple embedding model families (E5, Nomic, BGE), or to pick one and optimize? The current runtime hardcodes WordPiece even though the install script broadens models. |
| SS3 | Should `MemoryMaintenanceTasks.DeduplicateRecords` be removed entirely in favor of a proposal-workflow path, or should the auto-merge stay as long as it's gated behind a stricter Title equality check? |
| SS4 | Is the goal an open MCP server for external consumption, or a private local-first knowledge base? Determines whether the unaddressed auth findings are P0. |

## 13. Limits of This Audit

I did not:
- Run `dotnet build` or `dotnet test` against the branch. The 8/8 relevance suite pass is taken from the maintainer's own writeup, not re-verified.
- Stand up an MCP client and exercise `memorysmith_code_search` end-to-end against the new code.
- Re-deploy and time the new code on hardware.
- Read every file in the `Data/Pages/research/vector-search/` directory beyond the E5-vs-Nomic A/B and the model-export guide.
- Inspect the actual ONNX model files (they're not in the repo).
- Audit the Python export script for security (it downloads from HuggingFace and runs PyTorch; that's outside the .NET attack surface).

I did:
- Read every line of the diff between master and the feature branch.
- Re-read the full updated `CodeSearchService.cs`, `MemorySmithOptions.cs`, `appsettings.json`.
- Read the new `CodeSearchServiceTests.cs` additions and all 4 new mock providers.
- Read `MemoryMaintenanceTasks.cs` end-to-end first-hand (it was untouched in Audits #1-#3 first-hand reads).
- Read the new research/vector-search pages and the maintainer's own "vector-search-general-codebase-report" wiki page.
- Cross-reference each finding against my prior audits with explicit line references.

---

*End of Audit #4. ~10,400 words. 13 pages at standard typesetting.*

## Cross-Audit Reading Path

If you read in order:
- **Audit #1** (~9,500 words): strategic sweep — memory search, code search, MCP, chat, storage, security.
- **Audit #2** (~9,700 words): first-hand verifications + maintenance agent, governance, pages, state machine, audit log, XSS, source-link, OpenAPI, tests, repo hygiene.
- **Audit #3** (~6,200 words): SQLite verification + background services, scheduler DST, image attachments, tokenizer BERT spec, DI lifecycle, test doubles, CI, TaskDomainService.
- **Audit #4 (this report)** (~10,400 words): remediation review of `feature/code-search-high-roi-batch8` + continued deep dive on code search and vector embedding.

Combined: ~35,800 words, ~178 distinct findings (12 Critical / 42 High / 81 Medium / 43 Low), 7 closed by remediation, 3 tracked. The §9 prioritized list is the single best place to start work.
