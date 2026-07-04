# MemorySmith audit deltas addendum v6

This addendum contains only new findings or corrections discovered after the prior delta pass.

## New findings

### 1) Chat transcript persistence is best-effort only and can silently drop audit data
`ChatTranscriptWriter.WriteAsync()` catches `Exception`, logs a warning, and returns without surfacing failure to the caller. That means transcript writes can fail after a successful chat turn and the system will continue as though the audit record exists. fileciteturn111file0

**Why this matters:** the chat transcript is an audit artifact, not disposable UI state. Silent loss here breaks traceability and makes incident reconstruction unreliable.

**Recommendation:** either make transcript persistence explicit and observable in the response path, or add a durable retry/queue so failures are not only logged.

**Confidence:** 96%

### 2) Transcript retention cleanup failures are swallowed completely
`DeleteExpiredTranscripts()` catches all exceptions and intentionally ignores them. The cleanup loop can therefore fail indefinitely without any warning, which makes retention policy enforcement invisible to operators. fileciteturn111file0

**Why this matters:** retention is a compliance and storage-control concern. Hidden cleanup failures turn it into a best-effort suggestion.

**Recommendation:** emit a structured warning or metric on cleanup failure, and separate “best effort cleanup” from “silent ignore.”

**Confidence:** 90%

### 3) Task activity history can silently lose malformed audit lines
`FileTaskService.TryParseActivity()` returns `null` on `JsonException`, and `GetHistoryAsync()` filters those records out without any repair record or warning. A malformed activity line simply disappears from the returned history. fileciteturn125file0

**Why this matters:** activity history is the task audit trail. Silent omission hides corruption and weakens forensic value.

**Recommendation:** keep the parser tolerant, but surface a load-error marker or a malformed-line counter so operators know the history is incomplete.

**Confidence:** 92%

### 4) Task attachment upload can leave orphaned files on partial failure
`TasksController.AddFileAttachment()` writes the file to disk first, then calls `AddAttachmentAsync()` to persist the task record. If the second step fails after the file is saved, there is no rollback or cleanup of the stored file. The task service itself does not know the filesystem path, so it cannot compensate later. fileciteturn113file0turn116file0

**Why this matters:** the uploaded blob and the task metadata can diverge. That creates storage leaks and dangling attachments that are no longer referenced by any task.

**Recommendation:** wrap the two steps in a compensating transaction pattern: remove the saved file when the task metadata write fails, or stage the file and commit it only after the task update succeeds.

**Confidence:** 86%

### 5) Task actor attribution collapses too aggressively
`TasksController.Actor()` reduces anonymous or poorly-formed identities to a small set of generic fallback labels (`anonymous`, `authenticated-user`, or a claim fallback). That is convenient, but it weakens audit specificity because distinct callers can be merged into the same actor string. fileciteturn113file0

**Why this matters:** task activity records are used for traceability. Overly broad fallback actor labels reduce the value of the audit log and complicate incident review.

**Recommendation:** preserve the original identity shape when available, and use generic fallbacks only when the source is truly absent.

**Confidence:** 79%

## Consolidation opportunities

`ChatTranscriptWriter.WriteAsync()` and `FileTaskService.AppendActivity()` both implement file-backed audit logging with best-effort error handling, but they do so independently. That should be centralized into a single audit-write helper with explicit failure semantics so transcript and task activity logs cannot drift in policy.

`TasksController.AddFileAttachment()` and `TaskAttachmentFiles.SaveAsync()` split the upload workflow across two layers without a compensating rollback path. That should be refactored into one unit-of-work style helper so partial failures do not leave orphaned files behind.

## Practical next steps

1. Add operator-visible failure reporting for transcript writes and transcript retention cleanup.
2. Add incomplete-history signaling for task activity parsing failures.
3. Add rollback or staging for task file uploads.
4. Tighten actor attribution so audit trails retain as much identity detail as possible.

## Open question

The current transcript writer appears intentionally best-effort. If that is the desired policy, it should be documented as an explicit non-goal for audit durability; otherwise, the current behavior is too silent for a system that depends on traceability.
