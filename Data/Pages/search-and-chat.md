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
| `memorysmith_search` | You need direct lexical matches. |
| `memorysmith_semantic_search` | You need concept-level recall. |
| `memorysmith_hybrid_search` | You need balanced discovery. |
| `memorysmith_context_pack` | You want root records plus references, conflicts, and backlinks. |
| `memorysmith_get` | You know the exact memory ID. |
| `memorysmith_page_search` | You need markdown page hits by query text. |
| `memorysmith_page_get` | You know the exact page slug and need the page body. |
| `memorysmith_unified_search` | You want one call that searches memories and pages together. |
| `memorysmith_source_bundle` | You need source-linked file slices with the memory records. |
| `memorysmith_find_by_source` | You want records tied to a file path or source-link pattern. |

Use `context_pack` before `source_bundle` when researching code changes. The context pack tells you which records matter; the source bundle pulls the concrete source evidence for those records.

## Chat Mode

Chat mode answers questions with wiki context. It can use memory search, page search, provider/model selection, attachments, streaming responses, safe Markdown-rendered message bodies, context chips, and local chat history.

The chat provider abstraction currently supports local Ollama and GitHub Copilot-backed chat. The UI can ask providers for available models and remembers the last selected provider/model in browser storage.

The shared chat prompt asks providers to use GitHub-flavored Markdown for normal Chat mode answers and gives all chat agents explicit wiki-tool instructions. Agents should use preloaded MemorySmith context first, then request one app-intercepted `toolCalls` JSON object with no prose when more local evidence is needed. The prompt recommends unified search for broad memory/page discovery, hybrid or semantic search for memory retrieval, context packs for reference/backlink depth, and exact get/page-get calls when an ID or slug is known.

The UI renders chat Markdown with raw HTML disabled, Mermaid fenced block support, Prism-compatible code block classes, and unsafe link neutralization before inserting the HTML into the transcript. Mermaid and Prism run client-side after Blazor renders, so streamed chat updates can gain diagrams and syntax highlighting without changing chat storage. Chat skips Mermaid conversion while a response is actively streaming, so unfinished diagrams stay visible as code until the final closed fence is available. The chat toolbar includes a persisted Diagram theme setting with Auto, Light, and Dark modes; rendered diagrams get a matching light or dark background surface so Mermaid line/text contrast stays readable in the dark app shell. The Athena Ollama modelfile carries the same tool and output-formatting instructions.

The chat tool path now uses a shared tool catalog for page and unified search tools in addition to memory search tools. The app can also intercept clearly worded wiki intents before provider generation (for example, "search the wiki for ..." or "open page ...") so common retrieval tasks do not depend entirely on model-formatted tool JSON.

## Chat Context Loading

Chat keeps preloaded context conservative. Exact-reply smoke prompts, simple greetings, and write-only Agent commands do not pull the project wiki into the first provider call. Explicit MemorySmith/wiki/codebase questions get a small bounded pre-context controlled by `Chat:MaxPreloadedContextRecords` and `Chat:MaxPreloadedContextPages`.

When the model needs more evidence, it can request read-only MemorySmith tools mid-turn. Those tool results are fed back into the same provider turn and their touched memory/page resources are shown in the transcript as blue resource chips. Preloaded context chips keep the neutral wiki-chip theme, while Agent-created pages remain green write chips. Per-turn resource chips are tucked into a collapsed References drawer by default so evidence remains available without stretching every answer.

The chat transcript has a first-class Trace tab in the shared right sidebar. The sidebar toggles between History and Trace so the transcript does not need per-turn Trace buttons, and the chat toolbar/composer keep the same compact layout in either tab. The Trace tab includes a turn selector, compact execution graph, interleaved reasoning, preloaded context summaries, deterministic intercepts, model-requested tool calls, tool results, final answer segments, token estimates, and tool latency. Trace entries have collapsible headers so large reasoning or tool output can be tucked away. Filters can show or hide reasoning, tools, answer, system/write events, or errors only.

Tool-call trace entries keep editable JSON arguments and can be rerun from the panel without resending the whole prompt. Rerun results append to the same turn trace and any touched resources are added as blue tool-context chips.

Process note: for chat quality work, test both a no-context prompt such as `Reply exactly: ...` and a retrieval prompt that forces a tool or intercept. The first catches accidental context bloat; the second catches tool-loop and resource-chip regressions.

## Agent Mode

Agent mode asks the provider for structured actions. It can write memories and pages only when agent writes are explicitly enabled; the default is disabled. The chat UI requires explicit approval before applying proposed Agent memory/page writes, and each proposed write can be approved or rejected from the Trace side panel. Read-only tool calls are bounded by configured limits for iterations, tool calls per turn, and returned characters.

During generation, the icon Stop control cancels immediately. The icon Finish Step control requests a softer stop: the current provider/tool step is allowed to finish, then MemorySmith stops before continuing the tool loop.

Use Agent mode when the desired outcome is a wiki update or a multi-step change. Use Chat mode when the desired outcome is explanation, research, or a concise answer.

## Good Search Habits

- Start broad with hybrid search.
- Add tags such as `project-wiki` when you want curated project records.
- Use exact IDs with `memorysmith_get` once a record is known.
- Pull source bundles only after narrowing the record set.
- Check pages as well as memories when the question needs narrative context.