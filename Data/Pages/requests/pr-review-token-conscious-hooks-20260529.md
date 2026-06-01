# Request: Token-Conscious PR Review and Script Hooks

## Request Summary

Make PR review delivery less token-expensive by avoiding repeated chat polling and using script-driven wait hooks.

## Scope

- Move from repeated chat-level polling to snapshot-first checks plus script-driven bounded waits.
- Add script hooks for PR wait and CI snapshot operations.
- Update PR review skill guidance accordingly.

## Status

`Implemented`

## Evidence

- Added `Scripts/SkillHooks/Wait-PrReviewState.ps1`.
- Added `Scripts/SkillHooks/Get-CiSnapshot.ps1`.
- Updated canonical repo skill `.github/skills/pr-review-delivery/SKILL.md` with script-driven wait policy.
- Updated CI monitor skill guidance to prefer script hook usage.

## Follow-Up

- Evaluate extracting additional repeated terminal flows to `Scripts/SkillHooks/*`.
- Consider adding machine-readable output contracts for each hook script.
