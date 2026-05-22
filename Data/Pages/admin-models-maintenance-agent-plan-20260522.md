# Admin Models And Maintenance Agent Planning Notes

Status: mixed implementation/planning page, 2026-05-22

This page tracks a grouped set of user-facing configuration and agent-governance requests that need design before implementation. It is intentionally a page, not a structured memory record, because the items describe desired future behavior rather than existing system state.

## Immediate UI Corrections

- The Tag Manager should remain a main navigation item for Admin users and should not appear as a tab inside `/admin`.
- The `/admin` provider-management tab should be labeled `OAuth` so it is clear that this surface controls external sign-in providers.
- Editable settings should show the friendly setting name and info icon on the first line, then the configuration key on the next line.
- The Proposals page should remain usable at narrow desktop widths: the proposal list and selected proposal should stay visible, and approve/respond/reject controls should fit in one horizontal row where practical.
- Starting a maintenance run from the Proposals page should immediately show that work is active instead of appearing idle until a refresh.

## Model Registry

Implemented baseline: `/admin` now includes a Models tab backed by `ChatModelProfileService`. Admins can define chat model profiles with name, provider, model id, optional context-window tokens, enabled state, default chat selection, role allowlist, description, and assignments for maintenance runs, proposal reviews, and Admin Maintenance chat. `/chat` now selects from enabled model profiles allowed for the current user role and disables send when no enabled default profile is available. Existing installs continue to get an implicit legacy default derived from the older Chat provider/model settings until explicit profiles are configured.

Remaining design work: model profiles still need provider-safe chat settings beyond context-window metadata. Future profile fields should include at least:

| Field | Purpose |
| --- | --- |
| Chat settings | Temperature, tool budget, context preload, compaction policy, or other provider-safe settings. |
| Specialized workflow settings | Optional temperature/tool/context/compaction settings for maintenance or review workflows once governance defines their safe ranges. |

## Maintenance Agent Tasks And Logs

Implemented baseline: maintenance runs now append compact JSONL summaries to `MemorySmith:MaintenanceAgent:Storage:ActivityLogPath` and the Proposals page shows a `Recent task activity` panel with recent completed/skipped task runs, timestamps, finding/proposal counts, proposal-id drilldown buttons, and the first warning. `LastRunPath` remains the single latest-run state file for scheduler/operational checks. `/admin` now includes a Maintenance tab where admins can send non-mutating maintenance-agent messages and review recent transcript entries persisted to `MemorySmith:MaintenanceAgent:Storage:TranscriptLogPath`.

Remaining design work: admins still need deeper visibility into maintenance-agent work while it is running and after it completes. A later implementation should consider:

- active task state;
- transcript retention/search/redaction controls;
- optional tool-enabled maintenance chat once proposal governance covers generated writes.

## Proposal Review Agent

Implemented baseline: the Proposals page now exposes a Request Agent Review action for actionable proposals. The button calls `MaintenanceAgentService.ReviewProposalAsync`, records an `agent_review_requested` history event plus the optional human comment, runs the configured maintenance-agent LLM provider when enabled, records `agent_review_completed` feedback, and can save a validated revised proposal through `SubmitAgentRevisionAsync` while preserving the original proposal and diff.

Remaining design work: proposal review still needs richer review verdict metadata, detailed transcript logs, and optional fresh wiki-context retrieval. Users can currently disagree with the review by keeping the original proposal path open and ignoring or rejecting the revised proposal.

Open design questions:

- Should review verdicts become filterable proposal metadata?
- How should conflicting human and agent comments be represented in proposal history?
- Should review agents be allowed to inspect only proposal evidence, or also pull fresh wiki context?

## Chat Agent Writes Through Proposals

Standard chat-agent edits should likely use the proposal workflow instead of a separate trace-only approval path. This would unify audit, diff review, revision, approval, rejection, and history behavior for all agent writes.

Open design questions:

- How much of the current chat trace approval UI remains after writes become proposals?
- Should chat users see proposal ids inline with the assistant response?
- Should viewers/editors be able to draft proposals without approve permission?
- How should proposal history connect back to the chat turn that created it?

## Chat Compaction Mode

Long-running chat sessions may need a compacting mode. The design should decide whether compaction is automatic, user-triggered, or model-profile controlled. It should preserve auditability: compacted summaries should not erase raw history needed to understand proposal decisions, agent writes, or admin actions.

## Incomplete Note

The note `We need an admin page that is just the ...` is incomplete and needs clarification before it can become an implementation task.
