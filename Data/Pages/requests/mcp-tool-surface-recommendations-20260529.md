# Request: MCP Tool Surface Recommendations (2026-05-29)

## Summary

Backlog capture for MCP tool-surface changes identified during review of the current `/mcp` contract, chat tool catalog, and governance workflows.

## Recommendations

1. Default off the internal `memorysmith_code_search_merge_shard` MCP tool unless explicitly enabled.
2. Add proposal workflow MCP tools for listing, reading, and acting on proposals.
3. Add maintenance MCP tools for run/status/topic-map operations.
4. Add memory proposal or write MCP tools so external MCP clients can participate in the same governance model as chat proposals.
5. Add `dryRun` or `validateOnly` support on write-capable MCP tools.
6. Add named MCP tool profiles or modes beyond raw enabled/disabled name lists.
7. Add structured `format=json|envelope` support for page MCP tools.

## Linked Tasks

- [TSK-0225](../tasks?task=TSK-0225) Default-off gate `memorysmith_code_search_merge_shard`
- [TSK-0226](../tasks?task=TSK-0226) Add proposal workflow MCP tools
- [TSK-0218](../tasks?task=TSK-0218) Add maintenance MCP tools
- [TSK-0219](../tasks?task=TSK-0219) Add memory proposal/write MCP tools
- [TSK-0220](../tasks?task=TSK-0220) Add `dryRun`/`validateOnly` support for MCP writes
- [TSK-0221](../tasks?task=TSK-0221) Add named MCP tool profiles
- [TSK-0222](../tasks?task=TSK-0222) Add structured page MCP formats

## Evidence Anchors

- `README.md` MCP tool table and authorization notes.
- `MemorySmith.App/Services/ChatToolCatalog.cs` for actual tool descriptors.
- `MemorySmith.App/Controllers/McpController.cs` for enabled/default-off behavior.
- `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md` for current in-app chat tool contract.
