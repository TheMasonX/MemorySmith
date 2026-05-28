# Search System

MemorySmith has a three-mode search system used by the UI, APIs, MCP tools, and chat retrieval paths.

## Search Modes

| Mode | Best for | Behavior |
| --- | --- | --- |
| Lexical | Exact terms and IDs | Lucene-style token scoring over record fields. |
| Semantic | Concept-level recall | ONNX embedding ranker when local model assets are present, with deterministic fallback scoring when they are not. |
| Hybrid | General discovery | Reciprocal Rank Fusion over lexical and semantic ranks. |

## Input Contract

MemorySmith search takes plain text plus explicit filters.

| Input | Behavior |
| --- | --- |
| `query` | Plain text tokenized across title, tags, references, and content. There is no fielded query parser in the lexical path. |
| `tags` | Comma-separated, case-insensitive exact-match filter with any-match semantics. Namespaced tags such as `kind:rule` are matched literally. |
| `status` | Optional memory status filter: `Unconsolidated`, `Working`, `Core`, or `Deprecated`. |
| `limit` | Optional bounded result count. |

Unsupported assumptions:

- field targeting such as `title:mcp`
- boolean syntax such as `foo AND bar`
- wildcard or fuzzy syntax such as `auth*` or `auth~`
- date/range syntax

If phrase weighting matters, pass the phrase text itself. Empty query returns recency ordering.

## Output Contract

| Surface | Structured options | Notes |
| --- | --- | --- |
| Memory REST search endpoints | `format=envelope`, `format=json-v2` | Returns `memorysmith.retrieval-results.v1`; default stays backward-compatible. |
| Pages REST list/search | `format=json`, `format=envelope`, `format=json-v2` | Returns `memorysmith.page-results.v1` for structured responses. |
| MCP search tools | `format=json`, `format=envelope` | Search tools default to markdown and switch to structured retrieval payloads when requested. |
| MCP context pack | `format=json` | Returns `memorysmith.context-pack.v1`. |

Score interpretation:

- lexical and semantic scores are comparable only within their own mode and result set
- hybrid score is Reciprocal Rank Fusion, not confidence
- structured envelopes are the right surface for provider metadata, warnings, and diagnostics

## What It Does

- Finds relevant structured memories quickly.
- Supports tag, status, and result-limit controls.
- Exposes structured retrieval envelopes for REST, MCP, and chat-adjacent agent workflows.
- Keeps search useful even when embedding assets are unavailable.
- Feeds chat and agent retrieval with bounded evidence.

## Why It Matters

Search quality determines whether the right memory is available at the right time. The same retrieval system serves people, APIs, and agents, which keeps behavior consistent.

## Related Pages

- [Memories Workbench](memories-workbench.md)
- [Chat and Agent](chat-and-agent.md)
- [Search and Chat](../guides/search-and-chat.md)
- [API and MCP Integration](api-and-mcp.md)
- [Codebase Vector Search Whitepaper Notes](../research/codebase-vector-search-whitepaper.md)
