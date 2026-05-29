# Request: Skill Small Improvements Batch (2026-05-29)

## Request Summary

Apply a small, low-risk batch of skill/tooling improvements that reduces repeated command construction and prompt duplication without changing product runtime behavior.

## Why This Batch Exists (Evidence)

- The tracker shows frequent repeated focused-test command construction with long `dotnet test --filter` strings and repeated validator triads (`Scripts/Test-TaskRecords.ps1`, `Scripts/Test-PageLinks.ps1`, `Scripts/Test-PagePathLiterals.ps1`) in many rounds.
- CI monitoring guidance is currently duplicated across `.github/agents/smith.agent.md`, `.github/skills/ci-budget-conscious-monitor/SKILL.md`, and `.github/skills/training-sprint-loop/SKILL.md`.
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

- Proposal: keep authoritative CI-mode policy in `.github/skills/ci-budget-conscious-monitor/SKILL.md` and shorten repeated copies in other skills to one-line references.
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

## Status

`Requested`

## Follow-Up

- If approved, implement changes 1 and 2 first as a narrow batch and validate with `get_errors` plus script smoke checks.