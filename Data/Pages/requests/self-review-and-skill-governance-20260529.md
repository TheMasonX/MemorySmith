# Request: Self-Review Skill and Ongoing Skill Governance

## Request Summary

Add a user-invocable self-review skill where the agent evaluates improvements to skills and agent prompt quality, and keeps request tracking pages updated.

## Scope

- Add user-visible `/self-review` skill.
- Require updates to `Data/Pages/requests` master list and grouped request pages.
- Keep recommendations categorized as now/next/later.

## Status

`Implemented`

## Evidence

- Added `.github/skills/self-review/SKILL.md`.
- Added `Data/Pages/requests/skill-and-agent-prompt-requests-master-list-20260529.md`.
- Added grouped request pages under `Data/Pages/requests/`.

## Follow-Up

- Add cadence guidance (for example: weekly self-review pass).
- Link this request set to `/tasks` records for execution visibility.
- Treat chat tool usage parity as an explicit review dimension, not an implied prompt/runtime hygiene concern.
- Added from the 2026-05-29 follow-up self-review pass:
  - grouped request page: `skill-small-improvements-batch-20260529.md`
  - dedicated significant request page: `pr-review-closure-and-thread-sync-skill-20260529.md`
  - dedicated significant request page: `mcp-authoring-usability-and-chat-tool-parity-20260529.md`
  - task records: `TSK-0223` and `TSK-0224`
