# Code Search Benchmark: E5 vs Nomic Embed Text v1.5 (2026-05-28)

This page tracks the direct A/B benchmark and relevance comparison between the current E5 baseline and `nomic-embed-text-v1.5` for MemorySmith code search.

## Executive Summary

- Rebuild throughput: E5 remains faster (`1,055,795 ms`) than Nomic (`1,310,318 ms`), with Nomic about `24%` slower on the same `161`-file / `1746`-chunk corpus.
- Query latency: Nomic is faster on all six benchmark prompts in this run (about `11%` to `77%` better average latency).
- Relevance suite implementation: Added fixed suite + scorer via `Scripts/code-search-relevance-suite.json` and `Scripts/Measure-CodeSearchRelevance.ps1`.
- Relevance suite current result: both E5 and Nomic pass `8/8` after forcing model-consistent index ownership before E5 scoring.
- Latency under relevance-suite methodology still favors Nomic, while E5 retains the faster rebuild profile.

## Scope

- Repository: `MemorySmith`
- Targets: `MemorySmith.App`, `MemorySmith.Core`, `MemorySmith.Storage`, `MemorySmith.Tests`, `Scripts`
- Benchmark harness:
  - `Scripts/Warm-CodeSearchIndex.ps1`
  - `Scripts/Measure-CodeSearchQueries.ps1`
- Query baseline file (E5): `artifacts/browser-validation/code-search-query-baseline-e5-v2-20260528-clean.json`
- Rebuild baseline file (E5): `artifacts/browser-validation/code-search-index-summary-e5-v2-20260528.json`
- Query baseline file (Nomic): `artifacts/browser-validation/code-search-query-baseline-nomic-v1-5-20260528.json`
- Rebuild baseline file (Nomic): `artifacts/browser-validation/code-search-index-summary-nomic-v1-5-20260528.json`
- Relevance suite file (E5 corrected): `artifacts/browser-validation/code-search-relevance-e5-v3-20260528.json`
- Relevance suite file (Nomic): `artifacts/browser-validation/code-search-relevance-nomic-v1-5-20260528.json`

## Current Status

- E5 and Nomic rebuild/query artifacts are captured on the same corpus size (`161` files / `1746` chunks).
- Earlier E5 query file `code-search-query-baseline-e5-v2-20260528.json` is retained for history but was captured while indexing was active (`1742` chunks) and is not used for final A/B interpretation.

## E5 Baseline Snapshot

### Rebuild (cold)

| Model | Files | Chunks | Elapsed (ms) | Build (ms) | Files/s | Chunks/s | Avg Embedding ms/call |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| E5 (`e5-base-v2.onnx`) | 161 | 1746 | 1055795 | 1053000 | 0.15 | 1.66 | 3341.042 |

### Query Baseline (warm)

The first sample in each query set is consistently higher than subsequent runs, so median and min values better represent steady-state behavior on this host.

| Query Name | Avg ms | Median ms | Min ms | Max ms | Top Document |
| --- | ---: | ---: | ---: | ---: | --- |
| semantic provider path | 592.500 | 1527.675 | 115.037 | 1527.675 | `MemorySmith.App/Components/Pages/Admin.razor` |
| query telemetry | 261.165 | 554.907 | 108.028 | 554.907 | `MemorySmith.Tests/CodeSearchServiceTests.cs` |
| vector prefilter | 258.636 | 543.455 | 82.549 | 543.455 | `MemorySmith.Tests/CodeSearchServiceTests.cs` |
| benchmark harness | 191.579 | 506.250 | 33.445 | 506.250 | `MemorySmith.Tests/CodeSearchServiceTests.cs` |
| page validation | 297.627 | 695.855 | 49.263 | 695.855 | `MemorySmith.App/Components/Pages/Pages.razor` |
| semantic provider tests | 279.794 | 698.804 | 34.710 | 698.804 | `MemorySmith.Tests/SemanticEmbeddingPathTests.cs` |

## Nomic Snapshot

### Rebuild (cold)

| Model | Files | Chunks | Elapsed (ms) | Build (ms) | Files/s | Chunks/s | Avg Embedding ms/call |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Nomic (`nomic-embed-text-v1.5.onnx`) | 161 | 1746 | 1310318 | 1306000 | 0.12 | 1.34 | 4152.744 |

### Query Baseline (warm)

| Query Name | Avg ms | Median ms | Min ms | Max ms | Top Document |
| --- | ---: | ---: | ---: | ---: | --- |
| semantic provider path | 134.940 | 277.313 | 17.376 | 277.313 | `MemorySmith.App/Services/SemanticEmbeddingSearchService.cs` |
| query telemetry | 84.586 | 131.603 | 22.733 | 131.603 | `MemorySmith.App/Services/CodeSearchService.cs` |
| vector prefilter | 144.456 | 347.477 | 16.294 | 347.477 | `MemorySmith.App/Services/CodeSearchService.cs` |
| benchmark harness | 170.376 | 367.349 | 18.833 | 367.349 | `MemorySmith.Tests/SearchBenchmarkTests.cs` |
| page validation | 104.484 | 270.826 | 20.495 | 270.826 | `MemorySmith.App/Services/PageService.cs` |
| semantic provider tests | 126.504 | 224.954 | 32.620 | 224.954 | `MemorySmith.App/Services/SemanticEmbeddingSearchService.cs` |

## Side-By-Side Summary

### Rebuild Cost

- Nomic rebuild elapsed: `1,310,318 ms` vs E5 `1,055,795 ms` (Nomic `24.11%` slower).
- Nomic avg embedding call time: `4,152.744 ms` vs E5 `3,341.042 ms` (Nomic `24.29%` slower).

### Query Latency

| Query Name | E5 Avg ms | Nomic Avg ms | Nomic Faster % |
| --- | ---: | ---: | ---: |
| semantic provider path | 592.500 | 134.940 | 77.23 |
| query telemetry | 261.165 | 84.586 | 67.61 |
| vector prefilter | 258.636 | 144.456 | 44.15 |
| benchmark harness | 191.579 | 170.376 | 11.07 |
| page validation | 297.627 | 104.484 | 64.89 |
| semantic provider tests | 279.794 | 126.504 | 54.79 |

### Top-Result Drift (Quick Read)

- Nomic top hits for implementation-focused telemetry/prefilter queries are `MemorySmith.App/Services/CodeSearchService.cs`.
- E5 clean run top hits for those same queries skewed toward test files (`MemorySmith.Tests/CodeSearchServiceTests.cs`) under current weighting behavior.
- This suggests Nomic may be more aligned with implementation intent on these sample queries, but a larger fixed-query spot-check set is still recommended before default switch.

## Relevance Suite Results (Completed)

The fixed relevance suite now exists in `Scripts/code-search-relevance-suite.json` and is executed by `Scripts/Measure-CodeSearchRelevance.ps1`.

Pass/fail totals:

- E5 corrected run (`code-search-relevance-e5-v3-20260528.json`): `8/8` passed (`100%`).
- Nomic run (`code-search-relevance-nomic-v1-5-20260528.json`): `8/8` passed (`100%`).

### Case-Level Readout

| Case | E5 | Nomic | E5 Warm Avg ms (no first) | Nomic Warm Avg ms (no first) |
| --- | --- | --- | ---: | ---: |
| implementation-telemetry | pass | pass | 2251.307 | 13.331 |
| implementation-prefilter | pass | pass | 1931.827 | 66.092 |
| page-validation-intent | pass | pass | 2146.240 | 97.564 |
| semantic-provider-path | pass | pass | 2211.436 | 14.801 |
| semantic-provider-tests | pass | pass | 2113.255 | 41.070 |
| benchmark-harness-intent | pass | pass | 2139.772 | 12.637 |
| ranking-weight-implementation | pass | pass | 2041.100 | 48.266 |
| nunit-regression-tests | pass | pass | 2393.986 | 45.714 |

Interpretation:

- The first E5 relevance sample includes forced rebuild work (`~607,846 ms`) by design; warm-only averages above exclude that first sample.
- With fair indexing and fixed expectations, both models meet relevance intent constraints on this suite.
- Nomic remains materially faster under this relevance-suite query workload.

## Relevance Spot-Check Plan

1. Expand the fixed suite from `8` to `20+` cases covering page routing, auth/admin, chat tools, and task workflows.
2. Add expected-in-top-3 constraints (not only top-1) for ambiguous intents.
3. Version the suite by date/model family so trend comparisons stay reproducible.

## Recommendation

1. Keep E5 as the default rebuild path for now because rebuild throughput is materially better (`24%` faster on this corpus).
2. Continue Nomic as an optional query-optimized candidate because query latency was lower across all measured prompts in this run.
3. Keep the new fixed relevance suite in CI and extend it to 20-30 queries with expected top-3 paths by intent category.
4. Prioritize separating semantic and code-search embedding configuration seams so model experimentation does not couple both surfaces.

