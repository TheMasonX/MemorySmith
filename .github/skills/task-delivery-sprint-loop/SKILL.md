---
name: task-delivery-sprint-loop
description: "Run a /tasks-first implementation loop from active task selection through validation evidence and task status/comment updates."
argument-hint: "Task ID/key, change slice, and validation scope"
user-invocable: true
disable-model-invocation: false
---

# Task Delivery Sprint Loop

Inherits from `task-core-loop`.

## Added Context
Use this when delivery should be anchored to `/tasks` records, not ad hoc notes.

## Additional Inputs
- Task id or key.
- Target status transition (for example: Ready -> InProgress -> Done).
- Validation evidence required before completion.

## Additional Procedure
1. Load task context and linked pages before edits.
2. Move task status to `InProgress` when implementation starts.
3. Apply core steps from `task-core-loop`.
4. Add task comment evidence (files changed, validation commands, outcomes).
5. Transition task status only after validation evidence is captured.

## Done Criteria
- Task record reflects current status accurately.
- Evidence is attached in task comments.
- Code and wiki notes are synchronized with the delivered slice.
