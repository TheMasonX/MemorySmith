# MemorySmith Pages

MemorySmith is a local-first memory workbench. It keeps structured project knowledge in JSON records, longer notes in markdown pages, and exposes both to the UI, API, chat, and MCP tools.

This folder is the human-readable side of the project wiki. Pages here are meant to be easy to skim before changing the codebase, running the app, or asking the chat agent for help.

## Start Here

- [Architecture](architecture.md) explains the single-host app shape and where each project fits.
- [System Setup Guide](system-setup.md) walks through prerequisites, first run, admin bootstrap, validation, and troubleshooting.
- [Configuration Reference](configuration-reference.md) maps the active `MemorySmith:*` settings to their edit and verification surfaces.
- [Agent and Model Configuration](agent-configuration.md) explains chat model profiles, maintenance-agent model routing, and write gates.
- [HTTPS Setup Guide](https-setup.md) explains local development certificates, HTTPS launch profile setup, and verification steps.
- [Features](../features/index.md) provides product-level overviews of major app capabilities.
- [Operations](../ops/operations.md) covers running, validating, publishing, and the important data paths.
- [Wiki Health and Validation](../ops/wiki-health-and-validation.md) documents the current wiki/task validation commands and known live-memory validation gap.
- [Search and Chat](search-and-chat.md) explains lexical, semantic, hybrid, MCP, chat, and agent behavior.
- [Core Memory System Improvements RFC](../plans/temp-plan.md) reviews long-term options for making memories, search, pages, and chat more useful for AI agents and humans.
- [AI Memory Suite Implementation Plan](../plans/ai-memory-suite-implementation-plan.md) records the full 10-round council synthesis and phased plan for tag policy, staleness, retrieval, Agent writes, schema promotion, and page chunking.
- [Memory Governance Guide](memory-governance-guide.md) documents the first warning-first governance slice: tag policy, diagnostics, maintenance recommendations, and context-pack warning metadata.
- [Council Workflow](../council/llm-council.md) describes the multi-perspective review method for major MemorySmith decisions.
- [Deep Research Prompt](../research/memory-system-deep-research-prompt.md) turns the RFC's externally researchable questions into a prompt for Microsoft Copilot or ChatGPT Deep Research.
- [Deep Research Intake Notes](../research/deep-research-intake-20260520.md) distills the latest external-research response into decision-ready guidance and unresolved local questions.
- [Future Tasks](../workbench/tasks.md) tracks product work in a checklist format.

## What Lives Where

| Area | Purpose |
| --- | --- |
| `Data/Memories` | Structured wiki records used by search, MCP, tests, and agent context. |
| `Data/Pages` | Markdown pages for readable notes, runbooks, and planning context. |
| `Data/Pages/assets` | Page images, video, audio, and other files served through `/page-assets`. |
| `Data/Events` | Append-only audit/activity log. |
| `Data/Graph` | Reserved data folder for graph-oriented project knowledge. |
| `Data/Policies` | File-backed governance policy such as the local tag policy. |
| `Data/Models` | Optional local ONNX embedding model and vocabulary files. |
| `Data/vars.json` | Path variables such as `%MemorySmithRepo%` for source links. |
| `Data/Tasks` | First-class task records used by `/tasks` and sprint/workflow tracking. |

## Current App Surfaces

| Surface | What it is for |
| --- | --- |
| `/memories` | Browse, search, create, edit, and maintain structured memory records. |
| `/pages` | Create, search, edit, preview, and render markdown-backed pages from this folder. |
| `/chat` | Ask questions with wiki context, use provider/model selection, attachments, and optional agent mode. |
| `/health` | Check readiness, storage paths, maintenance telemetry, search status, and recent activity. |
| `/variables` | Manage source-link variables used by wiki records and MCP source bundles. |
| `/mcp` | Local JSON-RPC endpoint for agent tools over memories, pages, context packs, and source links. |

## How To Use These Pages

Use pages for prose that should be readable in a browser: operating notes, project orientation, release notes, and decisions that need narrative context. Use structured memories for compact facts that should rank well in search, carry source links, and participate in context-pack traversal.

Pages can link to page assets with markdown image paths such as `assets/example.png`; the app serves those files under `/page-assets` when rendered.
