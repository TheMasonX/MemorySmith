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

## 2026-05-30 Follow-Up

### Improvement shortlist (impact / effort)

1. Add a model-profile bootstrap hook that verifies Ollama tags and writes a trained-default + stock-comparator profile pair.
  - Impact: high
  - Effort: medium
  - Classification: `Now`
  - Confidence: 92%
2. Collapse duplicated council skill intent wording between repo and user-scoped skill surfaces into a single canonical contract note.
  - Impact: medium
  - Effort: low
  - Classification: `Next`
  - Confidence: 88%
3. Add a compact parity matrix output mode for prompt/runtime/tool-catalog checks to reduce token cost in recurring audits.
  - Impact: medium
  - Effort: low-medium
  - Classification: `Next`
  - Confidence: 89%
4. Add a monthly script-driven self-review cadence entrypoint that snapshots open request pages and unresolved task links.
  - Impact: medium
  - Effort: low
  - Classification: `Later`
  - Confidence: 84%

### Evidence

- Local Ollama currently has stock `qwen3.5:4b`; trained `memorysmith-athena:latest` is not present in the current local runtime.
- Added a 24k stock comparator tag `qwen3.5:4b-24k-stock` and profile wiring in local override files to support A/B routing comparisons in Admin Models and chat profile selection.

### Tracking

- Dedicated significant request page: `model-profile-and-ollama-comparator-parity-20260530.md`
- Significant execution task: `TSK-0253`
