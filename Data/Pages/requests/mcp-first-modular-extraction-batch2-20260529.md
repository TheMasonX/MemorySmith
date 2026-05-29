# Request: MCP-First Modular Extraction Batch 2

## Request Summary

Continue modular extraction so common repeated operations rely on MCP tools and reusable hook scripts rather than ad hoc manual writing.

## Scope

- Add reusable hook scripts for structured evidence payload generation.
- Update shared/core skills to be MCP-first.
- Update specialized skills to consume modular hooks and MCP flows.

## Status

`Implemented`

## Evidence

- Added `Scripts/SkillHooks/New-TaskEvidenceComment.ps1`.
- Added `Scripts/SkillHooks/New-CouncilEvidenceBundle.ps1`.
- Updated `.github/skills/task-core-loop/SKILL.md` with MCP-first guardrail and modular hook catalog.
- Updated `.github/skills/task-delivery-sprint-loop/SKILL.md` to use MCP task tools and script-generated comment payloads.
- Updated `.github/skills/council/SKILL.md` to use MCP-first evidence packing and council evidence bundle script.
- Updated `.github/skills/self-review/SKILL.md` to enforce MCP-first and hook-script recommendation patterns.

## Follow-Up

- Add hook scripts for task status-transition batching and council report page scaffolding.
- Add a lightweight check that implementation-focused skills include `task-core-loop` inheritance and MCP-first guidance.
