# Codebase Vector Search Whitepaper Notes

This page captures the current state of MemorySmith's codebase vector-search system, the refinements already made, the benchmark evidence gathered so far, and the remaining research questions. Treat it as a working whitepaper outline grounded in current code and measured behavior rather than a marketing summary.

## Executive Summary

MemorySmith now has a real codebase vector-search pipeline with:

- a SQLite-backed code index;
- local ONNX embeddings with CPU, CUDA, and OpenVINO runtime selection;
- CPU fallback when hardware acceleration is unavailable;
- startup prewarm by default;
- warm incremental rebuild reuse;
- live build status and timing breakdowns;
- exact-query caching;
- an optional batch document-embedding path;
- fail-closed handling for document embedding errors so failed files are marked as failed instead of being silently written with empty vectors.

The main current conclusion is uncomfortable but useful: on the active MemorySmith repo and CUDA host, the provider-only batch benchmark is faster than scalar inference, yet the full end-to-end code-search rebuild still gets slower as batch size increases. The correct default today is scalar document embedding, not because batching is theoretically bad, but because the current workload and pipeline shape do not convert the micro-benchmark win into a rebuild win.

## System Overview

The current code-search stack is built around these responsibilities:

| Layer | Responsibility |
| --- | --- |
| `CodeSearchService` | Scans the configured repo roots, chunks documents, reuses unchanged documents, embeds prepared chunks, writes SQLite rows, and serves code-search results plus build status. |
| `SemanticEmbeddingSearchService` | Hosts ONNX embedding infrastructure, tokenizer/pooling behavior, provider selection, and provider-health reporting. |
| `OnnxTextEmbeddingProvider` | Performs ONNX session setup, inference, pooling, and optional batch embedding. |
| SQLite index | Persists chunk rows and reusable metadata under `Data/Graph/code-search/code-search.db`. |
| MCP/chat tools | Expose `memorysmith_code_search` and `memorysmith_code_search_status` so indexing/search is visible to agents and operators. |

The design target is local-first reliability, not cloud-scale novelty. The system is supposed to be inspectable, recoverable, and practical on a single workstation or a small self-hosted service.

## Refinements Added So Far

### 1. Minimal End-To-End Code Search Slice

The original `TSK-0195` implementation established a durable baseline:

- repo scanning over configured project roots;
- file chunking for code search;
- SQLite-backed persistence;
- MCP/chat exposure through `memorysmith_code_search`.

That change made codebase search real, but not yet user-friendly under longer rebuilds or hardware-provider experiments.

### 2. Import Reliability And Progress Visibility

The next hardening sweep focused on trust and diagnosability:

- live build state and counters;
- warm incremental rebuild reuse;
- partial-failure isolation per file;
- batched SQLite document replacement;
- exact-query caching;
- `memorysmith_code_search_status` for operator-visible telemetry.

This was the first point where the import stopped feeling like a black box.

### 3. Hardware Acceleration And CPU Fallback

The semantic provider now supports runtime selection of:

- `Cpu`
- `Cuda`
- `OpenVino`

The runtime selection is intentionally decoupled from the published ONNX package flavor. That lets MemorySmith expose optional hardware acceleration without making the entire semantic stack brittle. When a requested hardware provider fails and `CpuFallbackEnabled=true`, the app keeps semantic embeddings available through CPU ONNX instead of silently disabling the feature.

### 4. Startup Prewarm

`PrewarmOnStartupEnabled` now defaults to `true`. The goal is simple: do not make the first real semantic or code-search request pay the full lazy ONNX initialization cost when the app can warm the provider in the background after startup.

This improves operator experience but does not solve steady-state rebuild throughput by itself.

### 5. Batch Document Embedding Path

An explicit batch document-embedding path was added so code-search indexing could reuse a batched provider when available. That work was valid and necessary to test, but the later live benchmark sweep showed that keeping the path available does not imply it should be the default on the current workload.

### 6. Fail Closed On Embedding Errors

The indexing path now treats document embedding failures as actual per-file indexing failures.

This matters because the earlier behavior could silently accept a failed `TryEmbed(...)` call, keep going with an empty vector, and write a document into the SQLite index without a usable embedding. That kind of partial success is worse than a visible failure because it poisons the corpus quietly and makes relevance problems look like ranking issues instead of ingestion defects.

The current behavior is stricter and more trustworthy:

- batch embedding can still fall back to scalar when the batch call fails;
- if the scalar path also fails or returns an empty vector, the affected document is skipped;
- build status records the file as failed instead of pretending the document indexed successfully.

## Current Measured Evidence

### Live CUDA Rebuild Sweep

Cold forced rebuilds were measured against the live MemorySmith repo and the same host/corpus across batch sizes:

| Batch Size | Elapsed Rebuild Time | Embedding Calls |
| --- | ---: | ---: |
| 1 | 116,992 ms | 2,417 |
| 2 | 125,565 ms | 1,273 |
| 4 | 140,630 ms | 705 |
| 8 | 147,988 ms | 433 |
| 16 | 159,699 ms | 300 |

The curve is monotonic in the wrong direction. Fewer embedding calls did not produce faster rebuilds.

Artifacts for that sweep were captured under `artifacts/browser-validation/` as:

- `code-search-index-summary-cuda-batch1-cold.json`
- `code-search-index-summary-cuda-batch2-cold.json`
- `code-search-index-summary-cuda-batch4-cold.json`
- `code-search-index-summary-cuda-batch8-cold.json`
- `code-search-index-summary-cuda-batch16-cold.json`

### Provider-Only CUDA Micro-Benchmark

The provider itself still showed a batching win:

| Mode | Median Latency |
| --- | ---: |
| Scalar | 447 ms |
| Batch | 307 ms |

That is exactly why end-to-end measurement matters. The provider benchmark alone would have supported the wrong default.

### Query Probe Result

Repeated and distinct `memorysmith_code_search` query probes were both about `18-20 ms`. That means the current full chunk reload path on uncached queries is not the dominant user-visible bottleneck on the active workload.

## Interpretation

The most likely current explanation is that the pipeline overhead outside raw ONNX inference is dominating the apparent batching win. Plausible contributors include:

- tokenizer and tensor-construction overhead;
- padding waste from heterogeneous chunk lengths;
- extra memory movement and allocation costs;
- synchronous build-loop structure around file-by-file chunk preparation;
- corpus shape that is too small or too irregular to amortize the batch overhead.

The system is already telling us something valuable: a faster inference primitive does not guarantee a faster indexing product.

## What Is Probably Not The Main Bottleneck Right Now

Current evidence argues against spending immediate engineering time on these paths first:

- query-side ANN complexity purely to reduce already-acceptable `18-20 ms` searches;
- SQLite write-path replacement as the first optimization target;
- removing CPU fallback to simplify the provider matrix;
- assuming GPU activation alone is enough to improve end-to-end rebuild speed.

Those may matter later, but they are not the highest-confidence next moves on the current evidence.

## High-Confidence Decisions Already Made

### Keep Scalar As The Default Document Embedding Mode

The default `CodeSearchOptions.EmbeddingBatchSize` was reset to `1` after the live CUDA sweep. This is benchmark-backed and should remain the conservative default until a new end-to-end benchmark proves otherwise.

### Keep The Batch Path Available

The batch path should stay in the codebase even though it is not the default. It remains useful for:

- future corpus-shape experiments;
- provider tuning on other hardware;
- OpenVINO testing on Intel hardware;
- token-budget or adaptive batching experiments that are more sophisticated than a fixed-size batch.

### Keep CPU Fallback And Startup Prewarm

These are reliability features, not optional polish. Hardware acceleration support is only acceptable when failure modes remain diagnosable and the system still works on CPU.

## Research Agenda

The next meaningful research questions are not generic vector-database questions. They are specific to the current MemorySmith stack.

### 1. Explain The Batch Regression

The highest-value technical question is why the provider-only batch benchmark wins while the live rebuild loses. The answer likely requires deeper instrumentation around:

- token counts per chunk;
- padding ratios per batch;
- tokenizer time versus inference time;
- tensor allocation and copy costs;
- batch-shape distribution across the real corpus.

### 2. Test Better Batch Shapes, Not Just Bigger Fixed Batches

Fixed `2/4/8/16` batching is a blunt instrument. The more defensible next experiments are:

- token-budget batching;
- same-length or near-length grouping;
- adaptive batching that backs off when padding waste spikes;
- cross-file prepared-chunk batching instead of narrow file-local batches.

### 3. Measure Cold, Warm, And Incremental Rebuilds Separately

MemorySmith now has enough observability to stop mixing these together. Future benchmarks should always separate:

- cold process startup plus first build;
- warm full rebuild;
- warm incremental rebuild with reuse;
- query latency under warmed caches.

### 4. Benchmark OpenVINO On An Intel Host

OpenVINO support is wired in, but the current evidence is CUDA-heavy because that is the available host. OpenVINO needs real measurements before any provider-specific recommendation is credible.

### 5. Decide Whether Finer-Grained Reuse Is Worth It

Document-level reuse already exists. Chunk-level reuse could reduce rebuild cost further, but only if the extra bookkeeping and hash management do not complicate the system more than they help.

## Suggested Whitepaper Sections For Future Expansion

If this page grows into a fuller paper, expand it in this order:

1. Measurement methodology and reproducibility.
2. Corpus-shape analysis for the MemorySmith repo.
3. Provider initialization versus steady-state throughput.
4. Batch-shape and padding-efficiency analysis.
5. Retrieval quality measurements, not just rebuild speed.
6. Operator-trust design: status, errors, warnings, and fallback visibility.

## Related Pages

- [Search System](../features/search-system.md)
- [Semantic Acceleration Setup Guide](../guides/semantic-acceleration-setup.md)
- [Codebase Vector Search Deep Research Prompt](codebase-vector-search-deep-research-prompt.md)
- [Code Search Benchmark Breakdown: CPU Fallback vs CUDA (2026-05-28)](code-search-benchmark-cpu-vs-cuda-20260528.md)
