# Task Tracking Feature

## Summary

MemorySmith now includes a first-class task tracking backend and site page at `/tasks`.
The feature converts high-level work items from the wiki tracker into durable task records under `Data/Tasks` with history in `Data/Events/tasks.activity.jsonl`.

This page describes current behavior and intentional V1 limits.

## Current Scope (V1)

- Full task domain API at `api/tasks` for list/get/history/create/update/status/comments/links/attachments/delete.
- Site-level Tasks workbench at `/tasks` backed by `ITaskService`.
- Assignee model:
  - Primary: directory-style assignee id (`AssigneeMode=Directory` + `AssigneeDirectoryId`).
  - Optional fallback: custom text assignee (`AssigneeMode=Custom` + `AssigneeCustomText`).
- Comments and activity history are append-only and durable.
- Attachments and external links are metadata references (URI-based), not binary blob storage.
- Linked pages are validated for existence, but invalid links only create warnings and do not block task updates.

## Linked Page Validation Behavior

When adding a linked page slug:

- The slug is canonicalized (supports `pages/...` or `.md` inputs).
- Existence is checked against `Data/Pages`.
- The link is still recorded even if missing.
- A warning activity event is appended:
  - `page_link_warning`
  - Note format: `Linked page not found: <slug>`

This keeps workflows non-blocking while preserving operator-visible diagnostics.

## State Model

Current statuses:

- `Backlog`
- `Ready`
- `InProgress`
- `Blocked`
- `Done`
- `Archived`

Finite state transition enforcement is intentionally deferred and remains an optional future feature.

## Storage and Contracts

- Task record files: `Data/Tasks/*.json`
- Task activity log: `Data/Events/tasks.activity.jsonl`
- Main implementation:
  - `MemorySmith.App/Services/TaskDomainService.cs`
  - `MemorySmith.App/Controllers/TasksController.cs`
  - `MemorySmith.App/Components/Pages/Tasks.razor`

## Tracker Import

Open rows from `Data/Pages/workbench/tasks.md` can be imported into the backend.
Imported tasks use:

- Labels: `tracker-import`, `workbench`, `future`
- Reporter: `tracker-import`
- Linked page: `workbench/tasks`
- Notes mapped to an initial comment when available
- Screenshot mapped to an attachment when available

The utility script used for import is:

- `Scripts/Import-OpenTasksFromWorkbench.ps1`

## Verification

Focused test coverage includes:

- `TasksApi_FullWorkflow_SupportsCrudHistoryAndRelatedArtifacts`
- `TasksPageRoute_ReturnsSuccessAndContainsTasksHeading`

The workflow test explicitly verifies that invalid linked pages generate warning activity and do not fail the operation.
