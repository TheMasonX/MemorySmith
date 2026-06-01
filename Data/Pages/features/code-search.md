# Code Search

MemorySmith includes a dedicated code-search subsystem that indexes the repository codebase and provides semantic + lexical search over code chunks.

## Architecture

The code search pipeline has three stages:

1. **Indexing** — Files are chunked (40-line windows with 8-line overlap by default), each chunk is embedded using the configured ONNX model (E5-base-v2 default), and the chunks + embeddings are stored in a SQLite database at `Data/Graph/code-search/code-search.db`.

2. **Search** — Queries go through a hybrid scoring pipeline: vector similarity (cosine) + lexical token matching, fused with configurable weights (`HybridVectorWeight=0.75`, `HybridLexicalWeight=0.25`). A SQL-based prefilter reduces the candidate set before vector scoring. Results are balanced across documents (`MaxResultsPerDocument=2`).

3. **Presentation** — Results include document path, line range, score, match reason, and a syntax-highlighted snippet. Available via MCP (`memorysmith_code_search`), chat tool, and the `/code-search` Blazor UI.

## Key Features

- **Hybrid scoring** with configurable vector/lexical weights and saturation normalization
- **Document-balanced results** — prevents one file from monopolizing top-K
- **Resumable builds** — crashed builds resume from last checkpoint
- **Shard merging** — external index shards can be merged into the main index
- **Staleness cooldown** — rapid queries don't each trigger rebuild checks
- **Target weighting** — test files and docs are down-weighted for implementation queries
- **Identifier splitting** — camelCase and snake_case query tokens are expanded

## Configuration

All code search settings live under `MemorySmith:CodeSearch` in `appsettings.json`. Key settings:

| Setting | Default | Description |
|---|---|---|
| `Enabled` | `true` | Master switch for code search |
| `EmbeddingBatchSize` | `8` | Chunks per embedding batch (GPU: use 32-128) |
| `VectorCandidatePrefilterEnabled` | `true` | Enable SQL prefilter before vector scoring |
| `HybridVectorWeight` | `0.75` | Weight for vector similarity in hybrid score |
| `HybridLexicalWeight` | `0.25` | Weight for lexical match in hybrid score |
| `MaxResultsPerDocument` | `2` | Max results from a single file |
| `ChunkLineCount` | `40` | Lines per chunk |
| `ChunkOverlapLineCount` | `8` | Overlap between adjacent chunks |

## MCP Tools

| Tool | Description |
|---|---|
| `memorysmith_code_search` | Search indexed code with vector + lexical hybrid |
| `memorysmith_code_search_status` | Get index status and build progress |
| `memorysmith_code_search_merge_shard` | Merge an external index shard (Write permission) |

## Model Setup

Use `Scripts/Install-CodeSearchModel.ps1` to download and prepare embedding models. See the [Code Search Model Export Workflow](../guides/code-search-model-export-workflow.md) guide.

## Future Improvements

- AST-aware chunking via Roslyn (C#) and tree-sitter (other languages)
- Symbol catalog for exact-name navigation and KB linkage
- Cross-encoder reranking stage
- BLOB storage for embeddings (replacing JSON TEXT)
- SIMD-accelerated dot product via TensorPrimitives
