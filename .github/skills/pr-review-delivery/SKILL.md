---
name: pr-review-delivery
description: 'Submit a pull request, request review, triage comments, fix issues, and use script-driven review waits with a bounded timeout before merge/cleanup. Use when implementation is complete and the branch is ready for PR handling end to end.'
argument-hint: 'Current branch, base branch, PR title/summary, and validation evidence'
user-invocable: true
disable-model-invocation: false
---

# PR Review Delivery

Inherits from `task-core-loop`.

Use this skill to take a ready branch through PR creation, review handling, merge, and cleanup.

## Outcome

Produce all of the following:
- A pull request with a clear summary, design context, and validation evidence
- Review requested promptly after PR creation
- Review comments triaged into defects, questions, or incorrect suggestions
- Follow-up fixes validated and pushed in focused increments
- Script-driven review waiting with a total timeout budget of 20 minutes unless the user overrides it
- Merge completion and branch cleanup when the PR is ready

## Use When

Use this workflow when implementation is complete and the next job is PR handling, especially with phrases like:
- open the PR
- request Copilot review
- triage review comments
- poll for review
- fix PR comments
- merge when ready
- delete the branch
- get back to base branch

Do not use this workflow to do initial design exploration or broad feature implementation. Use `/design-doc-implementation` first for that phase.

## Inputs

Collect or infer these inputs before starting:
- Current working branch and intended base branch
- PR title, scope summary, and linked design doc or design note if one exists
- Validation evidence to include in the PR body
- Merge strategy preference, if the user names one
- Remote and permission constraints for PR creation or merge

## Guardrails

- Do not open the PR until implementation is validated and the branch is reviewable.
- Treat the total review wait budget as 20 minutes by default.
- Prefer snapshot checks first, then a single script-driven wait pass instead of repeated chat-level polling.
- Use the hook script from repository root when available:
  - `pwsh ./Scripts/SkillHooks/Wait-PrReviewState.ps1 -PullNumber <number> -PollSeconds 60 -TimeoutMinutes 20`
- For each actionable comment, make the smallest fix that addresses the issue and rerun the narrowest validation that can fail.
- Do not blindly implement incorrect review comments; answer them with evidence.
- If review feedback changes the feature contract, revisit the design or council output before continuing.
- Keep follow-up commits focused so comment resolution remains traceable.

## Procedure

1. Confirm PR readiness.
Check branch status, recent validation, and whether the remote branch exists.
If the branch is not ready, stop and return control to `/design-doc-implementation`.

2. Prepare the PR summary.
Summarize what changed, why it changed, what design doc or decision record guided it, and which validations were run.

3. Create the PR and request review.
Open the PR against the intended base branch.
Immediately request Copilot review and any other requested reviewers.

4. Start the review loop.
Do one immediate snapshot check.
If waiting is needed, run one script-driven wait pass with a bounded timeout.
Track total elapsed wait time and stop automatic waiting at 20 minutes unless the user overrides it.

5. Triage incoming comments.
For each new comment, classify it as:
- real defect
- clarification request
- incorrect or non-actionable suggestion

6. Resolve actionable comments.
Fix confirmed defects in small slices, rerun targeted validation, push follow-up commits, and update the PR thread or resolution state.
If the comment is incorrect, answer with evidence rather than changing working code.

7. Re-enter the bounded wait loop only when needed.
After each meaningful push or resolution pass, do a snapshot check first, then optional script-driven wait until one of these is true:
- no blocking comments remain and the PR is ready
- the PR merged
- the 20-minute total wait budget is exhausted

8. Merge and clean up.
When the PR is ready, update from the base branch if needed, complete the merge using the requested strategy or the repo default, then delete remote and local branches and return to the updated base branch.

9. Stop cleanly on timeout or blockers.
If the 20-minute total wait budget expires or permissions block merge, summarize exact remaining status and next action instead of silently waiting longer.

## Decision Branches

- Branch A: No review arrives before timeout
Stop after the 20-minute total budget, report the status, and leave the PR ready for later follow-up.

- Branch B: Review comment reveals a real defect
Fix it, validate it, push it, and keep polling.

- Branch C: Review comment conflicts with current evidence
Answer with tests, design rationale, or code evidence and keep the implementation unless new evidence disproves it.

- Branch D: Merge is blocked by stale base branch or conflicts
Rebase or update, rerun the narrow validations that could change, and then merge.

- Branch E: Permissions prevent merge or branch deletion
Report the exact blocked step and leave the repo in the safest ready state.

## Completion Checks

This workflow is complete only when all are true:
- PR created with design context and validation evidence
- Review requested
- Review comments triaged and resolved or answered with evidence
- Waiting used snapshot-first plus script-driven bounded waits, respecting a 20-minute total timeout budget unless overridden
- Merge completed or a precise blocker/timeout summary was produced
- Branch cleanup and base-branch return happened when permissions allowed

## Prompt Patterns

Use prompts like:

```text
/pr-review-delivery Submit the current branch as a PR, request Copilot review, use script-driven bounded wait checks for up to 20 minutes total, fix valid comments, and merge plus clean up when ready.
```

```text
/pr-review-delivery Handle PR review end to end for this branch. Use the linked design doc and validation notes in the PR summary, triage comments carefully, and stop with an exact status if the 20-minute total wait budget expires.
```