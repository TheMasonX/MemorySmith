# Admin Models And Maintenance Agent Planning Notes

Status: planning page, 2026-05-22

This page tracks a grouped set of user-facing configuration and agent-governance requests that need design before implementation. It is intentionally a page, not a structured memory record, because the items describe desired future behavior rather than existing system state.

## Immediate UI Corrections

- The Tag Manager should remain a main navigation item for Admin users and should not appear as a tab inside `/admin`.
- The `/admin` provider-management tab should be labeled `OAuth` so it is clear that this surface controls external sign-in providers.
- Editable settings should show the friendly setting name and info icon on the first line, then the configuration key on the next line.
- The Proposals page should remain usable at narrow desktop widths: the proposal list and selected proposal should stay visible, and approve/respond/reject controls should fit in one horizontal row where practical.
- Starting a maintenance run from the Proposals page should immediately show that work is active instead of appearing idle until a refresh.

## Model Registry

Admins need a first-class Models page for named model profiles. A model profile should include at least:

| Field | Purpose |
| --- | --- |
| Name | User-facing profile name, such as `Athena`. |
| Provider | Runtime provider, such as `Ollama` or `GitHub`. |
| Model | Provider model id, such as `gemma4:e4b`. |
| Context window | Human/admin-friendly context budget, such as `32k`, normalized to tokens internally. |
| Chat settings | Temperature, tool budget, context preload, compaction policy, or other provider-safe settings. |
| Access | Roles or users allowed to select the profile. |
| Assignments | Optional default for chat, maintenance agent, or specialized workflows. |

Chat should not silently fall back to manual provider/model text values. If no default model profile is configured, chat should be disabled with a clear message telling an Admin to configure a default model. Once profiles exist, the Chat page should select from profile names rather than free-form provider/model editing.

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

The Proposals page should gain a Request Agent Review action. The review agent should evaluate the selected proposal as if performing a PR review, write feedback into the proposal history/comments, and when it recommends changes, create a revised proposal while preserving the original proposal and diff. Users must be able to disagree with the review and keep the original proposal path.

Open design questions:

- Which model profile should perform proposal reviews by default?
- Should review-generated revisions require the same approval path as normal maintenance proposals?
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
