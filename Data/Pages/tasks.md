# Future Tasks

This page tracks user-facing work in plain language. Completed items stay here as a lightweight product history; open items should describe the outcome a person would notice, not only the internal implementation detail.

## Current Priorities

- [ ] CRITICAL: I should not be able to view the admin page, let alone change roles, without admin access, and especially not signed in. This is a massive oversight and needs to be fixed immediately.
- [ ] Add browser-level smoke coverage for the main app routes: `/memories`, `/pages`, `/chat`, and `/health`.
- [ ] Add schema or fixture validation for the live `Data/Memories` wiki so bad records are caught before runtime.
- [ ] Keep reducing large UI/service files where extraction makes behavior easier to review.
- [ ] Add a short release checklist for Windows Service deployment and local model asset verification.
- [ ] Improve static Pages publishing with richer navigation once the first GitHub Pages workflow is proven in CI.
- [ ] Agent driven page generation - combined feature with chat that leverages chat interface and adds a preview pane
- [ ] Grid panels like the markdown/preview plane and the pages/edit columns should be resizable to a degree, like a classic gridsplitter
- [ ] admin/ page should allow the user to edit settings and not just view them (as appropriate)

## Pages
- [x] Page preview mode - live update or at least periodic refresh (toggleable) and a manual refresh button
- [x] Editor tools bar to create a table, link, add checkboxes (`- [ ]`), make bold, italics, etc.
- [x] Monaco editor? Reviewed; retained the local fill-height markdown editor to avoid a remote editor dependency while adding toolbar, preview, and dirty-state support.
- [x] Unsaved changes notice if leaving a page in edit mode
- [x] Editor has unused space at the bottom - should fill down
- [x] Image embed toolbar option to upload page images into `Data/Pages/assets` and insert markdown image links
- [x] Add human-readable wiki pages for architecture, operations, and search/chat behavior

## Chat
- [x] Chat model configuration (provider + model name) - would be nice if you can query the provider for which models are available
- [x] Enter to send with Shift+Enter to add a new line - a toggle button next to send `Send on Enter` to disable this.
- [x] Autoscroll to the bottom of chat
- [x] System prompt for the wiki Chat/Agent, saved in MemorySmith.Core\Docs\Prompts\wiki-chat-agent.md
- [x] Chat agent status display
- [x] Attach files option
- [x] Chat history (default is a new chat when you open the page with a collapsable sidebar)
- [x] Chat/Agent buttons should show state (like toggle buttons) and not use a separate readout
- [x] Increase the screen realestate used by the chat output window - compact chat style too
- [x] Resources in the chat window should be clickable (new tab) - snippet hover would be really neat, but idk if possible
- [x] Fully collapse chat history instead of leaving a narrow rail
- [x] Fix Enter-to-send so it sends the current textarea value immediately and clears the composer
- [x] Paste clipboard images as chat attachments
- [x] Retain unsent draft text and queued attachments when switching chats, with a leave-page warning
- [x] Show pending response feedback and collapsible thinking content when available
- [x] Send text attachments as bounded context and image attachments as Ollama image payloads for vision-capable models
- [x] Stream live chat responses with an elapsed timer and per-response duration
- [x] Persist the last used provider/model and restore active chat history across page navigation
- [x] Delete chats from history with a confirmation prompt
- [x] Add GitHub Copilot as a selectable provider using GitHub CLI auth or token env vars, with preferred mini model defaults
- [x] Add shared chat tool catalog and deterministic intent intercepts for page/unified wiki retrieval
- [x] Keep chat pre-context small for simple prompts and show mid-chat accessed resources as separate blue chips
- [x] Add a first-class trace drawer for interleaved reasoning and tool call/result events per assistant turn
- [x] Add a shared chat sidebar with History and Trace tabs, collapsible trace headers, responsive small-viewport layout, filters, compact execution graph, tool latency/token metadata, editable tool rerun, icon Finish Step/Stop controls, and per-action Agent write approval/rejection
- [x] Render chat transcript messages as safe Markdown and update the shared/Athena prompts to request Markdown answers

## Health
- [x] Make the health page scrollable inside the fixed app shell
- [x] Show semantic search provider status clearly enough to catch missing local ONNX assets

## Notes For Future Edits

- Prefer pages for readable explanations and runbooks.
- Prefer structured memories for searchable facts with tags, confidence, references, and source links.
- Keep `Data/Memories` stable; tests copy it to temp storage before mutation.