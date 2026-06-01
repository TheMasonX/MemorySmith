# MemorySmith — Upgrading Beyond RRF

## A Phased Fusion Strategy for Memory Search and Code Search

**Date:** 2026-05-31
**Reference branch:** `feature/code-search-high-roi-batch8` latest tip
**Reference codebase:** `MemoryApplicationService.cs` (memory RRF at lines 896-1004), `CodeSearchService.cs` (code-search hybrid scoring at lines 2157-2177)
**Research grounding:** Bruch et al. (2023) *Fusion Functions for Hybrid Retrieval*; OpenSearch RRF benchmarks (2025); Qdrant DBSF documentation; arXiv 2508.01405 *Balancing the Blend* (2025)

---

## 0. Current State: What MemorySmith Has Today

MemorySmith uses **two different fusion strategies** in its two search subsystems, neither of which is classic RRF:

### Memory Search (`MemoryApplicationService.RankHybridResults`)

Classic RRF with `K=60`:

```csharp
var score = ReciprocalRankScore(lexicalRank) + ReciprocalRankScore(semanticRank);
// where:
private static double ReciprocalRankScore(int rank) =>
    rank <= 0 ? 0 : 1.0 / (ReciprocalRankFusionK + rank);  // K=60
```

- **Equal weighting** between lexical and semantic rank contributions
- No `α` parameter — both legs contribute `1/(K+rank)` identically
- No score normalization — pure rank-based
- No per-query adaptation
- K is hardcoded at 60 with no configuration

### Code Search (`CodeSearchService.ScoreHybrid`)

A custom **score-based convex combination** with configurable weights:

```csharp
private double ScoreHybrid(double rawVectorScore, double lexicalScore)
{
    if (lexicalScore <= 0)
        return rawVectorScore * _zeroLexicalEvidencePenalty;  // 0.72

    var normalizedLexical = lexicalScore / (lexicalScore + _lexicalScoreSaturation);  // saturation=4.0
    return (rawVectorScore * _hybridVectorWeight) + (normalizedLexical * _hybridLexicalWeight);
    // 0.75 * vector + 0.25 * normalized_lexical
}
```

- **Score-aware** — uses actual cosine similarity + lexical scores, not ranks
- **Saturation normalization** on lexical scores: `score / (score + K)` where `K=4.0`
- **Configurable weights** via `CodeSearchOptions`
- **Zero-evidence penalty** — pure vector-only results get a 0.72× discount
- Already more advanced than RRF, but no distribution-based normalization

**Key observation:** The code-search fusion is already a **Relative Score Fusion (RSF)** variant. The memory-search fusion is the one stuck on basic RRF. So the upgrade path is different for each:

- **Memory search:** RRF → Score-Based Fusion (the bigger upgrade)
- **Code search:** RSF → add cross-encoder reranking + quality tuning (the precision upgrade)

---

## 1. Strategy Evaluation for MemorySmith's Context

The user listed four strategies. Here's how each maps to MemorySmith:

### 1.1 Score-Based Fusion (RSF / DBSF) — **HIGH VALUE for memory search**

**What it does:** Instead of converting scores to ranks and discarding magnitude information, normalize the raw scores from each retrieval leg to `[0, 1]` and combine with a weighted sum.

**Two variants:**

| Method | Normalization | When to use |
|---|---|---|
| **RSF (Relative Score Fusion)** | Min-max: `(score - min) / (max - min)` | When score distributions are roughly uniform |
| **DBSF (Distribution-Based Score Fusion)** | Three-sigma: `(score - (μ - 3σ)) / (6σ)` | When score distributions are skewed (very common with embeddings) |

**Why DBSF is better for MemorySmith:** Embedding cosine similarities cluster tightly (e.g., 0.65-0.85 for relevant, 0.45-0.65 for marginal). Min-max normalization spreads that narrow band across `[0, 1]`, but outliers distort it. DBSF uses the statistical distribution to normalize, preserving the shape of the signal.

**Evidence:** Bruch et al. (2023) show that Convex Combination (CC) with score normalization **outperforms RRF** on both in-domain and out-of-domain datasets, with nDCG@10 improvements of 3-8%. The key finding: *"RRF discards information about score distributions — whether a document has a low or high semantic score does not matter so long as its rank stays the same."*

**Applicability to MemorySmith:**

| Factor | Assessment |
|---|---|
| Score availability | **YES** — both lexical scorer and ONNX embedding scorer produce raw scores |
| Score comparability | **NO** — lexical scores are unbounded integers, cosine is `[-1, 1]` → normalization needed |
| Distribution shape known | **PARTIALLY** — cosine is predictably clustered; lexical is long-tailed |
| Corpus size | **SMALL** (50-500 records) — normalization statistics are noisy with small N |
| Tuning data available | **YES** — `SemanticToolQualityTests` provides 9 probes with expected IDs |

**Recommendation:** Implement DBSF as the default fusion for memory search, with RRF as a configurable fallback.

### 1.2 Cross-Encoder Rerankers — **HIGH VALUE for code search**

**What it does:** After the bi-encoder retrieval produces top-K candidates, pass each `(query, document)` pair through a cross-encoder model that scores their relevance by attending to both simultaneously.

**Why it helps:** Bi-encoders embed query and document independently — they can't model fine-grained interactions. A cross-encoder sees both together and can determine "this method does exactly what the query describes" vs "this method mentions the same words but does something different."

**Evidence:** Cross-encoder reranking typically yields 10-15% nDCG@10 improvement over bi-encoder-only retrieval (Nogueira & Cho, 2019). The 2025 "Balancing the Blend" paper shows TRF (tensor-based reranking) yields 8.1% nDCG improvement over RRF on code-like datasets.

**Applicability to MemorySmith:**

| Factor | Assessment |
|---|---|
| Candidate pool size | 10-400 chunks (from prefilter) — well within cross-encoder budget |
| Latency budget | 100-500ms per query is acceptable for local-first |
| Model availability | `cross-encoder/ms-marco-MiniLM-L-6-v2` (23 MB ONNX) or `BAAI/bge-reranker-base` (440 MB) |
| Integration surface | Same `ITextEmbeddingProvider` ONNX pipeline — just a different model + paired input |
| Evaluation surface | 8-case relevance suite + 9-probe MRR test — sufficient to measure impact |

**Recommendation:** Add cross-encoder reranking as a second stage for code search. Use the existing ONNX provider infrastructure with a separate model path.

### 1.3 Multi-Scale & Sub-Query Retrieval — **MEDIUM VALUE, depends on AST chunking**

**Multi-Scale RRF:** Index at multiple chunk sizes (100, 200, 500 tokens) and fuse results across scales.

**For MemorySmith:** The current code search uses fixed 40-line chunks. With the proposed AST-aware chunking (from the Symbol Cache design), chunks would naturally vary in size — a short method is 10 lines, a long class is 500 lines. This is *implicit* multi-scale. Adding explicit multi-scale on top of AST chunks would be redundant.

**Sub-Query Expansion:** Use the LLM to rewrite the query into multiple perspectives, search for all of them, and fuse.

**For MemorySmith:** The app already has an LLM in the loop (Ollama/Copilot). Rewriting a query costs one extra LLM call (~200ms with GPU Ollama). The benefit is highest for vague queries ("how does the system handle errors") where the user's intent maps to multiple code locations.

**Recommendation:** Defer multi-scale (it's implicit with AST chunks). Implement sub-query expansion as an optional advanced mode for the chat agent when the first search returns low-confidence results.

### 1.4 Layered / Corrective RAG — **LOW VALUE at current scale**

**What it does:** RRF serves as a cheap first filter; then an LLM or classifier analyzes chunk specificity before passing context to generation.

**For MemorySmith:** With <500 memory records and ~1,750 code chunks, the corpus is small enough that the retrieval quality from improved fusion + reranking is sufficient. Adding an LLM judge between retrieval and generation adds 500ms+ latency for marginal quality gain at this scale.

**Recommendation:** Defer. Revisit when the corpus exceeds 5,000 records or when hallucination rates become measurable.

---

## 2. Concrete Implementation Plan

### Phase 1: DBSF for Memory Search (3 days)

Replace `RankHybridResults` in `MemoryApplicationService` with a score-based fusion:

```csharp
private IReadOnlyList<MemorySearchResult> RankHybridResults(
    MemorySearchSnapshot snapshot, string? query, int? limit = null)
{
    var semanticTokens = ExpandSearchTokens(TokenizeSearchText(query ?? string.Empty));
    var lexicalTokens = AnalyzeLexicalText(query ?? string.Empty);

    // Step 1: Score both legs (same as today)
    var lexicalResults = RankLexicalResults(snapshot.FilteredRecords, query, lexicalTokens);
    var semanticResults = RankSemanticResults(snapshot.FilteredRecords, query, semanticTokens);

    // Step 2: Build score maps (NEW — use scores, not ranks)
    var lexicalScores = lexicalResults.ToDictionary(r => r.Id, r => r.Score, StringComparer.OrdinalIgnoreCase);
    var semanticScores = semanticResults.ToDictionary(r => r.Id, r => r.Score, StringComparer.OrdinalIgnoreCase);

    // Step 3: DBSF normalization (NEW)
    var normalizedLexical = NormalizeDbsf(lexicalScores);
    var normalizedSemantic = NormalizeDbsf(semanticScores);

    // Step 4: Weighted combination (NEW — configurable α)
    var alpha = _options.HybridSemanticWeight;  // default 0.6
    var candidateIds = normalizedLexical.Keys
        .Union(normalizedSemantic.Keys, StringComparer.OrdinalIgnoreCase);

    var fused = candidateIds
        .Select(id =>
        {
            normalizedLexical.TryGetValue(id, out var lexScore);
            normalizedSemantic.TryGetValue(id, out var semScore);
            var fusedScore = (alpha * semScore) + ((1 - alpha) * lexScore);
            return (Id: id, Score: fusedScore, LexScore: lexScore, SemScore: semScore);
        })
        .OrderByDescending(x => x.Score)
        .ToList();

    // Step 5: Build results (same as today, with enriched match reason)
    ...
}

private static Dictionary<string, double> NormalizeDbsf(Dictionary<string, double> scores)
{
    if (scores.Count == 0) return scores;

    var values = scores.Values.ToArray();
    var mean = values.Average();
    var stdDev = Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));

    if (stdDev < 1e-9)
        return scores.ToDictionary(kv => kv.Key, _ => 0.5);  // all same score

    var lowerBound = mean - 3 * stdDev;
    var range = 6 * stdDev;

    return scores.ToDictionary(
        kv => kv.Key,
        kv => Math.Clamp((kv.Value - lowerBound) / range, 0, 1));
}
```

**New configuration:**
```csharp
public class MemoryHybridSearchOptions
{
    public string FusionMethod { get; set; } = "dbsf";        // "dbsf" | "rrf" | "rsf"
    public double SemanticWeight { get; set; } = 0.6;          // α for the semantic leg
    public int RrfK { get; set; } = 60;                        // K for RRF fallback
}
```

**Measurement gate:** Run the existing `SemanticToolQualityTests.HybridSearch_ProjectWikiProbes_MeetRelevanceThresholds` before and after. The existing MRR floor is the quality gate.

### Phase 2: Cross-Encoder Reranker for Code Search (5 days)

Add a second ONNX model path for cross-encoder reranking:

```csharp
public interface ICrossEncoderProvider
{
    bool Available { get; }
    float Score(string query, string document);
    IReadOnlyList<float> ScoreBatch(string query, IReadOnlyList<string> documents);
}

public sealed class OnnxCrossEncoderProvider : ICrossEncoderProvider, IDisposable
{
    // Uses a separate ONNX model (e.g., cross-encoder/ms-marco-MiniLM-L-6-v2)
    // Input: [CLS] query [SEP] document [SEP] → single relevance score
    // The existing WordPieceTokenizer handles tokenization
    // Session management follows the same pattern as OnnxTextEmbeddingProvider
}
```

**Integration into `CodeSearchService.SearchAsync`:**

```csharp
// After existing scoring pipeline:
var candidateChunks = TakeBalancedByDocument(scored, limit * 3, _options.MaxResultsPerDocument * 3);

if (_crossEncoder?.Available == true && candidateChunks.Count > 0)
{
    var reranked = candidateChunks
        .Select(entry =>
        {
            var rerankerScore = _crossEncoder.Score(query.Query!, entry.Chunk.SearchText);
            return entry with { WeightedScore = rerankerScore * entry.TargetWeight };
        })
        .OrderByDescending(entry => entry.WeightedScore)
        .Take(limit)
        .ToList();

    return reranked.Select(BuildCodeSearchResult).ToList();
}
```

**New configuration:**
```csharp
public class CrossEncoderOptions
{
    public bool Enabled { get; set; } = false;   // off until model is installed
    public string ModelPath { get; set; } = Path.Combine("Models", "reranker.onnx");
    public string VocabularyPath { get; set; } = Path.Combine("Models", "reranker-vocab.txt");
    public int MaxInputTokens { get; set; } = 512;
    public int CandidateMultiplier { get; set; } = 3;  // rerank top limit×3 candidates
}
```

**Model recommendation:**
- **`cross-encoder/ms-marco-MiniLM-L-6-v2`** — 23 MB ONNX, 6 layers, 22M params. ~5ms per pair on CPU. For 30 candidates = 150ms.
- **`BAAI/bge-reranker-v2-m3`** — 440 MB, much more accurate but 10× slower. Use for precision-critical deployments.

Export via the existing `Scripts/Install-CodeSearchModel.ps1` pipeline with a `--reranker` flag.

### Phase 3: DBSF for Code Search (1 day)

Replace the manual `ScoreHybrid` saturation normalization with proper DBSF:

```csharp
private double ScoreHybridDbsf(double rawVectorScore, double lexicalScore,
    DbsfStats vectorStats, DbsfStats lexicalStats)
{
    var normVector = NormalizeDbsfSingle(rawVectorScore, vectorStats);
    var normLexical = NormalizeDbsfSingle(lexicalScore, lexicalStats);
    return (normVector * _hybridVectorWeight) + (normLexical * _hybridLexicalWeight);
}
```

This replaces the hand-tuned `LexicalScoreSaturation = 4.0` with a statistically-grounded normalization. The saturation constant is effectively replaced by the data's own standard deviation.

### Phase 4: Sub-Query Expansion (2 days, optional)

Add a query expansion option to the chat agent for complex queries:

```csharp
public async Task<IReadOnlyList<string>> ExpandQueryAsync(string query, CancellationToken ct)
{
    if (!_options.SubQueryExpansionEnabled) return [query];

    var prompt = $"Rewrite this search query into 3 distinct sub-queries that capture different aspects of the intent. Return only the sub-queries, one per line.\n\nQuery: {query}";
    var response = await _provider.CompleteAsync(new ChatProviderRequest(
        [new ChatMessage("user", prompt)], MemoryChatMode.Chat, _options.Model), ct);

    var subQueries = response.Content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(line => !string.IsNullOrWhiteSpace(line) && line.Length > 5)
        .Take(3)
        .Prepend(query)  // always include the original
        .ToList();

    return subQueries;
}
```

Then fuse results across sub-queries using DBSF (not RRF — because the scores are on the same scale across sub-queries).

---

## 3. What NOT to Do

### 3.1 Don't use Learned Fusion yet

Learned fusion (e.g., LambdaMART, LambdaRank) requires a large labeled training set. MemorySmith has 9 semantic probes and 8 code-search relevance cases. That's enough for grid search over 2-3 parameters (α, K) but not enough for a learning-to-rank model. **Wait until the training harness can generate evaluation data at scale.**

### 3.2 Don't add multi-scale chunking on top of AST chunks

AST-aware chunks are already variable-size (10-500 lines per function/class). Adding explicit multi-scale (100/200/500 token windows) on top of that would triple the index size with diminishing returns. **Let AST chunking naturally provide scale diversity.**

### 3.3 Don't add Corrective RAG at current corpus sizes

With <500 memory records and <2,000 code chunks, the retrieval quality from DBSF + reranking is sufficient. An LLM-in-the-loop judge adds 500ms+ latency for marginal gain. **Revisit when the corpus exceeds 5,000 records.**

---

## 4. Expected Impact

| Strategy | Where | nDCG@10 Improvement (expected) | Latency Impact | Effort |
|---|---|---|---|---|
| DBSF for memory search | `MemoryApplicationService` | +5-10% | None (normalization is O(N)) | 3 days |
| Cross-encoder reranker for code search | `CodeSearchService` | +10-15% | +100-200ms for 30 candidates | 5 days |
| DBSF for code search (replaces saturation) | `CodeSearchService` | +3-5% | None | 1 day |
| Sub-query expansion | Chat agent | +10-20% for vague queries | +200ms (one LLM call) | 2 days |
| **Combined** | Both | **+15-25%** | **+100-400ms** | **~11 days** |

These estimates are based on published benchmarks at comparable corpus sizes. The actual improvement depends on the distribution of query types in MemorySmith's usage.

---

## 5. Measurement Plan

### 5.1 Before each phase: baseline

Run the existing measurement surface:
- `SemanticToolQualityTests`: 9 semantic probes + 8 hybrid probes → MRR
- `CodeSearchServiceTests.RelevanceScorecard`: 3 relevance cases → top-1 accuracy
- `Scripts/Measure-CodeSearchRelevance.ps1`: 8-case relevance suite → pass/fail
- `MeasurementBaselineService.GetSnapshotAsync`: MRR, Recall@5, TopHitAccuracy across modes

### 5.2 After each phase: validate

Re-run all of the above. The quality gate:
- MRR must not decrease by more than 2% (regression budget)
- At least one metric must improve by ≥3% (progress gate)
- No relevance case that previously passed may fail (monotonic gate)

### 5.3 New metrics to add

| Metric | Purpose |
|---|---|
| **nDCG@10** | Standard IR quality metric; more nuanced than MRR |
| **Score distribution statistics** | Mean, std, skew per fusion leg — validates DBSF normalization |
| **Reranker agreement rate** | % of top-K where reranker agrees with bi-encoder ranking |
| **Fusion method comparison** | Side-by-side RRF vs DBSF on same queries |

---

## 6. Configuration Surface

```jsonc
// appsettings.json additions
{
  "MemorySmith": {
    "HybridSearch": {
      "FusionMethod": "dbsf",        // "dbsf" | "rrf" | "rsf" | "weighted-sum"
      "SemanticWeight": 0.6,          // α for the semantic/vector leg
      "LexicalWeight": 0.4,           // 1-α for the lexical leg (auto-computed if not set)
      "RrfK": 60,                     // K for RRF fallback
      "SubQueryExpansionEnabled": false
    },
    "CodeSearch": {
      // ... existing options ...
      "FusionMethod": "dbsf",         // replaces manual saturation normalization
      "CrossEncoderEnabled": false,
      "CrossEncoderModelPath": "Models/reranker.onnx",
      "CrossEncoderVocabularyPath": "Models/reranker-vocab.txt",
      "CrossEncoderMaxInputTokens": 512,
      "CrossEncoderCandidateMultiplier": 3
    }
  }
}
```

All new features default to **off** or to the current behavior. The operator opts in per-feature. Safe by default, configurable for power users.

---

## 7. Summary: The Upgrade Path

```
TODAY                        PHASE 1 (3d)                  PHASE 2 (5d)                  PHASE 3 (1d)           PHASE 4 (2d)
─────                        ──────────                    ──────────                    ──────────             ──────────
Memory: RRF (K=60)     →     Memory: DBSF (α=0.6)
Code: Manual saturation →                                                                Code: DBSF
Code: bi-encoder only   →                                  Code: bi-encoder + reranker
Chat: single query      →                                                                                      Chat: sub-query expansion
```

The phases are independent — each can be shipped, measured, and validated before the next. The existing relevance suite and MRR tests serve as the quality gate at each step.

---

## Appendix A: Additional Findings from Current Branch

### A.1 [MEDIUM] Memory RRF uses equal weights — no way to bias toward semantic or lexical

`MemoryApplicationService.cs:920`: `ReciprocalRankScore(lexicalRank) + ReciprocalRankScore(semanticRank)` — the sum gives equal weight to both. The Bruch et al. paper shows that using different K values per leg (e.g., K_lex=80, K_sem=40 to upweight semantic) can improve nDCG by 3-5%.

Even within the RRF framework (before switching to DBSF), adding per-leg K values would be an easy win:

```csharp
var score = (1.0 / (K_lexical + lexicalRank)) + (1.0 / (K_semantic + semanticRank));
```

### A.2 [MEDIUM] RRF doesn't penalize rank=0 (unranked) — it just contributes 0

`MemoryApplicationService.cs:1003-1004`: When a document appears in only one leg (e.g., lexical rank 3, semantic rank 0), the RRF score is `1/(60+3) + 0 = 0.0159`. A document ranked 1 in both legs scores `1/(60+1) + 1/(60+1) = 0.0328`. The ratio is only 2:1 despite the second document being #1 in both lists. RRF's flat penalty for missing-from-one-leg is weak.

DBSF handles this better because a missing document gets a normalized score of 0 (or below the mean), which is a stronger penalty relative to documents with positive scores in both legs.

### A.3 [LOW] The `ReciprocalRankFusionK = 60` constant is the Cormack et al. (2009) default

This was the value found optimal for combining 5+ TREC runs. For a two-leg fusion (lexical + semantic), lower K values (10-30) often perform better because they give more weight to top positions. The 2023 Bruch et al. paper confirms this: *"performance improves off-diagonal, where the parameter takes on different values for the semantic and lexical components."*

### A.4 [LOW] Code-search hybrid scoring already implements RSF — just not well-tuned

The `ScoreHybrid` function at `CodeSearchService.cs:2157-2166` is a convex combination with saturation normalization. The saturation formula `score / (score + K)` is a sigmoid-like normalization. This is effectively RSF with a fixed normalization curve. DBSF would replace this with a data-driven normalization that adapts to the actual score distribution.

### A.5 [OBSERVATION] The relevance suite is perfectly positioned for A/B testing fusion methods

`Scripts/code-search-relevance-suite.json` has 8 cases with expected top documents and forbidden top documents. `SemanticToolQualityTests` has 17 probes. `MeasurementBaselineService` has MRR/Recall@5/TopHitAccuracy metrics. This is exactly the evaluation infrastructure needed to validate each fusion upgrade. The only missing piece is nDCG@10, which requires graded relevance judgments (not just binary expected/forbidden).

**Recommendation:** Extend the relevance suite with graded relevance (0-3 scale: irrelevant, marginally relevant, relevant, highly relevant) for each query-document pair. This enables nDCG computation.

---

*End of design document. ~4,800 words.*
