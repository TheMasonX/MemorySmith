# Code Search Benchmark Breakdown: CPU Fallback vs CUDA (2026-05-28)

This page captures the current measured code-search indexing benchmarks from local MemorySmith runs, with direct CPU-fallback versus CUDA comparisons and a reliability-oriented interpretation.

## Scope And Caveats

- These numbers are from the local host and local repo snapshot used in each run.
- The runs include two nearby corpus sizes:
  - `225` files / `2,388` chunks (earlier CPU/CUDA comparison set)
  - `228` files / `2,417` chunks (later CUDA batch-sweep set)
- Because corpus size changed, compare within each measurement set first, then compare across sets with caution.
- Warm runs (`ForceRebuild=false`) measure reuse behavior and request overhead, not full re-embedding throughput.

## Source Artifacts

Primary comparison set:

- `artifacts/browser-validation/code-search-index-summary-cpu-fallback-cold.json`
- `artifacts/browser-validation/code-search-index-summary-cpu-fallback-warm.json`
- `artifacts/browser-validation/code-search-index-summary-cuda-cold.json`
- `artifacts/browser-validation/code-search-index-summary-cuda-warm.json`
- `artifacts/browser-validation/code-search-index-summary-cuda-warm-postcold.json`

CUDA batch sweep set:

- `artifacts/browser-validation/code-search-index-summary-cuda-batch1-cold.json`
- `artifacts/browser-validation/code-search-index-summary-cuda-batch2-cold.json`
- `artifacts/browser-validation/code-search-index-summary-cuda-batch4-cold.json`
- `artifacts/browser-validation/code-search-index-summary-cuda-batch8-cold.json`
- `artifacts/browser-validation/code-search-index-summary-cuda-batch16-cold.json`

## CPU Fallback vs CUDA: Main Comparison (225 Files / 2,388 Chunks)

| Scenario | Elapsed (ms) | Build Duration (ms) | Files/s | Chunks/s | Provider Status |
| --- | ---: | ---: | ---: | ---: | --- |
| CPU fallback cold (`ForceRebuild=true`) | 418,824 | 417,000 | 0.54 | 5.73 | Requested CUDA unavailable; fell back to CPU |
| CPU fallback warm (`ForceRebuild=false`) | 771 | 0 | n/a | n/a | Requested CUDA unavailable; fell back to CPU |
| CUDA cold (`ForceRebuild=true`) | 525,224 | 524,000 | 0.43 | 4.56 | ONNX provider available via Cuda (0) |
| CUDA warm first run (`ForceRebuild=false`) | 53,849 | 0 | n/a | n/a | ONNX provider available via Cuda (0) |
| CUDA warm post-cold (`ForceRebuild=false`) | 865 | 0 | n/a | n/a | ONNX provider available via Cuda (0) |

### Derived Comparison

- Cold rebuild: CUDA is slower than CPU fallback by `106,400 ms` (about `25.4%` slower).
- Warm steady-state reuse: CUDA post-cold warm (`865 ms`) is close to CPU fallback warm (`771 ms`) but still about `12.2%` slower.
- First CUDA warm request (`53,849 ms`) is a one-time warm-up outlier and should not be compared to steady-state warm numbers.

## CUDA Batch Sweep (228 Files / 2,417 Chunks)

| CUDA Batch Size | Elapsed (ms) | Build Duration (ms) | Embedding Calls | Embedded Chunks | Avg Embedding ms/call |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 116,992 | 116,000 | 2,417 | 2,417 | 46.834 |
| 2 | 125,565 | 125,000 | 1,273 | 2,417 | 96.159 |
| 4 | 140,630 | 139,000 | 705 | 2,417 | 195.363 |
| 8 | 147,988 | 146,000 | 433 | 2,417 | 335.215 |
| 16 | 159,699 | 159,000 | 300 | 2,417 | 523.653 |

### Batch Sweep Interpretation

- End-to-end rebuild time worsens monotonically as fixed batch size increases.
- Lower embedding call count did not produce a faster full rebuild on this workload.
- Most runtime in these runs is embedding-bound (`embeddingMilliseconds` dominates timing breakdown), not DB write-bound.

## Timing-Breakdown Highlights

From the batch sweep timing fields:

- `embeddingMilliseconds` dominates total duration.
- `databaseWriteMilliseconds` remains relatively small compared to embedding time.
- `providerInitializationMilliseconds` is negligible in rebuild runs once the provider is active.

This supports the working hypothesis that pipeline-level embedding overhead (tokenization, padding, tensor assembly, and per-call orchestration) is currently the decisive factor, not SQLite writes.

## Operational Conclusions

1. Keep `EmbeddingBatchSize=1` as the current default for code-search rebuilds on this host profile.
2. Keep CUDA optional and preserve CPU fallback as a reliability baseline.
3. Treat first CUDA warm runs separately from steady-state warm measurements.
4. Prioritize better batching strategy research (token-budget, length-aware grouping, adaptive batching) before changing defaults again.
5. Continue benchmarking with strict run labeling (`cold`, `warm-first`, `warm-steady`, corpus size) to avoid mixed conclusions.

## Cross-Reference

- [Codebase Vector Search Whitepaper Notes](codebase-vector-search-whitepaper.md)
- [Semantic Acceleration Setup Guide](../guides/semantic-acceleration-setup.md)
