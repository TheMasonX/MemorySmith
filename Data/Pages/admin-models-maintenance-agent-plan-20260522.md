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

Implemented baseline: `/admin` now includes a Models tab backed by `ChatModelProfileService`. Admins can define chat model profiles with name, provider, model id, optional context-window tokens, enabled state, default chat selection, role allowlist, and description. `/chat` now selects from enabled model profiles allowed for the current user role and disables send when no enabled default profile is available. Existing installs continue to get an implicit legacy default derived from the older Chat provider/model settings until explicit profiles are configured.

Remaining design work: model profiles still need maintenance-agent/review-agent assignments and provider-safe chat settings beyond context-window metadata. Future profile fields should include at least:

| Field | Purpose |
| --- | --- |
| Chat settings | Temperature, tool budget, context preload, compaction policy, or other provider-safe settings. |
| Assignments | Optional default for maintenance agent, proposal review agent, or specialized workflows. |

## Maintenance Agent Tasks And Logs

Admins need visibility into maintenance-agent work while it is running and after it completes. A later implementation should consider a durable maintenance activity store with:

- active task state;
- task start/end timestamps;
- task trigger and selected task type;
- warnings, proposal ids, and output summaries;
- admin-only transcript-style logs;
- optional admin conversation with the maintenance agent using the same model registry and role rules as chat.

The current Proposals page can show page-local activity while a run is started from that page, but durable task history belongs in the later activity/log design.

## Proposal Review Agent

Implemented baseline: the Proposals page now exposes a Request Agent Review action for actionable proposals. The button calls `MaintenanceAgentService.ReviewProposalAsync`, records an `agent_review_requested` history event plus the optional human comment, runs the configured maintenance-agent LLM provider when enabled, records `agent_review_completed` feedback, and can save a validated revised proposal through `SubmitAgentRevisionAsync` while preserving the original proposal and diff.

Remaining design work: proposal review still needs a dedicated model-profile assignment, richer review verdict metadata, durable task logs, and optional fresh wiki-context retrieval. Users can currently disagree with the review by keeping the original proposal path open and ignoring or rejecting the revised proposal.

Open design questions:

- Which model profile should perform proposal reviews by default?
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
