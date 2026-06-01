# Request: Core Skill Inheritance and Missing Skills

## Request Summary

Extract shared task workflow into a common base, then let specialized skills add only their unique context.

## Scope

- Add shared `task-core-loop` skill.
- Refactor existing skills to inherit from `task-core-loop`.
- Add missing user-visible skills:
  - `task-delivery-sprint-loop`
  - `runtime-parity-audit`
  - `wiki-hygiene-audit`

## Status

`Implemented`

## Evidence

- Added `.github/skills/task-core-loop/SKILL.md`.
- Added `.github/skills/task-delivery-sprint-loop/SKILL.md`.
- Added `.github/skills/runtime-parity-audit/SKILL.md`.
- Added `.github/skills/wiki-hygiene-audit/SKILL.md`.
- Updated existing skills to reference inheritance model.

Rename note (2026-05-31): canonical names were later simplified from `prompt-runtime-parity-audit` and `wiki-memory-hygiene-audit` to `runtime-parity-audit` and `wiki-hygiene-audit` for clearer skill identity.

## Follow-Up

- Consider adding a lightweight test/check that every implementation-focused skill references `task-core-loop`.
