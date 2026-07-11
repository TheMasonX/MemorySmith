# Search and Chat

MemorySmith has three search modes and one chat surface that can use those modes as context.

## Search Modes

| Mode | Best for | Behavior |
|---|---|---|
| Lexical | Exact terms, tags, IDs, source words | Uses Lucene-style tokenization and weighted title/tag/reference/content scoring. |
| Semantic | Conceptual matches | Uses ONNX embeddings when local model assets are present, otherwise falls back to token/tag/title/reference/alias scoring. |
| Hybrid | General discovery | Combines lexical and semantic ranks with Reciprocal Rank Fusion. |

Hybrid is the best default when the user is exploring. Lexical is best when the exact phrase or ID matters. Semantic is best when the wording may differ from the records.

## ONNX Embeddings

Semantic embedding search is optional. Fresh clones work without model binaries because the app falls back to local scoring.

To enable ONNX ranking, place a compatible model and WordPiece vocabulary under `Data/Models` and keep these settings relative to the data deployment folder:

```json
{
  "EmbeddingsEnabled": true,
  "ModelPath": "Models/embedding-model.onnx",
  "VocabularyPath": "Models/vocab.txt"
}
```

Check `/health` after restart to confirm whether the provider is active or falling back.

## MCP Tools

The MCP endpoint is available at `/mcp` and exposes local tools over the wiki. The most useful tools are:

| Tool | Use it when |
|---|---|
| `memorysmith_hybrid_search` | You need balanced lexical+semantic discovery. |
| `memorysmith_context_pack` | You want root records plus references, conflicts, and backlinks. |
| `memorysmith_get` | You know the exact memory ID. |
| `memorysmith_source_bundle` | You need source-linked file slices with the memory records. |
| `memorysmith_find_by_source` | You want records tied to a file path or source-link pattern. |
| `memorysmith_code_search` | You need code-level search across the indexed codebase. |
| `memorysmith_page_search` | You need markdown page content. |

> **Note:** `memorysmith_search` (standalone lexical) and `memorysmith_semantic_search` (standalone semantic) were consolidated into `memorysmith_hybrid_search` (TSK-0271). Use `memorysmith_hybrid_search` for all discovery needs; use `memorysmith_get` when you know the exact ID.

Use `context_pack` before `source_bundle` when researching code changes. The context pack tells you which records matter; the source bundle pulls the concrete source evidence for those records.

## Chat Mode

Chat mode answers questions with wiki context. It can use memory search, page search, provider/model selection, attachments, streaming responses, context chips, and local chat history.

The chat provider abstraction currently supports local Ollama and GitHub Copilot-backed chat. The UI can ask providers for available models and remembers the last selected provider/model in browser storage.

## Agent Mode

Agent mode asks the provider for structured actions. It can write memories and pages only when agent writes are enabled. Read-only tool calls are bounded by configured limits for iterations, tool calls per turn, and returned characters.

Use Agent mode when the desired outcome is a wiki update or a multi-step change. Use Chat mode when the desired outcome is explanation, research, or a concise answer.

## Good Search Habits

- Start broad with hybrid search.
- Add tags such as `project-wiki` when you want curated project records.
- Use exact IDs with `memorysmith_get` once a record is known.
- Pull source bundles only after narrowing the record set.
- Check pages as well as memories when the question needs narrative context.