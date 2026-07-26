# Wave 1 Handoff: Antiforgery and Path Fixes

Date: 2026-07-26
Owner: Copilot
Confidence: 95%

## Completed implementation

- `TSK-0326`: removed controller-level `[IgnoreAntiforgeryToken]` from `AdminController` and `SourceLinksController`; added token-aware integration-test helpers and a tokenless admin setup regression test.
- `TSK-0358`: relative `MemorySmith:DataProtectionKeysPath` values now resolve from `AppContext.BaseDirectory` while rooted paths remain unchanged.
- `TSK-0355`: file-store paths and relative SQLite data sources now resolve from `AppContext.BaseDirectory`; `:memory:` SQLite sources remain unchanged.

## Tests

Command:

```powershell
dotnet test 'D:\@Repos\MemorySmith\MemorySmith.Tests\MemorySmith.Tests.csproj' --no-restore --filter "FullyQualifiedName~SecurityAndSourceLinkTests|FullyQualifiedName~AppApiContractTests|FullyQualifiedName~SqliteMetadataStoreTests|FullyQualifiedName~SemanticEmbeddingPathTests" --logger "console;verbosity=minimal"
```

Result: 77 passed, 0 failed, 0 skipped.

This includes the antiforgery-aware bootstrap callers, authenticated admin-settings PUT coverage, and `AdminSetup_RejectsJsonPostWithoutAntiforgeryToken`.

## Remaining scope

- Other controller-level `[IgnoreAntiforgeryToken]` attributes remain in auth/OAuth, MCP, health/diagnostics, chat, governance, maintenance, memories, pages, search, stats, and tasks controllers. They were not changed in this wave because each endpoint needs a separate contract decision (browser form, API key, webhook, or callback).
- No source-link tokenless regression was added: `/api/source-links/open` is authorization-protected, so a useful antiforgery test requires an authenticated test identity and an allowed source-link fixture.
- The focused build still reports the repository's known pre-existing warnings; no new warning was introduced by this slice.

## Worktree and commit state

The implementation is currently uncommitted in `MemorySmith` on `dev/sprint-1`. Preserve unrelated existing changes, especially `Data/Events/tasks.activity.jsonl` and `Data/Tasks/tsk-0419-task-type.json`. `MemorySmith.Agent` has separate unrelated changes on `dev/round-3` and was not modified for this wave.

Before committing, review the diff and commit only the implementation, test, and handoff files associated with `TSK-0326`, `TSK-0355`, and `TSK-0358`. Update the task records through the MemorySmith task tools rather than editing task JSON directly, then run `Scripts/Test-TaskRecords.ps1` if task metadata changes are made.

## Recommended next action

After this wave is committed and pushed, take up `TSK-0388` as the next approved item. First resolve its current task description and acceptance criteria, then keep the same sequence: narrow edit, focused test, task evidence comment, and only then status transition.

## Open questions

- Should the remaining API controller exemptions be removed in one governed task or split by endpoint contract?
- What is the intended authenticated source-link test fixture and login helper for proving both authorization and antiforgery behavior?