# Vector Search Research Index

This page is the coordination hub for the current MemorySmith vector-search and semantic-search research stream. It consolidates benchmark evidence, deep audits, implemented hardening changes, and mapped task records.

## Current Papers And Audits

- [Codebase Vector Search Whitepaper Notes](../codebase-vector-search-whitepaper.md)
- [Code Search Benchmark Breakdown: CPU Fallback vs CUDA](../code-search-benchmark-cpu-vs-cuda-20260528.md)
- [Deep Research Prompt: MemorySmith Codebase Vector Search And Embedding Stack](../codebase-vector-search-deep-research-prompt.md)
- [Vector Search General Codebase Report (2026-05-28)](../../audits/vector-search-general-codebase-report_5-28-26.md)

## Benchmarks

### Latest Snapshot

- CPU fallback cold rebuild: about `418,824 ms` (`225` files / `2,388` chunks)
- CUDA cold rebuild: about `525,224 ms` on the same corpus (about `25.4%` slower than CPU fallback in that run)
- CUDA batch sweep (`228` files / `2,417` chunks):
  - batch `1`: `116,992 ms`
  - batch `2`: `125,565 ms`
  - batch `4`: `140,630 ms`
  - batch `8`: `147,988 ms`
  - batch `16`: `159,699 ms`

### Interpretation

- Current end-to-end rebuild performance does not support fixed-size batch defaults above `1` on the measured host/corpus.
- Provider-level micro-bench wins are not sufficient for default tuning decisions.
- Current optimization focus should remain evidence-first: candidate reduction and batching-shape strategy, not speculative complexity.

## Implemented Hardening (2026-05-28)

- Unsupported execution providers now fail fast before ONNX native initialization, fixing CI instability for unsupported-provider diagnostics.
- Code-search document embedding failures now fail closed at file scope instead of silently persisting empty vectors.
- Code-search query timing telemetry now supports explicit disable, sampling interval throttling, and slow-query warning thresholds.

## Task Mapping

Completed:

- [TSK-0196](../../tasks?task=TSK-0196) Fail fast on unsupported ONNX execution provider before native initialization.
- [TSK-0197](../../tasks?task=TSK-0197) Add throttled code-search query timing telemetry.

Backlog from deep audit:

- [TSK-0198](../../tasks?task=TSK-0198) Candidate prefilter before code-search vector scoring.
- [TSK-0199](../../tasks?task=TSK-0199) Semantic-search candidate index and ANN migration plan.
- [TSK-0200](../../tasks?task=TSK-0200) Surface semantic fallback and hybrid provider metadata in default API responses.

## Next High-ROI Validation

1. Run CI and confirm the unsupported-provider path remains deterministic across runner environments.
2. Capture query timing samples under real `/mcp` and `/api/search/code` load with telemetry disabled and enabled to quantify overhead.
3. Prototype candidate prefiltering for code-search vector ranking and compare latency and relevance against current brute-force baseline.
