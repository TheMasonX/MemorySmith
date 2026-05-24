# Variables and Source Links

The Variables feature at `/variables` manages reusable path tokens used by memory source links.

## What It Does

- Stores variable mappings such as `%MemorySmithRepo%` in `Data/vars.json`.
- Expands variables inside source link URIs.
- Supports source-link driven workflows across UI and MCP tools.
- Helps keep source links portable across local environments.

## Why It Matters

Source links tie memory records to concrete code evidence. Variables make those links resilient when machine paths differ.

## Key Capabilities

- Variable CRUD through the UI.
- Source-link line range support for focused snippets.
- Compatibility with source bundle and source lookup MCP tools.
- Guard rails for allowed source roots and maximum read sizes.

## Related Pages

- [Memories Workbench](memories-workbench.md)
- [API and MCP Integration](api-and-mcp.md)
- [Architecture](../guides/architecture.md)
