# API and MCP Integration

MemorySmith exposes both REST APIs and an MCP endpoint so local automation and AI tools can use the same data and service layer as the UI.

## What It Does

- Provides REST routes for memories, pages, search, chat, auth, admin, stats, and diagnostics.
- Exposes MCP tools at `/mcp` for memory and page retrieval workflows.
- Reuses shared application services to avoid UI and API behavior drift.
- Applies role and policy checks consistently across surfaces.

## Why It Matters

This feature makes MemorySmith usable by scripts, local tools, and AI agents without creating a separate backend product.

## Key Capabilities

- Unified search and retrieval surfaces across memories and pages.
- Context-pack and source-bundle support for evidence-driven workflows.
- Edit-gated page save and delete MCP operations.
- Local-first operational defaults with optional API hardening controls.

## Related Pages

- [Search System](search-system.md)
- [Variables and Source Links](variables-and-source-links.md)
- [Search and Chat](../guides/search-and-chat.md)
