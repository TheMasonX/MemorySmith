# Skill and Agent Prompt Requests Master List (2026-05-29)

Status legend: `Requested` | `InProgress` | `Implemented` | `Deferred`

## Outstanding and Recent Requests

1. [Core task inheritance across skills](skill-core-inheritance-and-missing-skills-20260529.md) - `Implemented`
2. [Token-conscious PR review hooks and script waits](pr-review-token-conscious-hooks-20260529.md) - `Implemented`
3. [Self-review skill and governance loop](self-review-and-skill-governance-20260529.md) - `Implemented`
4. [MCP-first modular extraction batch 2](mcp-first-modular-extraction-batch2-20260529.md) - `Implemented`
5. [MCP tool surface recommendations](mcp-tool-surface-recommendations-20260529.md) - `Requested`
6. [MCP authoring usability and chat tool parity](mcp-authoring-usability-and-chat-tool-parity-20260529.md) - `Requested`
7. [Skill small improvements batch](skill-small-improvements-batch-20260529.md) - `Requested`
8. [PR review closure and thread sync skill](pr-review-closure-and-thread-sync-skill-20260529.md) - `Deferred` (covered by canonical `pr-review-delivery` + hook-script follow-ups)
9. Script extraction backlog for additional repeated terminal flows - `Requested`
10. Prompt/runtime parity audit cadence standardization - `Requested`
11. Wiki-memory hygiene audit cadence standardization - `Requested`

## 2026-05-30 Self-Review Follow-Up

1. [Skill small improvements batch](skill-small-improvements-batch-20260529.md) - `InProgress`
2. [MCP authoring usability and chat tool parity](mcp-authoring-usability-and-chat-tool-parity-20260529.md) - `InProgress`
3. Focused dotnet test command hook execution task (`TSK-0251`) - `Requested`
4. Skill contract lint script execution task (`TSK-0252`) - `Requested`
5. [Model profile and Ollama comparator parity for training A/B](model-profile-and-ollama-comparator-parity-20260530.md) - `InProgress`
6. Model-profile bootstrap hook for trained/default and stock comparator (`TSK-0253`) - `Requested`
7. Canonicalize PR review skill in repo-local skills and keep repo as skill source of truth - `Implemented`

### Now / Next / Later Snapshot

- `Now` (high confidence): script hooks for repeated focused test command generation (`TSK-0251`, confidence 93%) and skill contract linting (`TSK-0252`, confidence 90%).
- `Now` (high confidence): add model-profile/Ollama bootstrap automation for trained default plus stock comparator parity (`TSK-0253`, confidence 92%).
- `Next` (medium-high confidence): parity drift automation and coding-agent surface synchronization under `TSK-0224` (confidence 90%).
- `Later` (medium confidence): cadence refinements for prompt/runtime and hygiene audits once script hooks are stabilized (confidence 85%).

### 2026-05-31 Skill Pack Rationalization Snapshot

- Skill count target adopted: keep repo-local skills within an 8-10 range.
- Canonical source policy adopted: PR review delivery skill now lives in `.github/skills/pr-review-delivery/SKILL.md`.
- Consolidation applied: retired narrow standalone skills `gpu-reality-validation` and `training-contract-evolution` in favor of broader training loop coverage.

## Maintenance Rules

- Add all new skill/agent-prompt change requests here first.
- Use a dedicated request page when scope is significant.
- Group small related changes into one request page when possible to avoid page clutter.
- Mark status updates in both this master list and the linked request page.
