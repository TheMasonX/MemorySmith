# Request: Skill Small Improvements Batch (2026-05-29)

## Request Summary

Apply a small, low-risk batch of skill/tooling improvements that reduces repeated command construction and prompt duplication without changing product runtime behavior.

## Why This Batch Exists (Evidence)

- The tracker shows frequent repeated focused-test command construction with long `dotnet test --filter` strings and repeated validator triads (`Scripts/Test-TaskRecords.ps1`, `Scripts/Test-PageLinks.ps1`, `Scripts/Test-PagePathLiterals.ps1`) in many rounds.
- CI monitoring guidance is currently duplicated across `.github/agents/smith.agent.md`, `.github/skills/ci-status-monitor/SKILL.md`, and `.github/skills/training-sprint-loop/SKILL.md`.
- Skill inheritance and MCP-first adoption now exist, but there is no lightweight repo check that enforces those contracts.

## Proposed Small Changes

### 1. Add a focused test command builder hook

- Proposal: add `Scripts/SkillHooks/New-FocusedDotnetTestCommand.ps1`.
- Purpose: generate a stable `dotnet test ... --filter` command from test names and optional categories.
- Output contract: machine-readable JSON with `command`, `filter`, and `warnings`.
- Classification: `Now`.
- Impact: medium (fewer command typos and shorter chat tokens).
- Effort: low.
- Confidence: 93%.

### 2. Add a compact "skill contract" verifier

- Proposal: add `Scripts/Test-SkillContracts.ps1`.
- Purpose: verify each implementation-focused skill includes `Inherits from task-core-loop`, and each user-invocable skill includes an output section.
- Scope: structural/lint checks only, no behavior checks.
- Classification: `Now`.
- Impact: medium-high (prevents contract drift).
- Effort: low-medium.
- Confidence: 90%.

### 3. Collapse duplicated CI-mode wording into one reference block

- Proposal: keep authoritative CI-mode policy in `.github/skills/ci-status-monitor/SKILL.md` and shorten repeated copies in other skills to one-line references.
- Classification: `Next`.
- Impact: medium (token and maintenance reduction).
- Effort: low.
- Confidence: 88%.

### 4. Add a tiny schema README for hook outputs

- Proposal: add `Scripts/SkillHooks/README.md` documenting stable output keys for `Get-CiSnapshot.ps1`, `Wait-PrReviewState.ps1`, `New-TaskEvidenceComment.ps1`, and `New-CouncilEvidenceBundle.ps1`.
- Classification: `Later`.
- Impact: medium (easier reuse by future skills/agents).
- Effort: low.
- Confidence: 85%.

### 5. Add a model-profile bootstrap hook for trained/default + stock comparator setup

- Proposal: add `Scripts/SkillHooks/Ensure-TrainingModelProfiles.ps1`.
- Purpose: verify required Ollama tags exist, optionally create a stock 24k comparator tag, and write a stable pair of model profiles (trained default + stock comparator) into the resolved settings override.
- Output contract: machine-readable JSON with `resolvedSettingsPath`, `ollamaTags`, `createdTags`, `profileIds`, and `warnings`.
- Classification: `Now`.
- Impact: high (removes repeated manual setup drift before A/B sessions).
- Effort: medium.
- Confidence: 92%.

## Status

`InProgress`

Execution tracking:

- `TSK-0251` - focused dotnet test command hook
- `TSK-0252` - skill contract lint script
- `TSK-0253` - training model profile bootstrap hook

## Follow-Up

- Now:
  - implement change 1 via `TSK-0251` (confidence 93%)
  - implement change 2 via `TSK-0252` (confidence 90%)
- Next:
  - collapse duplicated CI wording to shared references (confidence 88%)
- Later:
  - add stable hook output schema README and examples (confidence 85%)
