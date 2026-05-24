# Search System

MemorySmith has a three-mode search system used by the UI, APIs, MCP tools, and chat retrieval paths.

## Search Modes

| Mode | Best for | Behavior |
| --- | --- | --- |
| Lexical | Exact terms and IDs | Lucene-style token scoring over record fields. |
| Semantic | Concept-level recall | ONNX embedding ranker when local model assets are present, with deterministic fallback scoring when they are not. |
| Hybrid | General discovery | Reciprocal Rank Fusion over lexical and semantic ranks. |

## What It Does

- Finds relevant structured memories quickly.
- Supports tag, status, and result-limit controls.
- Keeps search useful even when embedding assets are unavailable.
- Feeds chat and agent retrieval with bounded evidence.

## Why It Matters

Search quality determines whether the right memory is available at the right time. The same retrieval system serves people, APIs, and agents, which keeps behavior consistent.

## Related Pages

- [Memories Workbench](memories-workbench.md)
- [Chat and Agent](chat-and-agent.md)
- [Search and Chat](../guides/search-and-chat.md)
