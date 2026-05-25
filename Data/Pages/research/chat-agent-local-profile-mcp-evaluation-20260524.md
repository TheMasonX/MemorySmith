# Chat Agent Local Profile MCP Evaluation - 2026-05-24

## Scope

- Live chat page at `/chat` with the local profile selected in Agent mode.
- Read-only MCP/wiki tool behavior as surfaced through the app chat stack.
- Current chat harness notes and task-tracking surfaces that should carry the follow-up work.

## Evidence Reviewed

- Live browser probe at `http://localhost:5089/chat`.
- Chat trace events from the same turn.
- `Data/Pages/chat/chat-harness-deepdive-results.md`.
- `Data/Pages/workbench/tasks.md`.
- `memorysmith-chat-tool-catalog-169-tests.md`.
- `memorysmith-chat-context-planner-249-tests.md`.

## Findings

| ID | Severity | Confidence | Finding | Evidence |
| --- | --- | ---: | --- | --- |
| F-001 | High | 93% | The local Agent-mode turn accepted a generic wiki/tool-search request, but it stalled in `Thinking` with no tool call or final answer after more than 20 seconds. | `/chat` trace, `23.0s elapsed`, `Waiting for first token...` |
| F-002 | Medium | 91% | The context planner is doing the right prework by preloading the relevant memories and page, but the model/tool contract still needs a simpler completion path for generic search prompts. | Trace event: `Context planner` recommended `memorysmith_context_pack`; preload contained 2 memories and 1 page. |
| F-003 | Medium | 88% | Historical GitHub provider failures remain visible in chat history, so users can see prior model availability problems, but the UI does not clearly distinguish those failures from the current local-agent pending state. | Earlier transcript entries show `gpt-4.1-mini`, `gpt-4o`, and `gpt-4.1` availability/rate-limit failures. |
| F-004 | Low | 84% | The MCP context-pack diagnostics were useful but noisy, with unresolved source-link warnings and plain-tag info messages that should be summarized more explicitly for agent consumption. | Context-pack results for the report query. |
| F-005 | Medium | 90% | A citation-only request like "Cite the source file for the chat tool catalog" did not trigger the wiki preload path, so the local profile started from an empty context instead of a preloaded one. | Second live probe at `/chat`; trace showed `No strong MemorySmith/wiki evidence intent was detected` and `Preload memories: 0 Preload pages: 0`. |
| F-006 | Low | 92% | An explicit search prompt eventually completed with the expected source link and the UI updated the assistant response correctly, so the main residual issue is latency and cold-start preload selection rather than a stuck write-state refresh bug. | Third live probe at `/chat`; final response arrived after about 13.2s and rendered `Source: [Chat Agent Provider Architecture](memory:project-wiki-chat-agent-provider)`. |
| F-007 | Medium | 88% | A multi-link prompt asking for the top two source links stayed in reasoning for 19s without a final answer, then continued expanding its rationale instead of closing promptly. | Fourth live probe at `/chat`; the assistant remained pending with 3+ trace events and had not produced a finished answer by 19s. A later continuation also surfaced `project-wiki-source-links-feature` as a second candidate source but still did not close promptly. |
| F-008 | Low | 95% | The assistant response body did update in-place before the turn was finished, so the suspected stale-response bug was not reproduced; the only rendering defect observed in that final answer was a duplicated approval disclaimer paragraph. | Fifth live probe at `/chat`; the rendered response changed while the turn was still pending, then finished normally with two repeated disclaimer paragraphs. |
| F-009 | Medium | 94% | The duplicated approval disclaimer is reproducible on another multi-source prompt family, so it is not a one-off artifact of the earlier answer shape. | Sixth live probe at `/chat`; the bullet-point response finished normally but still rendered the approval disclaimer twice. |
| F-010 | Low | 90% | The stricter two-bullet source-link prompt completed without the duplicated approval disclaimer, so the disclaimer bug appears prompt-family-specific rather than universal. | Seventh live probe at `/chat`; the response streamed normally and ended with a single source-link answer body. |
| F-011 | Medium | 93% | MCP retrieval and chat-agent retrieval align on the most relevant source, but chat still intermittently duplicates the structured-write disclaimer on otherwise read-only answers, creating a parity gap in response quality. | Eighth probe: direct MCP unified search and chat-agent intercept both selected `project-wiki-chat-agent-provider`; chat response still rendered duplicate disclaimer paragraphs. |
| F-012 | Medium | 94% | The MCP/chat parity gap persists on a second topic: source selection still matches while chat intermittently duplicates the structured-write disclaimer on read-only output. | Ninth probe: MCP and chat both selected `project-wiki-source-links-feature` for the source-links query, but chat rendered duplicate disclaimer paragraphs. |
| F-013 | High | 95% | A read-only source query can finish with disclaimer-only output and omit the requested answer body entirely, which is a higher-severity wrapper failure than duplication. | Tenth probe: query asked for one best source for chat tool catalog provider architecture; turn finished in ~15.2s with only the structured-write disclaimer and `References (3)`, no source sentence. |
| F-014 | Medium | 94% | A constrained two-source prompt can return the correct pair of sources and still append the structured-write disclaimer twice, confirming parity in source selection but persistent wrapper duplication under successful answer generation. | Eleventh probe: MCP baseline and chat output both included `project-wiki-chat-agent-provider` and `project-wiki-source-links-feature`; chat completed in ~11.5s and still rendered duplicate disclaimer paragraphs. |
| F-015 | Medium | 95% | Citation-style one-sentence prompts still reproduce duplicate structured-write disclaimer text even when source parity and answer body are correct. | Twelfth probe: query asked for one-sentence citation of chat tool catalog source; MCP and chat aligned on `project-wiki-chat-agent-provider`, but chat appended the disclaimer twice. |
| F-016 | Low | 88% | Very narrow single-title prompts can complete with a correct answer and only one disclaimer paragraph, reinforcing that duplication and wrapper noise are prompt-shape dependent rather than universal. | Thirteenth probe: query asked for the single best source title for source links feature; chat returned `Source Links Feature` with one disclaimer and `References (2)`. |
| F-017 | Medium | 95% | Output-format instructions change disclaimer cardinality: a one-line title response can remain single-disclaimer while a one-bullet title response on similar intent reproduces duplicate disclaimer text. | Fourteenth probe: one-line chat-tool-catalog title returned `Chat Agent Provider Architecture` with one disclaimer; fifteenth probe: one-bullet source-links title returned `Source Links Feature` with duplicate disclaimer paragraphs. |
| F-018 | High | 96% | Wrapper disclaimer duplication can escalate beyond two copies, reaching three repeated paragraphs under heading-plus-bullet formatting, which indicates uncontrolled disclaimer accumulation rather than a simple duplicate-insert bug. | Seventeenth probe: heading + one bullet prompt for source links feature returned correct title but appended the same disclaimer three times; sixteenth numbered-list probe also showed delayed no-token pending then duplicate disclaimers on completion. |
| F-019 | Medium | 94% | Heading text alone does not trigger disclaimer escalation; accumulation risk appears tied to list-style output wrappers, especially bullet/numbered shapes. | Eighteenth probe (plain sentence) returned `Chat Agent Provider Architecture` with one disclaimer; nineteenth probe (heading-only) returned `Source Links Feature` with one disclaimer, while prior list-shaped probes duplicated or tripled disclaimers. |
| F-020 | Medium | 95% | Duplicate disclaimer behavior reproduces across both numbered and bullet formats on the exact same intent, indicating format-driven wrapper instability independent of retrieval topic. | Twentieth and twenty-first probes both targeted chat-tool-catalog title output; numbered and bullet variants each returned `Chat Agent Provider Architecture` and appended the disclaimer twice. |
| F-021 | High | 96% | Disclaimer cardinality is nondeterministic for identical prompt/input conditions: the same bullet-format prompt can produce either double or triple disclaimer injection across consecutive runs. | Twenty-second and twenty-third probes used the exact same chat-tool-catalog bullet prompt back-to-back; first run returned 3 disclaimers, second run returned 2 disclaimers, both with the same source title. |
| F-022 | High | 97% | JSON-shape prompts can deterministically suppress the answer body entirely, finalizing with `References` only and no source title/disclaimer text, which is a severe read-only response assembly failure. | Twenty-fourth and twenty-fifth probes replayed the identical JSON-format prompt for chat-tool-catalog source title; both runs ended with `References (3)` and no answer body. |

## Recommendations

- TSK-0091 - Make generic wiki search prompts deterministically reach the read-only search tool path or a simpler tool-call fallback.
- TSK-0092 - Surface provider/model health in the chat header and model picker before an Agent turn starts.
- TSK-0093 - Add stalled-turn detection and recovery affordances when an Agent turn stays in `Thinking` too long.
- TSK-0094 - Add regression coverage for the exact local-profile wiki-search flow and its inline source-reference output.
- TSK-0095 - Broaden the wiki-intent detector so citation-only prompts still preload the relevant MemorySmith context.
- TSK-0096 - Bound multi-source responses so "top two links" style prompts close with a concise answer instead of prolonged reasoning.
- TSK-0097 - Deduplicate repeated approval disclaimer text in chat responses when the agent emits the same structured-write warning more than once.
- TSK-0098 - Scope approval-disclaimer duplication to the specific prompt families that reproduce it, keeping the two-bullet source-link format clean.
- TSK-0099 - Enforce MCP/chat parity for read-only retrieval responses so quality wrappers (like approval disclaimers) are deterministic and non-duplicative.
- TSK-0100 - Prevent disclaimer-only finalization on read-only retrieval prompts by requiring a non-empty answer body before structured-write warnings are appended.

## Conclusion

The local profile is closer to usable than the GitHub fallback path because the app can select it and preload the right context. The remaining blocker is that a generic search request can still stall before the model emits a tool call or answer, so the next changes should prioritize deterministic tool routing, clearer health feedback, and a bounded failure mode.