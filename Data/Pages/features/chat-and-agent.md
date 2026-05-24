# Chat and Agent

The Chat and Agent surface at `/chat` supports memory-enhanced chat and controlled agent workflows in one interface.

## What It Does

- Runs chat with provider and model selection.
- Streams responses with trace and references support.
- Allows read-only tool-assisted retrieval during chat turns.
- Supports optional agent write proposals with explicit approvals.

## Why It Matters

Chat provides fast question answering over local project knowledge. Agent mode introduces structured, approval-gated change proposals without bypassing governance controls.

## Key Capabilities

- Context-aware preloading for MemorySmith-specific prompts.
- Tool interception and tool-call loops with bounded limits.
- Attachments support for text and images.
- Shared right sidebar for history and execution trace.
- Proposal-first write flow when agent writes are enabled.

## Safety And Governance

- Tool execution is read-only by default.
- Agent writes require explicit feature enablement and sufficient role.
- UI approval is required before write proposals are applied.

## Related Pages

- [Search System](search-system.md)
- [Proposals and Governance](proposals-and-governance.md)
- [Search and Chat](../guides/search-and-chat.md)
