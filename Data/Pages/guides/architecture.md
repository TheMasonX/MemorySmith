# Architecture

MemorySmith is designed as one deployable ASP.NET Core app. The UI, REST API, MCP endpoint, chat workflow, markdown pages, storage, and background maintenance all run in the same host.

That shape keeps the system understandable: there is one app process, one file-backed data folder, one set of app services, and one place to look when behavior changes.

## Project Map

| Project | Responsibility |
|---|---|
| `MemorySmith.Core` | Domain models, scoring, state transitions, and project documentation. |
| `MemorySmith.Storage` | File-backed memory, variable, and event stores. |
| `MemorySmith.App` | The active host: Blazor UI, controllers, MCP, chat, diagnostics, and maintenance. |
| `MemorySmith.Tests` | NUnit tests for domain behavior, storage hardening, API contracts, search quality, and app services. |
| `MemorySmith.Benchmarks` | BenchmarkDotNet and smoke checks for search/context workflows. |

Legacy `MemorySmith.Worker` and `MemorySmith.Dashboard` code was removed after the single-host migration. Treat `MemorySmith.App` as the source of truth.

## Runtime Flow

```text
Blazor pages
    -> MemoryApplicationService
        -> file stores, search services, stats, events

REST controllers
    -> MemoryApplicationService
        -> same behavior used by the UI

MCP endpoint and chat agent
    -> search, context pack, page search, source bundle, memory get
        -> same wiki data used by the app
```

The important design rule is that UI and API behavior should not drift. Both should pass through the same app service layer wherever possible.

## Data Model

Structured memories are JSON records under `Data/Memories/{Status}`. They are good for facts that benefit from metadata: tags, confidence, references, conflicts, source links, usage counts, and status.

Markdown pages are files under `Data/Pages`. They are good for explanations, runbooks, notes, and readable project context. Pages are searched alongside memories when chat or combined search needs broader context.

## Maintenance

Background maintenance handles triage, consolidation, indexing, and telemetry. It runs inside `MemorySmith.App`, writes events to `Data/Events/audit.log`, and reports state through `/health` and diagnostics endpoints.

## Safety Boundaries

- API and MCP access is local-only by default unless `AllowRemoteApi` is enabled.
- `ApiKey` can require an `X-Api-Key` header for guarded API and MCP callers while leaving browser auth/setup routes available for LAN UI sign-in.
- Source bundle reads are bounded by size and allowed roots.
- Raw HTML in rendered pages is disabled by default.
- Chat tool-call execution is read-only; durable writes require Agent mode and explicit `Chat:AgentWritesEnabled` opt-in, which is disabled by default.

## Design Preference

When adding features, keep the simple single-host model intact unless there is clear evidence that a new process, queue, database, or service boundary solves a real current problem.