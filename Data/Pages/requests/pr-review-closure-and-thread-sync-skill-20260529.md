# Request: PR Review Closure And Thread Sync Skill

## Request Summary

Add a dedicated skill plus hook support for closing PR review loops with less manual `gh api` plumbing and fewer missed unresolved threads.

## Problem Statement

Current PR-review follow-ups still require manual composition of:

- review-thread fetch commands,
- unresolved thread filtering,
- per-thread reply/resolution commands,
- and post-push verification.

This is repeated often enough to justify a standard skill and one helper hook.

## Evidence

- Recent PR follow-up flow required direct `gh api repos/<owner>/<repo>/pulls/<number>/comments` usage to inspect inline Copilot comments, then manual reply posting and confirmation.
- Existing hooks cover CI snapshots and bounded PR wait, but not unresolved-thread inventory and closure planning.
- Tracker evidence shows repeated PR review/checkpoint loops across rounds, including manual verification that review threads are resolved.

## Proposed Significant Change

### New user-invocable skill

- Path: `.github/skills/pr-review-closure-loop/SKILL.md`.
- Purpose: run a deterministic loop for review-thread triage and closure.
- Core procedure:
  1. Gather unresolved review threads and comments.
  2. Map each thread to `code change`, `doc clarification`, or `already addressed`.
  3. Apply narrow fix or post evidence reply.
  4. Re-check unresolved count after push.

### New hook script

- Path: `Scripts/SkillHooks/Get-PrReviewThreads.ps1`.
- Purpose: output unresolved/resolved thread inventory in JSON.
- Required output keys:
  - `pullNumber`
  - `totalThreads`
  - `unresolvedThreads`
  - `threads[]` with `id`, `path`, `author`, `state`, `latestComment`, `url`

## Priority, Impact, And Confidence

- Classification: `Now`.
- Impact: high (faster and safer PR review closure loop).
- Effort: medium.
- Confidence: 91%.

## Status

`Requested`

## Acceptance Criteria

- Skill can run end-to-end on a real PR and produce an explicit unresolved-thread delta before/after fixes.
- Hook output is stable JSON and can be consumed by the skill without custom parsing hacks.
- The skill defaults to conservative checks (single refresh) and only escalates polling when explicitly needed.

## Follow-Up

- Optionally add a second helper script later for generating templated review replies from evidence (`test command + result + file path`).