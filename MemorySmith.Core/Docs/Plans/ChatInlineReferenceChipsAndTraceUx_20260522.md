# MemorySmith Chat Inline Reference Chips And Trace UX Design

Date: 2026-05-22
Status: Proposed design
Primary decision level: medium-sized chat UX and rendering change
Overall confidence: 0.91

## 1. Executive Summary

MemorySmith chat already has the hard part: it captures a structured per-turn reference set, stores trace events separately from answer text, persists chat preferences locally, and renders answer Markdown through a shared safe renderer. The missing piece is that the structured references stay trapped in the collapsed References drawer instead of being easy for the model to cite inline.

This design makes the system do more work so the model does less:

1. Add an AI-friendly inline reference resolver that turns simple backticked reference handles or lightweight `msref:` Markdown links into clickable inline chips.
2. Keep the existing References drawer as the fallback source of truth.
3. Make trace auto-follow toggleable and persisted in chat preferences.
4. Replace header-only collapse behavior with explicit expansion controls, including a clickable left gutter/stripe for trace entries and actions for bulk expand/collapse.

The recommendation is to implement inline reference resolution server-side in the chat Markdown rendering path, not as a purely client-side DOM rewrite. That keeps resolution deterministic, testable, and aligned with the existing safe-link sanitizer.

## 2. User Problem And Requirements

The user request is precise:

- inline source/reference chips should be available directly inside answer text;
- the system should try to auto-resolve a simple backticked reference name against the references already included with the turn;
- if auto-resolution is too brittle, the system should support lightweight link formatting instead of forcing the model to generate full URLs;
- the design must optimize for AI ease first;
- trace auto-scroll must be toggleable;
- collapsible sections need an affordance other than clicking only the header.

The governing principle for this design is therefore:

> The chat surface should ask the model for the smallest possible citation effort, then do the heavier normalization, resolution, and UI work on the host side.

## 3. Current State Ground Truth

### 3.1 Turn content and references are split across separate render paths

`MemorySmith.App/Components/Pages/Chat.razor` renders answer text via:

```csharp
<div class="chat-message-body chat-message-markdown">@((MarkupString)ChatMarkdownRenderer.RenderHtml(turn.Content))</div>
```

The same turn renders references separately in a collapsed drawer using `turn.Context`, `turn.WrittenMemories`, and `turn.WrittenPages`.

Implication: the answer renderer has no current turn-aware reference resolution step, even though the turn already has the structured data needed to build one.

### 3.2 The Markdown renderer is safe and shared, but currently stateless

`MemorySmith.App/Services/ChatMarkdownRenderer.cs` converts Markdown to HTML through Markdig and sanitizes `href` and `src` attributes. It has no parameter for per-turn reference catalogs.

Implication: this is the best seam for adding deterministic inline reference handling, but it needs a turn-aware overload or companion service.

### 3.3 Trace auto-scroll is unconditional once `_scrollPending` is set

`Chat.razor` currently does this in `OnAfterRenderAsync`:

```csharp
if (_scrollPending)
{
    _scrollPending = false;
    await JS.InvokeVoidAsync("memorySmith.chat.scrollToBottom", _transcriptPane);
    await JS.InvokeVoidAsync("memorySmith.chat.scrollToBottom", _tracePanelList);
}
```

Implication: transcript and trace follow behavior are coupled, and trace cannot be independently paused.

### 3.4 Collapse state is mostly implicit DOM state, not Blazor state

The page relies on `<details>` and `<summary>` for Thinking, Trace, References, and trace-entry sections. There are no toggle callbacks that keep user expansion choices synchronized into component state.

Implication: the current UI is cheap to render but brittle for richer affordances. It also makes alternate toggles awkward because the real source of truth is the DOM element, not the chat state model.

### 3.5 Local chat preference persistence already exists

`ChatPreferencesState` already stores provider, model, mode, Mermaid theme mode, and active session id in browser storage under `memorysmith.chat.preferences.v1`.

Implication: a persisted trace-follow preference is a straightforward extension, not a new subsystem.

### 3.6 Existing tests cover chat tool loops and trace event generation

`MemorySmith.Tests/PagesAndChatTests.cs` already covers tool-call interception, streaming trace events, thinking capture, and bounded context behavior.

Implication: inline references, trace-follow preference, and expanded-state behavior can be added as focused extensions to the existing chat test surface rather than requiring a new test architecture.

## 4. Goals

- Let the model cite current-turn references inline with minimal syntax.
- Resolve citations only against references already attached to the turn, not against the entire global wiki.
- Fail soft: unresolved citations must not break the answer.
- Preserve safe Markdown rendering and current link sanitization behavior.
- Add a user-visible trace follow toggle that persists.
- Add explicit, accessible expand/collapse controls that are not limited to the current header click target.
- Preserve the current References drawer as a fallback and audit surface.

## 5. Non-Goals

- Free-text auto-linking of arbitrary prose mentions across the entire answer.
- Global wiki lookup during render time.
- Replacing the References drawer.
- Full transcript auto-scroll redesign.
- Cross-session persistence of every individual trace-entry expansion state.

## 6. Proposed Design

### 6.1 Add a per-turn reference catalog

Build a lightweight catalog from the turn state before rendering the assistant message body.

Suggested shape:

```csharp
public sealed record ChatReferenceCatalogItem(
    string Key,
    string Kind,
    string Id,
    string Title,
    string Href,
    string Origin,
    string Snippet,
    IReadOnlyList<string> Aliases,
    int Ordinal);
```

Catalog source inputs:

- `turn.Context`
- `turn.WrittenMemories`
- `turn.WrittenPages`

Canonical keys:

- `memory:<id>`
- `page:<slug>`

Derived aliases:

- exact ID or slug;
- normalized title;
- optional short normalized handle when it is unique within the turn;
- ordinal form for collisions, for example `council#2`.

Important constraint: resolution is limited to the current turn's references. That keeps the model mental model simple and avoids render-time surprises.

### 6.2 Support two AI-friendly citation forms

#### Form A: auto-resolved backticked handles

Examples:

- `memory:project-wiki-chat-streaming-thinking`
- `page:search-and-chat`
- `Council Workflow#2`

Behavior:

- if the backticked text resolves uniquely in the current turn catalog, render it as an inline chip anchor;
- if it is ambiguous without an ordinal suffix, leave it as code text and optionally emit an internal warning trace event;
- if it is unknown, leave it as code text.

Why this is the primary syntax:

- models already naturally emit inline code spans for identifiers;
- the model does not need to know route formats;
- the host handles normalization and alias matching.

#### Form B: lightweight synthetic Markdown links

Examples:

- `[Council workflow](msref:page:search-and-chat)`
- `[streaming thinking](msref:memory:project-wiki-chat-streaming-thinking)`
- `[council review](msref:council#2)`

Behavior:

- `msref:` is resolved against the same per-turn catalog;
- once resolved, it becomes the real relative app link;
- unresolved `msref:` links degrade to safe inert text or `#` after sanitization.

Why this exists:

- it gives the model a second path when it wants human-friendly link text;
- it is far easier than requiring fully formed `/pages/...` or `/api/memories/...` URLs.

### 6.3 Prefer server-side resolution in the Markdown renderer

Recommended implementation direction:

1. Introduce a small `ChatReferenceCatalogBuilder` service that derives the per-turn catalog.
2. Extend the chat render path to call a new overload such as:

```csharp
ChatMarkdownRenderer.RenderHtml(turn.Content, referenceCatalog)
```

1. Add a Markdig extension or equivalent Markdown AST pass that:
   - inspects `CodeInline` nodes for backticked handles;
   - inspects `LinkInline` nodes for `msref:` targets;
   - rewrites matched nodes to real link/chip HTML before the existing sanitizer runs.

Why server-side is preferred over a DOM rewrite:

- deterministic output in storage and tests;
- easier unit coverage;
- avoids JS timing races with streamed updates and Mermaid/Prism post-processing;
- keeps safety in the same renderer that already sanitizes links.

Fallback if the Markdig extension is too expensive for the first slice:

- implement only `msref:` synthetic links first;
- add backticked-handle resolution in the next slice.

That fallback still respects the AI-first goal because `msref:` is lighter for the model than full URLs.

### 6.4 Expose canonical handles to the model in existing context blocks

The host should not assume the model will guess the best alias.

Recommendation:

- when building the provider-visible local context and tool-result blocks, append a compact citation helper section;
- for each included context item, include at least one canonical handle:

```text
Citation handles for inline references:
- memory:project-wiki-chat-streaming-thinking | Chat Streaming and Thinking Blocks
- page:search-and-chat | Search and Chat
```

This is low-token overhead because the referenced items already exist in the turn. It removes guesswork and directly supports the principle that the system should work harder than the model.

### 6.5 Render inline references as chips, not plain links

Add a dedicated chat chip style for resolved inline references.

Suggested classes:

- `.chat-inline-ref`
- `.chat-inline-ref.is-memory`
- `.chat-inline-ref.is-page`
- `.chat-inline-ref.is-tool`
- `.chat-inline-ref.is-preloaded`
- `.chat-inline-ref.is-write`

Suggested behavior:

- link opens the real MemorySmith page or memory route;
- hover shows `Snippet` in a tooltip or title attribute;
- chip label prefers the human title while preserving the canonical target in a tooltip;
- color tone reflects origin the same way the drawer already distinguishes neutral preloaded, blue tool, and green write resources.

The References drawer stays in place and continues to show the complete per-turn reference set even if no inline citations were used.

### 6.6 Add a persisted trace auto-follow toggle

Add a new preference field:

```csharp
public bool TraceAutoScroll { get; set; } = true;
```

Recommended UI placement:

- Trace panel header or filter row, labeled `Follow trace`.

Recommended behavior:

- transcript auto-scroll remains unchanged for now;
- trace auto-scroll occurs only when:
  - `_scrollPending` is true,
  - the trace panel list is present,
  - `TraceAutoScroll` is true.

Recommended supporting affordance:

- add a `Latest` button in the Trace panel header that performs a one-shot jump to bottom when follow is off.

This solves the user complaint directly without expanding scope into a full transcript-follow redesign.

### 6.7 Replace header-only expanders with explicit stateful controls

The current `<details>` approach is compact but too limited for richer controls.

Recommended direction:

1. Keep top-level disclosure semantics where convenient, but move actual expansion state into Blazor data.
2. Add explicit toggle buttons for:
   - Thinking section
   - per-turn Trace drawer
   - per-turn References drawer
   - each trace entry in the trace panel and transcript
3. Make the left trace color stripe or left gutter an actual clickable button target instead of only a CSS border.
4. Add an overflow menu or toolbar actions for `Expand all`, `Collapse all`, and `Expand errors` in the Trace panel.

Suggested state additions:

```csharp
ChatTurnState.ThinkingOpen
ChatTurnState.TraceDrawerOpen
ChatTurnState.ReferencesOpen
ChatTraceEntryState.IsExpanded
```

Why this is worth the extra code:

- alternate click targets become first-class and accessible;
- expansion state no longer resets unpredictably on rerender;
- bulk actions become trivial;
- the left gutter can genuinely count as an expander trigger instead of only a visual stripe.

### 6.8 Accessibility requirements

Every expander must be keyboard reachable and expose `aria-expanded`.

Minimum requirements:

- real `button` elements for expander controls;
- visible focus ring on the gutter/chevron control;
- summary text remains readable when chips wrap;
- trace follow toggle is screen-reader labeled.

## 7. Data And Persistence Impact

### 7.1 Browser storage

`memorysmith.chat.preferences.v1` can stay on the same key if the new property is optional and defaults to `true` when absent.

Suggested additive change:

```json
{
  "provider": "Ollama",
  "model": "gemma4:latest",
  "mode": "Chat",
  "mermaidThemeMode": "auto",
  "activeSessionId": "...",
  "traceAutoScroll": true
}
```

There is no need to persist per-entry expansion state across reloads in the first slice.

### 7.2 No API contract change required

This design does not require changes to `/api/chat`, `/mcp`, or provider contracts for the first implementation slice. The work lives in:

- `Chat.razor`
- `ChatMarkdownRenderer.cs`
- `ChatServices.cs` or a nearby chat rendering helper
- `memorysmith.js`
- `app.css`
- prompt content in `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md`

## 8. Validation Plan

### 8.1 Automated tests

Add targeted coverage for:

1. `ChatMarkdownRenderer` resolves `memory:<id>` and `page:<slug>` handles into safe relative links.
2. `msref:` links resolve correctly and still pass through link sanitization.
3. ambiguous aliases require an ordinal suffix and otherwise remain unresolved.
4. unresolved backticked handles remain literal code text.
5. `ChatPreferencesState` round-trips `TraceAutoScroll` with absent-field fallback to `true`.
6. trace auto-follow only calls the trace scroll helper when the new preference is enabled.
7. expansion state survives rerenders for trace entries once explicit state replaces raw DOM-only `<details>` behavior.

Existing likely homes:

- `MemorySmith.Tests/PagesAndChatTests.cs`
- a new focused renderer test file if the Markdown extension becomes large enough to justify separation.

### 8.2 Manual visual validation

Validate at desktop, tablet, and 390px mobile widths.

Manual checks:

- inline chips wrap cleanly in dense transcript paragraphs;
- unresolved inline handles remain readable and non-broken;
- trace follow off prevents scroll jumping while events continue arriving;
- `Latest` jumps correctly;
- left gutter expander works with mouse and keyboard;
- `Expand all` and `Collapse all` act only on the selected turn's visible trace entries.

The existing repo note about chat visual validation already calls out 390px as the width that exposes stacked-layout issues most reliably.

## 9. Rollout Plan

### Slice 1: Inline reference foundations

- build the per-turn reference catalog;
- add `msref:` support;
- update the prompt with canonical citation handle guidance;
- keep the drawer unchanged.

### Slice 2: Backticked handle auto-resolution and chip styling

- add code-span resolution;
- add chip styles and tooltips;
- add renderer tests for alias collisions and unresolved fallbacks.

### Slice 3: Trace follow and explicit expanders

- add `TraceAutoScroll` preference and UI toggle;
- add `Latest` button;
- convert trace entry expand/collapse to explicit stateful controls;
- add bulk expand/collapse actions.

This sequence preserves value if implementation stops early. Slice 1 alone already makes inline citations much easier for the model than the current drawer-only design.

## 10. Risks And Mitigations

| Risk | Why it matters | Mitigation |
| --- | --- | --- |
| False-positive auto-linking | Common backticked words could accidentally resolve | Restrict auto-resolution to exact normalized matches against the current turn catalog only |
| Alias collisions | Titles can normalize to the same handle | Require explicit ordinal suffix like `#2` for ambiguous handles |
| Unsafe synthetic links | `msref:` targets must not bypass sanitizer | Resolve to normal relative paths before the existing safe-link pass |
| Rerender state drift | DOM-only `<details>` state is not robust for richer controls | Move expansion state into Blazor data |
| Prompt token creep | Citation helper text adds tokens | Emit only canonical handles for included references, not large alias lists |

## 11. Open Questions

1. Should unresolved inline handles add a visible subtle warning chip in the transcript, or should they only remain literal text?
2. Should canonical citation handles also be shown in the References drawer so users can see the exact AI-friendly forms?
3. Should `TraceAutoScroll` remain trace-only, or should transcript follow also become independently configurable in a later pass?
4. Should clicking an inline chip navigate in-place or open a new tab for pages and memories by default?

## 12. Recommendation

Proceed with Slice 1 and Slice 3 planning immediately, with the renderer-based reference resolver as the default design choice.

Reasoning:

- it directly addresses the user request to make citation easier for the model;
- it uses existing structured turn context instead of inventing a new citation store;
- it avoids broad provider/API churn;
- it fixes a real UX problem in trace-follow and collapse controls without changing chat semantics.

Confidence: 91%. The key uncertainty is implementation effort for the Markdig extension versus a simpler first-pass `msref:` resolver, not the product direction itself.
