# Tool A/B Executive Summary - Explicit Corpus v6 ChatML Launch (2026-06-01)

## Run Status

- A/B run launched:
  - command: `Scripts/eval_tool_ab_spotcheck.py`
  - output target: `Data/Pages/research/training/tool-ab-spotcheck-20260601-explicit-corpus-v6-chatml.data.json`
   - adapter: `D:/temp/memorysmith-training/runs/20260601-142123/adapter`
   - results: `Data/Pages/research/training/tool-ab-spotcheck-20260601-explicit-corpus-v6-chatml.data.json`
   - generatedAtUtc: `2026-06-01T21:08:58.345701+00:00`
   - Current state: completed; final JSON artifact emitted and saved to repo working tree.

## Interim Baseline For Interpretation

Because the explicit-corpus run is still in flight, this executive interpretation uses the latest completed comparable run:

- source: `Data/Pages/research/training/tool-ab-spotcheck-20260601-step01-v6-chatml-batched-rerun-t256.data.json`
- report: `Data/Pages/research/training/tool-ab-spotcheck-20260601-step01-v6-chatml-batched-rerun-t256.md`

## Headline Findings (Latest Comparable Completed Run)

 - Envelope validity improved versus base:
   - base: 0/60
   - tuned: 10/60 (16.7%)
- Expected-tool match did not improve:
  - base: 0/60
  - tuned: 0/60
- Interpretation:
  - The model got better at producing parseable tool-call envelopes.
  - The model is still not selecting canonical MemorySmith tool names required by the benchmark contract.

## Executive Interpretation

1. Contract compliance is now split across two dimensions:
   - Syntax/format compliance improved.
   - Semantic tool selection remains failing.
2. The largest quality blocker is canonical tool-name alignment, not JSON formatting.
3. Historical regressions are consistent with corpus contamination and alias drift:
   - training examples contained non-canonical aliases (`search`, `open_page`, `fetch_task`, etc.) that do not satisfy benchmark tool-name expectations.

## Decision Guidance

- Do not advance to Step 2 additive data experiments until tool-match passes gate.
- Keep explicit corpus defaults (no implicit transcript or starter ingestion).
- Prioritize corpus curation for canonical tool names before additional training volume.
 
## Final Numbers (this run)

- Cases run: 60
- Envelope valid (tuned): 10/60 (16.7%)
- Tool match (tuned): 0/60 (0.0%)

Note: The tuned model improved envelope validity over base but did not produce canonical tool-name matches required by the benchmark gate.

## Recommended Next Actions

1. Complete current A/B run and update this page with final numbers.
2. Build a canonical-only corpus slice for tool-call responses:
   - allowlist only benchmarked canonical tool names.
   - remove or remap alias tool names before training.
3. Re-run the same 60-case spotcheck and compare deltas on:
   - envelope validity
   - expected-tool match
4. Keep gate unchanged for progression:
   - envelope: 60/60
   - tool-match: >= 42/60

## Notes

- UI launch path now supports explicit corpus configuration and no longer assumes transcript/starter corpus by default.
