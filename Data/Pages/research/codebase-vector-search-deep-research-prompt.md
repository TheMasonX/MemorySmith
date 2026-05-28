# Deep Research Prompt: MemorySmith Codebase Vector Search And Embedding Stack

Use this prompt in Microsoft Copilot, ChatGPT Deep Research, or a comparable research mode. The goal is to gather external evidence and implementation patterns that can improve MemorySmith's current codebase vector-search and embedding pipeline without drifting into cloud-first assumptions or architecture churn that the local-first product does not need.

## Prompt

```text
You are performing deep technical research for MemorySmith, a local-first ASP.NET Core and Blazor application that stores structured memories, markdown wiki pages, and a SQLite-backed code-search vector index.

Your job is to produce an evidence-backed research report on how to improve the current codebase vector embedding process, search stack, indexing pipeline, hardware-acceleration strategy, and operator experience.

MemorySmith implementation context:
- The app is a single-host ASP.NET Core process with local file-backed content and local SQLite data.
- Code search indexes the MemorySmith repo into a SQLite database at Data/Graph/code-search/code-search.db.
- The current indexer chunks source files, embeds chunks through ONNX Runtime, and serves search through MCP/chat tooling.
- Search and indexing live primarily in CodeSearchService.
- Semantic embeddings live primarily in SemanticEmbeddingSearchService via an ONNX text embedding provider.
- The provider supports CPU, CUDA, and OpenVINO configurations, with CPU fallback when hardware acceleration is unavailable.
- Startup prewarm is enabled by default to reduce first-request ONNX initialization cost.
- The system already has warm incremental rebuild reuse, progress/status reporting, timing breakdowns, and exact-query caching.
- A batch document-embedding path exists, but the current live repo benchmark showed that scalar document embedding (effective batch size 1) still produced the best end-to-end rebuild time on the current corpus.
- Current measured evidence on the active maintainer machine:
  - Live CUDA forced rebuild sweep on the same repo/corpus:
    - batch size 1: 116,992 ms
    - batch size 2: 125,565 ms
    - batch size 4: 140,630 ms
    - batch size 8: 147,988 ms
    - batch size 16: 159,699 ms
  - Provider-only CUDA micro-benchmark was still faster in batch mode:
    - scalar median: 447 ms
    - batch median: 307 ms
  - Live query probes were about 18-20 ms, so query-time chunk loading is not the main bottleneck on the current workload.

Research objective:
Produce a whitepaper-grade report on how MemorySmith should improve its codebase vector search and embedding stack. Separate improvements that are broadly supported by external evidence from changes that require local benchmarks or MemorySmith-specific product judgment.

Questions to research:

1. End-to-end indexing bottlenecks
- In local code-search or documentation-RAG systems, what usually dominates rebuild time once SQLite writes and query latency are already acceptable?
- How often do batching wins in model micro-benchmarks disappear in the real indexing pipeline because of tokenization, padding waste, tensor assembly, file I/O, chunk-shape variance, or database coordination?
- What measurement practices best isolate provider initialization, tokenization, batching overhead, embedding time, persistence time, and total wall-clock time?

2. Batching strategy design
- What batching strategies work best for heterogeneous code/document corpora where chunk sizes vary widely?
- Compare fixed batch sizes, token-budget batching, dynamic batching by similar sequence length, and adaptive runtime batch tuning.
- What heuristics or algorithms best reduce padding waste and GPU underutilization for transformer embeddings?
- Under what conditions should a system keep a batch implementation available but default to scalar or near-scalar operation?

3. ONNX Runtime acceleration on developer workstations and local servers
- What are the best current practices for using ONNX Runtime with CPU, CUDA, and OpenVINO for embedding workloads in local apps?
- What failure modes are common for CUDA and OpenVINO on developer-operated Windows or Linux hosts?
- What configuration, fallback, packaging, and diagnostics patterns are recommended so hardware acceleration remains optional instead of fragile?
- Are there provider-specific ONNX session options, graph optimizations, memory arena settings, or I/O binding patterns that materially improve embedding throughput for local applications?

4. Tokenization and embedding-pipeline overhead
- How expensive is local tokenization relative to model inference in typical embedding pipelines?
- What optimizations exist for tokenizer reuse, pooling implementation, tensor construction, pinned buffers, memory pooling, vectorized attention-mask assembly, or avoiding repeated allocations?
- What techniques are used in production-grade local embedding systems to reduce per-call overhead when the model itself is not the only bottleneck?

5. Chunking strategy for code search
- What code chunking approaches produce the best trade-off between retrieval quality, indexing cost, and explainability?
- Compare fixed-size chunks, syntax-aware chunks, symbol-aware chunks, and heading or section-preserving chunks for code and mixed markdown/code corpora.
- What metadata should be stored per chunk to improve later filtering, relevance, or citation fidelity?
- When is it worth reusing unchanged chunk embeddings at a more granular level than document-level reuse?

6. SQLite-backed vector-search persistence
- When is SQLite a durable enough vector/index store for local developer tooling, and when does it become the wrong persistence layer?
- What indexing, schema, serialization, or update patterns help SQLite-backed embedding stores remain fast and reliable for incremental rebuilds?
- What are the best practices for warm metadata reuse, exact-query caching, chunk-row replacement, and crash-safe rebuild bookkeeping in a local-first app?

7. Retrieval quality and evaluation methodology
- What benchmarks and evaluation metrics are recommended for local code search and embedding-backed developer tooling?
- Compare latency, throughput, MRR, recall@k, nDCG, hit rate on known questions, citation quality, operator trust, and warm/cold startup behavior.
- What experiment design helps distinguish a real user-visible win from a micro-benchmark artifact?

8. Operator-visible status and reliability
- What progress reporting, status APIs, logs, and UI affordances are most useful during long-running embedding imports or index rebuilds?
- How should local tools surface partial failures, skipped files, reuse counts, warm/cold state, fallback activation, and rebuild timing so users can trust the process?
- What anti-patterns make vector indexing feel opaque or unreliable even when it technically works?

9. Query-path optimization priorities
- If query latency is already around 20 ms on the current workload, what evidence supports leaving the query path alone versus preloading chunks, adding ANN structures, or caching more aggressively?
- What are the risks of prematurely complicating the query path when rebuild/import cost is the real bottleneck?

10. Future-facing architecture decisions
- Under what conditions should a local-first code-search system stay on SQLite plus exact scan/rerank, and under what conditions should it move to ANN indexes, external vector databases, or specialized retrieval engines?
- What migration triggers should MemorySmith watch for: corpus size, chunk count, rebuild duration, query latency, memory use, hardware mix, or evaluation regressions?

Deliverables:
- Executive summary with the top 10 findings.
- A prioritized recommendation list for MemorySmith, ordered by ROI and implementation risk.
- A clear separation between:
  - changes justified now by evidence,
  - changes that need targeted local benchmarks,
  - changes that are probably unnecessary complexity.
- A measurement framework for future experiments, including which metrics must be recorded for cold builds, warm builds, incremental rebuilds, and query probes.
- Concrete examples from real systems, papers, documentation, or open-source implementations, with URLs.
- A section on likely reasons why provider-only batch micro-benchmarks can beat scalar inference while full index rebuilds still regress.
- A decision memo answering this question directly: what should MemorySmith do next to improve end-to-end indexing throughput and retrieval quality without compromising local-first reliability?

Constraints for your answer:
- Do not assume a cloud service, SaaS tenancy, distributed retrieval tier, or hosted vector database is required.
- Prefer local-first, inspectable, offline-capable designs unless strong evidence shows a more complex design is necessary.
- Treat CPU fallback as a hard reliability requirement, not an optional nicety.
- Distinguish recommendations for large cloud-scale corpora from recommendations that actually make sense for a local developer tool.
- Cite sources with URLs and explain why each source is relevant.
- If evidence is weak or conflicting, say so directly.
```

## How To Use The Results

Use the returned research to challenge the current implementation, not to rubber-stamp it. A useful follow-up pass should separate:

- findings that justify immediate code changes;
- findings that justify benchmark-only experiments;
- findings that are interesting but not yet worth the complexity for MemorySmith;
- findings that contradict current assumptions and need explicit local validation.

Pair any proposed implementation work with the current benchmark evidence recorded in [Codebase Vector Search Whitepaper Notes](codebase-vector-search-whitepaper.md) and the hardware setup guidance in [Semantic Acceleration Setup Guide](../guides/semantic-acceleration-setup.md).
