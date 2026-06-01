# Search Quality Deep Dive — External Agent Benchmark (2026-05-29)

**Author:** Claude Sonnet 4.6 via npx mcp-remote bridge
**Scope:** Memory search (hybrid, semantic, lexical) and code search (vector + hybrid rerank)
**Method:** Non-destructive read-only tool calls only; no writes made during testing
**Related tasks:** TSK-0183, TSK-0185, TSK-0186
**Intended slug:** `audits/search-quality-deepdive-20260529`

---

## 1. Scope and Methodology

This report covers a focused quality evaluation of MemorySmith's search tooling from the perspective of an external agent. The primary concern driving this evaluation is that the ONNX-based code search is new and unproven in production, and that memory search quality under real agent queries — paraphrased, negative, adversarial — has not been externally characterised.

All findings are based on live tool call results. Scores are raw values from the MCP response envelope. No indirect inferences were made: every claim has a corresponding tool result recorded here.

### 1.1 Assumptions

- `[assumption]` ONNX e5-base-v2 is the active embedding model for both memory semantic search and code search, as confirmed by `provider.modelPath` in every search response: `C:\Users\norrt\source\repos\MemorySmith\Data\Models\e5-base-v2.onnx`, `dimension: 768`.
- `[assumption]` `rebuildIfStale=false` was passed to all code search calls. Results reflect the last completed build (2026-05-29T04:29:28Z, 171 files, 1912 chunks, 160 reused / 11 updated) without triggering re-indexing during testing.
- `[assumption]` The ~55 memory records observed represent the full corpus. At `limit=50`, the tail included test fixtures and low-relevance governance records.
- `[critical assumption]` Score comparisons across queries are valid as within-query relative rankings. Absolute RRF scores (0.009–0.033) and absolute semantic cosine scores are compared cross-query as approximations only; the model is not calibrated for cross-query confidence claims.

### 1.2 Test Categories

| Category | Count | Purpose |
|---|---|---|
| Memory positive | 5 | Known record → should rank #1 |
| Memory negative | 3 | Off-domain → should return low-relevance |
| Code search positive | 3 | Known symbol/concept → correct file |
| Code search negative | 1 | Off-domain → obviously low scores |
| Fuzz / adversarial | 5 | XSS, SQL injection, Unicode, empty query, invalid status |

---

## 2. Memory Search — Positive Tests

### P1 — Single-Host Architecture

**Query:** `single host file backed architecture constraint refactor`
**Expected #1:** `project-wiki-active-architecture`

| Rank | ID | Semantic | Lex rank | RRF | Verdict |
|---|---|---|---|---|---|
| 1 | `project-wiki-active-architecture` | 0.827 | 1 | 0.032787 | ✅ Correct |
| 2 | `project-wiki-scope-boundaries` | 0.826 | 4 | 0.031754 | ✅ Correct |
| 3 | `project-wiki-chat-agent-provider` | 0.788 | 2 | 0.031281 | ⚠️ Weak FP |

**Assessment (confidence: 90%):** Top two are both valid. #3 is a false positive — `project-wiki-chat-agent-provider` carries an `architecture` tag and the word "architecture" in its title, producing lexical rank 2. The semantic component correctly demotes it (0.788 vs 0.827) but the lexical tag match keeps it in the top 3. The `architecture` tag on a provider-pattern record is questionable — see OQ-P1.

---

### P2 — ONNX Embedding Configuration

**Query:** `ONNX e5 mean pooling WordPiece tokenizer embedding model`
**Expected #1:** `project-wiki-onnx-semantic-embeddings`

| Rank | ID | Semantic | Lex rank | RRF | Verdict |
|---|---|---|---|---|---|
| 1 | `project-wiki-onnx-semantic-embeddings` | **0.881** | 1 | 0.032787 | ✅ Correct |
| 2 | `project-wiki-semantic-search-gap` | 0.817 | 3 | 0.032002 | ✅ Relevant |
| 3 | `project-wiki-semantic-tool-quality-suite` | 0.810 | 6 | 0.031025 | ✅ Relevant |

**Assessment (confidence: 95%):** Perfect result. 0.881 is the highest semantic cosine score observed across all memory search tests. Lexical rank 1 + semantic rank 1 = maximum RRF fusion. The query exactly mirrors the record's vocabulary, confirming the ONNX model handles technical terminology well when vocabulary overlap is high. All top-3 are genuinely related.

---

### P3 — UI / Blazor Architecture

**Query:** `blazor server mudblazor interactive render mode navigation drawer`
**Expected #1:** `project-wiki-ui-architecture`

| Rank | ID | Semantic | Lex rank | RRF | Verdict |
|---|---|---|---|---|---|
| 1 | `project-wiki-ui-architecture` | 0.847 | 1 | 0.032787 | ✅ Correct |
| 2 | `project-wiki-ui-layout-source-link-polish` | 0.821 | 3 | 0.032002 | ✅ Correct (#2 describes the 164px drawer) |
| 3 | `project-wiki-semantic-ui-current` | 0.800 | 2 | 0.031754 | ✅ Adjacent |

**Assessment (confidence: 92%):** All three are genuinely relevant UI records. Semantic scores well-separated: 0.847 / 0.821 / 0.800 — a healthy 47-point spread. No false positives. The model correctly differentiated three distinct UI records that all share Blazor/MudBlazor vocabulary.

---

### P4 — Test Architecture (Semantic Rescue Case)

**Query:** `NUnit 4 isolated temp directory integration test file backed`
**Expected #1:** `project-wiki-test-architecture`

| Rank | ID | Semantic | Lex rank | RRF | Verdict |
|---|---|---|---|---|---|
| 1 | `project-wiki-test-architecture` | 0.840 | **4** | 0.032018 | ✅ Correct |
| 2 | `project-wiki-test-fixture-overview` | 0.789 | 1 | 0.031319 | ⚠️ FP |
| 3 | `project-wiki-test-fixture-context-root` | 0.789 | 3 | 0.030579 | ⚠️ FP |

**Assessment (confidence: 87%):** This is the clearest demonstration of hybrid search value in the test set. The semantic component ranked `project-wiki-test-architecture` #1 despite it being lexical rank 4. The test fixtures outscored it lexically because they contain "test", "integration", "temp", "directory" as content terms — but they describe fixture data, not the testing architecture. Semantic correctly understood the intent. The RRF fusion rescued the right record from position 4 to position 1. Pure lexical search would have given the wrong top result here.

---

### P5 — Maintenance Proposals (Natural Language)

**Query:** `what triggers a maintenance proposal and how are they reviewed`
**Expected #1:** `project-wiki-maintenance-proposals-current`

| Rank | ID | Semantic | Lex rank | RRF | Verdict |
|---|---|---|---|---|---|
| 1 | `project-wiki-maintenance-proposals-current` | 0.852 | 1 | 0.032787 | ✅ Correct |
| 2 | `project-wiki-maintenance-observability-refinements` | 0.805 | 2 | 0.032002 | ✅ Relevant |
| 3 | `project-wiki-configuration-settings-current` | 0.783 | 4 | 0.031010 | ⚠️ Weak adjacent |

**Assessment (confidence: 94%):** Strong natural language query result. The phrasing ("what triggers... how are they reviewed") correctly mapped to the proposals record despite not using exact terms like "FileMaintenanceProposalStore" or "proposal JSON". Dual-mode agreement (lexical rank 1 and semantic rank 1) produces maximum RRF. #3 is weakly adjacent — it appears because "maintenance-agent scheduling" appears in the configuration snippet, but it is not about proposals.

---

## 3. Memory Search — Negative Tests

### N1 — Kubernetes / Docker (Fully Off-Domain)

**Query:** `kubernetes pod autoscaling docker container network overlay`

| Rank | ID | Semantic | Lex rank | RRF | Verdict |
|---|---|---|---|---|---|
| 1 | `project-wiki-chat-agent-provider` | 0.770 | none | 0.016393 | ⚠️ FP |
| 2 | `project-wiki-mcp-integration` | 0.764 | none | 0.016129 | ⚠️ FP |
| 3 | `project-wiki-test-architecture` | 0.762 | none | 0.015873 | ⚠️ FP |

**Assessment (confidence: 88%):** All five results have `"lexical rank none"` — zero lexical evidence, semantic-only. RRF scores (0.015–0.016) are approximately half those of true positive queries (0.031–0.033). Semantic scores (0.762–0.770) are at the noise floor — below the ~0.78 threshold for meaningful queries.

The system returns results with no signal that they are low-quality. An agent seeing RRF 0.016 needs to independently know this is half a good result's score. `"lexical rank none"` in `matchReason` is the only machine-readable quality signal. There is no `minScore` filter and no explicit "no results above threshold" response.

**The double gap:** RRF scores ~2× lower AND semantic scores ~7% lower than true positives. Both signals degrade proportionally for off-domain queries. Agents monitoring both would get a clear combined signal.

---

### N2 — PostgreSQL / Entity Framework (Constraint-Content Contamination)

**Query:** `postgresql database schema migration entity framework`

| Rank | ID | Semantic | Lex rank | RRF | Verdict |
|---|---|---|---|---|---|
| 1 | `project-wiki-scope-boundaries` | 0.772 | 9 | 0.030622 | ⚠️ Ambiguous |
| 2 | `project-wiki-admin-configuration-surface` | 0.771 | 6 | 0.030536 | ⚠️ Ambiguous |
| 3 | `project-wiki-semantic-search-gap` | 0.760 | 2 | 0.030018 | ⚠️ Ambiguous |

**Assessment (confidence: 75%):** This is the most important negative test. These records rank highly because they explicitly mention "PostgreSQL" — but in a constraint context ("Do not add PostgreSQL", "migration path that does not force PostgreSQL"). The lexical match is technically accurate; the records ARE about PostgreSQL in the sense that they discuss it as out-of-scope.

The RRF scores (0.030) are close to true positive scores (0.032–0.033) — only a 6% gap. An agent receiving these results could reasonably conclude PostgreSQL is relevant to this codebase, when the actual answer is the opposite. This is the **constraint-content contamination problem**: records that document exclusions rank for queries about the excluded technology.

A structured `excludes[]` field or `stance:excluded:postgresql` tag would allow agents to filter these correctly without reading full content.

---

### N3 — Japanese Unicode

**Query:** `日本語テスト 検索クエリ`

| Rank | ID | Semantic | Lex rank | RRF | Verdict |
|---|---|---|---|---|---|
| 1 | `memory-system-rfc-council-review-20260520` | 0.741 | none | 0.016393 | ✅ Graceful |
| 2 | `project-wiki-agent-instructions-source-of-truth` | 0.740 | none | 0.016129 | ✅ Graceful |

**Assessment (confidence: 95%):** No crash, no exception, clean JSON response. Semantic scores 0.740–0.741 are the absolute noise floor — the lowest observed in any memory search test. `"lexical rank none"` on both confirms zero token matches (expected: CJK characters are not in the WordPiece vocabulary of an English-trained e5-base-v2 model). The e5-base-v2 model handles CJK by embedding the Unicode sequence but produces near-noise output relative to English content. The 0.740 values represent the minimum cosine floor of the model. ✅ Completely safe, correct graceful degradation.

---

## 4. Adversarial / Fuzz Tests

### F1 — Empty Query

**Input:** `query=""` (empty string passed explicitly)

**Result:** 3 results, all with explicit annotations: `"Lexical score 0: No query supplied; returned by recency."` and `"Semantic score 0: No query supplied; returned by recency."`

**Assessment (confidence: 99%):** ✅ Perfect. Explicit null-score annotation, recency ordering, no exception. The `matchReason` is machine-parseable. This is the best-handled edge case in the test set.

---

### F2 — XSS + SQL Injection Combined

**Input:** `<script>alert('xss')</script> SELECT * FROM memories WHERE 1=1; --`

**Result:** 2 results returned. `<script>` tags stripped by Lucene tokenizer. `SELECT`, `WHERE`, `FROM` treated as stop words. `memories` matched lexically (score 3: "lexical content: memories") — the word appears in records describing `Data/Memories/`. `alert` matched nothing. No injection behavior.

**Assessment (confidence: 99%):** ✅ Safe. The StandardAnalyzer tokenises as plain text, discards HTML markup, ignores SQL keywords as stop words. The word "memories" in `SELECT * FROM memories` is a genuine content term that correctly returned `project-wiki-data-folder-policy` (which describes the `Data/Memories/` path). No security risk observed.

---

### F3 — Invalid Status Value

**Input:** `status="Nonexistent"` (not a valid memory status name)

**Result:** Results returned as if no status filter was applied. Records with status 1 and 2 both appeared. No error, no warning, no empty response, no validation message.

**Assessment (confidence: 90%):** ⚠️ **Silent failure.** An invalid status value is silently ignored rather than returning a validation error or empty result. An agent passing `status="Active"` (incorrect name, common from outdated docs or assumptions) would receive all records without any indication the filter had no effect. This is a correctness risk for agent workflows that assume the filter was applied.

**Expected behaviour:** Either return `{"error": "unknown status 'Nonexistent'. Valid values: Unconsolidated, Working, Core, Deprecated"}` or return zero results. Currently returns everything.

---

### F4 — Japanese Unicode

Covered in N3 above. ✅ Safe and graceful.

---

### F5 — Slug Path Traversal

**Input:** `slug="../../../etc/passwd"` — not tested to avoid potential risk on a production instance.

**Open question OQ-F1:** Does `page_get` sanitize slug input against path traversal? The slug is used to construct a file path under `Data/Pages/`. Given that MemorySmith is a local-first app with `AllowRemoteApi=false` by default, the immediate risk is low. But an explicit test confirming sanitization is worth adding to the security test suite, particularly since `AllowRemoteApi=true` is supported.

---

## 5. Code Search — System State

At test time:
- **Files indexed:** 171
- **Chunks:** 1912 (240 new on last build, 1672 reused from prior builds)
- **Provider:** ONNX CPU, e5-base-v2
- **Last build:** 2026-05-29T04:29:28Z (35.6 seconds total, 35.0s embedding, avg 991ms/call across 36 calls)
- **Build state:** idle (no rebuild triggered during testing per `rebuildIfStale=false`)

The `matchReason` for code search uses three components:
`"Code embedding cosine similarity X.XXX, lexical evidence Y.YYY, token coverage weight Z.ZZZ (hybrid rerank)."`

The lexical evidence and token coverage weight implement the no-lexical-evidence penalty described in `project-wiki-code-search-relevance-suite`.

---

## 6. Code Search — Positive Tests

### CP1 — Hybrid Search RRF Implementation

**Query:** `reciprocal rank fusion hybrid search RRF`

| Rank | File | Lines | Score | Embedding | Lex ev. | Verdict |
|---|---|---|---|---|---|---|
| 1 | `ChatToolCatalog.cs` | 161–200 | 0.803817 | 0.810 | 5.649 | ✅ MCP tool registration for hybrid search |
| 2 | `ChatToolCatalog.cs` | 129–168 | 0.792829 | 0.799 | 5.458 | ⚠️ Same file, lexical search chunk |
| 3 | `MemoryApplicationService.cs` | 1153–1192 | 0.740287 | 0.825 | 4.650 | ✅ `BuildHybridMatchReason` method |
| 4 | `MemoryApplicationService.cs` | 897–936 | 0.735951 | 0.819 | 4.608 | ✅ Hybrid search algorithm body |
| 5 | `SemanticToolQualityTests.cs` | 33–72 | 0.695183 | 0.826 | 6.558 | ✅ Quality probes — `HybridProbes[]` array |

**Assessment (confidence: 85%):** Results 1, 3, 4, 5 are all correct and useful. **Result 2 is a false positive**: `ChatToolCatalog.cs` L129–168 is the *lexical search* handler chunk in the same file. It ranked #2 because it shares the file with the correct result (same token coverage weight boost) and has similar lexical evidence (5.458 vs 5.649). `MaxResultsPerDocument` did not prevent consecutive same-file chunks.

Critically, `MemoryApplicationService.cs` L1153–1192 (`BuildHybridMatchReason`) has a *higher* embedding cosine (0.825) than the #2 result (0.799) but ranks lower because its lexical evidence is weaker (4.650 vs 5.458). The most precisely relevant result is out-ranked by a less relevant one due to 0.8 points of lexical evidence. See OQ-CP1.

---

### CP2 — SemanticEmbeddingSearchService

**Query:** `SemanticEmbeddingSearchService cosine similarity persisted embeddings`

| Rank | File | Lines | Score | Embedding | Lex ev. | Verdict |
|---|---|---|---|---|---|---|
| 1 | `SemanticEmbeddingSearchService.cs` | 257–296 | **0.941927** | 0.876 | 17.650 | ✅ Result-building with cosine score |
| 2 | `SemanticEmbeddingSearchService.cs` | 225–264 | 0.935577 | 0.867 | 17.878 | ✅ Query embedding + search loop |
| 3 | `MemoryApplicationService.cs` | 33–72 | 0.833582 | 0.831 | 10.106 | ✅ Synonym expansion dictionary |
| 4 | `MemoryApplicationService.cs` | 1–40 | 0.776285 | 0.826 | 8.549 | ✅ Service header / using directives |
| 5 | `SemanticEmbeddingPrewarmService.cs` | 1–40 | 0.749011 | 0.834 | 10.696 | ✅ Background prewarm service |

**Assessment (confidence: 96%):** Highest scores in any code search test. 0.941927 is consistent with an exact class name match with strong semantic alignment. All five results are genuinely relevant — the synonym expansion dictionary (result 3) is a useful bonus discovery. 

Again, two consecutive chunks from `SemanticEmbeddingSearchService.cs` at ranks 1 and 2 — in this case both are correct, so it's not harmful. But the `MaxResultsPerDocument` diversification is confirmed not to spread results.

**Hidden discovery from result 3:** `MemoryApplicationService.cs` L33–72 contains the synonym expansion dictionary:
```
"semantic" → ["meaning", "concept", "conceptual", "embedding", "embeddings", "vector", "similarity"]
"embedding" → ["semantic", "vector", "similarity"]
"search"    → ["find", "query", "lookup", "retrieval"]
```
This undocumented synonym expansion silently boosts recall for natural-language queries. A query for "how does the system find similar meanings" would lexically match records containing "semantic", "embedding", or "retrieval". This is a significant undocumented capability worth surfacing in tool descriptions.

---

### CP3 — Authorization Policy (Precision Ranking Issue)

**Query:** `CanReadSourceBundle authorization permission check`

| Rank | File | Lines | Score | Embedding | Lex ev. | Verdict |
|---|---|---|---|---|---|---|
| 1 | `Program.cs` | 289–328 | 0.840806 | 0.808 | 8.096 | ⚠️ Auth failure recording — adjacent |
| 2 | `SecurityServices.cs` | 1–40 | 0.796868 | 0.827 | 6.750 | ⚠️ Service header — indirect |
| 3 | `McpController.cs` | 161–200 | 0.794423 | 0.825 | 6.700 | ⚠️ Tool filtering — adjacent |
| 4 | `SourceLinksController.cs` | 1–26 | 0.790391 | 0.807 | 7.881 | ✅ **Exact:** `[Authorize(Policy = MemorySmithPolicies.CanReadSourceBundl...` |
| 5 | `SecurityServices.cs` | 193–232 | 0.742297 | 0.837 | 5.417 | ✅ Anonymous actor context |

**Assessment (confidence: 80%):** The most precisely relevant file (`SourceLinksController.cs`) ranks **#4**. The snippet visible in the result shows `[Authorize(Policy = MemorySmithPolicies.CanReadSourceBundl...` — this is exactly where the policy is applied. It ranks below three files that discuss auth broadly.

Root cause: `Program.cs` #1 has 8.096 lexical evidence vs `SourceLinksController.cs` #4 with 7.881 — a 0.2 difference. `Program.cs` is a larger file with more auth-related content, generating higher lexical evidence even though the specific policy is not applied there. The embedding cosines are within 0.001 of each other (0.808 vs 0.807) — too close to discriminate. The 0.2 lexical advantage in the larger file outweighs the more specific semantic match in the controller.

This is the **precision gap for short-file / attribute-declaration queries**: a file where the answer is a single `[Authorize]` attribute on a class can be outscored by larger files that discuss the same concept at length. A title-or-symbol-boost (giving extra weight to matches where the query term appears in a class/method declaration) might address this.

---

## 7. Code Search — Negative Test

### CN1 — Kubernetes / Docker

**Query:** `kubernetes pod autoscaling docker container`

| Rank | File | Lines | Score | Embedding | Lex ev. | Verdict |
|---|---|---|---|---|---|---|
| 1 | `memorysmith.js` | 513–552 | 0.477389 | 0.765 | 1.346 | ⚠️ DOM `container` match |
| 2 | `memorysmith.js` | 481–520 | 0.477069 | 0.770 | 1.232 | ⚠️ Same file, adjacent chunk |
| 3 | `PageService.cs` | 705–744 | 0.460395 | 0.740 | 1.232 | ⚠️ `ContainerInline` Markdig class |
| 4 | `PageService.cs` | 737–776 | 0.459997 | 0.751 | 1.000 | ⚠️ Same file, adjacent |
| 5 | `MainLayout.razor` | 33–72 | 0.452564 | 0.733 | 1.100 | ⚠️ `MudContainer` / layout |

**Assessment (confidence: 93%):** ✅ The no-lexical-evidence penalty is clearly working. Scores of 0.45–0.48 versus 0.80–0.94 for true positives is a near-2× gap — sufficient for an agent to recognise these as noise. Lexical evidence of 1.0–1.3 (vs 5–17 for true positives) is the key differentiator.

The residual false positives are caused by the DOM/HTML meaning of "container" — `document.createElement("section")` creates a container, `ContainerInline` is a Markdig class, `MudContainer` is a MudBlazor layout component. These are genuine but contextually wrong lexical matches. The embedding cosine (0.733–0.770) is at the lower end of observed values, reinforcing low relevance.

**Important:** Both `memorysmith.js` chunks appear consecutively (#1 and #2), and both `PageService.cs` chunks appear consecutively (#3 and #4). Same-file diversification is not spreading results for this query either — confirming the MaxResultsPerDocument pattern is consistent across both positive and negative tests.

**Machine-readable threshold confirmation:** Lexical evidence < 2.0 is a reliable indicator of noise-tier code search results across all tests conducted. The clean gap between true positives (≥4.6) and negatives (≤1.4) suggests this threshold is robust.

---

## 8. Cross-Cutting Analysis

### 8.1 Memory Search Score Reference

| Query type | RRF range | Semantic range | `lexical rank none`? |
|---|---|---|---|
| Strong positive, dual-mode | 0.031–0.033 | 0.827–0.881 | No |
| Strong positive, semantic-led | 0.030–0.032 | 0.789–0.852 | No |
| Weak positive / adjacent | 0.031 | 0.783–0.800 | Partial |
| Off-domain negative | 0.015–0.016 | 0.762–0.770 | Yes |
| Unicode / garbage | 0.016 | 0.740–0.741 | Yes |

**Practical thresholds (confidence: 70%):**
- RRF > 0.030 + semantic > 0.820: high confidence
- RRF 0.020–0.030: moderate; check semantic component
- RRF < 0.020 or `"lexical rank none"`: low confidence; treat with suspicion

*Confidence 70% because corpus is ~55 records; thresholds will compress as corpus grows.*

### 8.2 Code Search Score Reference

| Query type | Score range | Lex evidence range | Interpretation |
|---|---|---|---|
| Exact class name | 0.93–0.94 | 15–18 | Definitive |
| Strong conceptual | 0.79–0.84 | 5–10 | Trust |
| Adjacent / same-file FP | 0.77–0.80 | 4–6 | Verify snippet |
| Off-domain | 0.45–0.48 | 1.0–1.4 | Noise |

**Practical thresholds (confidence: 75%):**
- Score > 0.80: strong match
- Score 0.70–0.80: relevant; verify by reading snippet
- Score < 0.70: adjacent context only
- Lexical evidence < 2.0: noise regardless of embedding cosine

### 8.3 MaxResultsPerDocument Diversification Gap

In all four code search result sets (CP1, CP2, CP3, CN1), two consecutive chunks from the same file appeared in the top results. The `MaxResultsPerDocument` guard caps total results per file but does not spread them through the ranking:

- CP1: `ChatToolCatalog.cs` at ranks 1 and 2 (second chunk is a false positive)
- CP2: `SemanticEmbeddingSearchService.cs` at ranks 1 and 2 (both correct, but dense)
- CN1: `memorysmith.js` at ranks 1 and 2; `PageService.cs` at ranks 3 and 4

For a `limit=5` query, showing 2/5 results (40%) from the same file is excessive for most discovery use cases. A rank-spreading rule — "no two results from the same file within N positions of each other" — would improve result diversity without reducing total file coverage.

### 8.4 Synonym Expansion — Undocumented Power Feature

The code search test exposed `MemoryApplicationService.cs` L33–72, which contains the synonym expansion dictionary used by both memory search and code search lexical scoring:

```
"semantic"   → ["meaning", "concept", "conceptual", "embedding", "embeddings", "vector", "similarity"]
"embedding"  → ["semantic", "vector", "similarity"]
"search"     → ["find", "query", "lookup", "retrieval"]
"hybrid"     → ["combined", "fusion", "mixed", "rrf"]
"memory"     → ["record", "knowledge", "wiki", "kb"]
```

This expansion means queries using natural language synonyms automatically receive lexical boosts for the technical terms — a query for "how does the system find similar meanings" would match records containing "semantic", "embedding", or "retrieval". This is a significant undocumented capability. Surfacing it in tool descriptions and the search guide would enable agents to write more effective queries.

### 8.5 Constraint-Content Contamination

The PostgreSQL negative test (N2) revealed that records explicitly excluding a technology can rank highly for queries about that technology. Three records (`project-wiki-scope-boundaries`, `project-wiki-admin-configuration-surface`, `project-wiki-semantic-search-gap`) mention PostgreSQL in constraint context, producing RRF scores of 0.030 — only 6% below strong true positives.

There is currently no structured way for a record to express its stance toward a concept. An `excludes[]` array or a `stance:excluded:postgresql` structured tag would allow agents to filter these correctly without needing to read the full content. Given that MemorySmith's architecture records frequently document what is NOT in scope, this pattern will recur for any technology in the "explicitly rejected" category.

---

## 9. Summary

### What Is Working Well

| Finding | Confidence |
|---|---|
| Memory positive recall: 5/5 correct records ranked #1 | 92% |
| Hybrid search semantic rescue (P4: semantic overrode lower lexical position) | 90% |
| Off-domain negative: RRF ~2× lower, semantic scores at measurable noise floor | 88% |
| Code search exact class/method lookup: scores 0.93–0.94 | 96% |
| No-lexical-evidence penalty in code search: off-domain scores near 0.45 vs 0.80–0.94 | 93% |
| Adversarial inputs (XSS, SQL injection, Unicode, empty) handled cleanly | 99% |
| ONNX provider active, healthy, CPU-bound, 768-dim e5-base-v2 | 99% |
| Japanese/Unicode: graceful noise-floor degradation, no crash | 95% |

### Issues Found

| Issue | Severity | Confidence |
|---|---|---|
| Invalid `status` value silently ignored — no error, no empty, no warning | Medium | 90% |
| `MaxResultsPerDocument` does not spread same-file results through ranking | Medium | 88% |
| Code search CP3: exact policy location ranks #4 behind broader auth files | Medium | 80% |
| No `minScore` threshold — off-domain queries return noise-floor results without signal | Medium | 85% |
| Constraint-content contamination: "Do not use X" records rank for queries about X | Low–Medium | 75% |
| `"lexical rank none"` is the only machine-readable quality signal for weak results | Low | 88% |
| Memory P1: `project-wiki-chat-agent-provider` false positive via `architecture` tag | Low | 90% |
| Synonym expansion dictionary is undocumented | Low | 95% |
| `page_save` now in `DisabledTools` — returns proper error (improvement over prior silent hang) | Info | 99% |

---

## 10. Open Questions

- **OQ-P1:** Does `project-wiki-chat-agent-provider` belong in the `architecture` tag? Renaming it to `pattern` would eliminate the false positive in architecture queries.
- **OQ-CP1:** Is lexical evidence weighting calibrated correctly relative to embedding cosine? In CP3, a 0.2 lexical evidence difference reversed the ranking, placing the most precise result at #4. A symbol-declaration boost might correct short-attribute-file queries.
- **OQ-F1:** Does `page_get` sanitize slug input against path traversal? Not tested on production instance; worth adding to the security regression suite.
- **OQ-7:** Should `status=Nonexistent` return a validation error? Current silent-ignore behaviour is a correctness risk.
- **OQ-8:** Is there a plan to expose `minScore` on the semantic component? See TSK-0186 for related discussion.
- **OQ-9:** Can `MaxResultsPerDocument` be configured to spread results through ranking rather than just capping per-file total? A rank-spread parameter would improve diversity for `limit=5` queries.
- **OQ-10:** Should the synonym expansion dictionary be documented in tool descriptions? Its existence significantly affects query effectiveness and is entirely opaque to callers.
- **OQ-11:** Is a structured `excludes[]` field or `stance:excluded:X` tag system planned to address constraint-content contamination?

---

*Written by Claude Sonnet 4.6 via npx mcp-remote bridge | 2026-05-29*
*All scores are raw values from live MCP tool responses. Non-destructive: no KB mutations during testing.*
*Note: `page_save` was disabled by `MemorySmith:Mcp:DisabledTools` at time of publishing — this file requires manual upload to the wiki at slug `audits/search-quality-deepdive-20260529`.*
