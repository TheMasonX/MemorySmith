# MemorySmith Pages

MemorySmith is a local-first memory workbench. It keeps structured project knowledge in JSON records, longer notes in markdown pages, and exposes both to the UI, API, chat, and MCP tools.

This folder is the human-readable side of the project wiki. Pages here are meant to be easy to skim before changing the codebase, running the app, or asking the chat agent for help.

## Start Here

- [Architecture](architecture.md) explains the single-host app shape and where each project fits.
- [Operations](operations.md) covers running, validating, publishing, and the important data paths.
- [Search and Chat](search-and-chat.md) explains lexical, semantic, hybrid, MCP, chat, and agent behavior.
- [Core Memory System Improvements RFC](temp-plan.md) reviews long-term options for making memories, search, pages, and chat more useful for AI agents and humans.
- [Council Workflow](llm-council.md) describes the multi-perspective review method for major MemorySmith decisions.
- [Deep Research Prompt](memory-system-deep-research-prompt.md) turns the RFC's externally researchable questions into a prompt for Microsoft Copilot or ChatGPT Deep Research.
- [Future Tasks](tasks.md) tracks product work in a checklist format.

## What Lives Where

| Area | Purpose |
|---|---|
| `Data/Memories` | Structured wiki records used by search, MCP, tests, and agent context. |
| `Data/Pages` | Markdown pages for readable notes, runbooks, and planning context. |
| `Data/Pages/assets` | Page images, video, audio, and other files served through `/page-assets`. |
| `Data/Events` | Append-only audit/activity log. |
| `Data/Graph` | Reserved data folder for graph-oriented project knowledge. |
| `Data/Models` | Optional local ONNX embedding model and vocabulary files. |
| `Data/vars.json` | Path variables such as `%MemorySmithRepo%` for source links. |

## Current App Surfaces

| Surface | What it is for |
|---|---|
| `/memories` | Browse, search, create, edit, and maintain structured memory records. |
| `/pages` | Create, search, edit, preview, and render markdown-backed pages from this folder. |
| `/chat` | Ask questions with wiki context, use provider/model selection, attachments, and optional agent mode. |
| `/health` | Check readiness, storage paths, maintenance telemetry, search status, and recent activity. |
| `/variables` | Manage source-link variables used by wiki records and MCP source bundles. |
| `/mcp` | Local JSON-RPC endpoint for agent tools over memories, pages, context packs, and source links. |

## How To Use These Pages

Use pages for prose that should be readable in a browser: operating notes, project orientation, release notes, and decisions that need narrative context. Use structured memories for compact facts that should rank well in search, carry source links, and participate in context-pack traversal.

Pages can link to page assets with markdown image paths such as `assets/example.png`; the app serves those files under `/page-assets` when rendered.