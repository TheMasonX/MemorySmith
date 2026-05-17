# Future Tasks
User defined future tasks

## Pages
- [x] Page preview mode - live update or at least periodic refresh (toggleable) and a manual refresh button
- [x] Editor tools bar to create a table, link, add checkboxes (`- [ ]`), make bold, italics, etc.
- [x] Monaco editor? Reviewed; retained the local fill-height markdown editor to avoid a remote editor dependency while adding toolbar, preview, and dirty-state support.
- [x] Unsaved changes notice if leaving a page in edit mode
- [x] Editor has unused space at the bottom - should fill down
- [x] Image embed toolbar option to upload page images into `Data/Pages/assets` and insert markdown image links

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

## Health
- [x] Make the health page scrollable inside the fixed app shell