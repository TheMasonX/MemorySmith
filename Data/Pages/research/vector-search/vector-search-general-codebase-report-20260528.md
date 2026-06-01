# Vector Search General Codebase Report (Research Copy, 2026-05-28)

This page is the research-home copy of the 2026-05-28 vector-search deep audit so vector-search findings live under research pages.

## High-Severity Findings

1. Semantic ranking currently scales linearly with corpus size because all filtered records are ranked in-process for each semantic request.
2. Code-search vector scoring still performs brute-force in-memory scans over chunk embeddings.
3. `nomic-embed-code` compatibility is not a simple model export task because current runtime defaults assume WordPiece-style assets.

## Medium-Severity Findings

1. Embedding execution remains synchronous in request flow for semantic paths.
2. Per-document persistent lock map can grow without bound in long-lived high-churn scenarios.
3. Silent fallback behavior can hide embedding failures from default response payloads.
4. Local model export workflow previously depended on ad hoc environment setup.
5. Python minor-version wheel support remains a practical export risk (`torch`/`optimum`).

## Implemented Follow-Up (2026-05-28)

- Added reproducible model export workflow tooling:
  - `Scripts/Install-CodeSearchModel.ps1`
  - `Scripts/model-tools/export_hf_embedding_model.py`
  - `Scripts/model-tools/requirements-model-export.txt`
- Added explicit compatibility and workflow notes to research and model-manifest flow.

## Current Recommendation

1. Keep current E5-based production path as default while validating alternatives with strict A/B benchmarks.
2. Treat tokenizer/runtime compatibility as a first-class gate before switching embedding families.
3. Land candidate-reduction and indexing improvements before larger ANN migration scope.

## Canonical Source

- Original audit copy: `Data/Pages/audits/vector-search-general-codebase-report_5-28-26.md`
