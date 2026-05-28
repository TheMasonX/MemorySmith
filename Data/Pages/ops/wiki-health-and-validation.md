# Wiki Health and Validation

This runbook explains how to verify that the live MemorySmith wiki is healthy enough for humans, chat, MCP tools, and maintenance workflows.

The wiki has three active content layers:

| Layer | Stored in | Used by |
| --- | --- | --- |
| Structured memories | `Data/Memories` | `/memories`, search, MCP, context packs, chat retrieval, tests |
| Markdown pages | `Data/Pages` | `/pages`, combined search, static docs, human-readable runbooks |
| Task records | `Data/Tasks` | `/tasks`, sprint plans, implementation tracking |

## Fast Validation Path

From the repository root, the standard validation entrypoint is:

```powershell
.\Scripts\Validate-Repo.ps1
```

That command runs build and tests plus the wiki/task validators already wired into the repository workflow.

## Focused Validation Commands

| Command | What it checks |
| --- | --- |
| `.\Scripts\Test-TaskRecords.ps1` | Task JSON parseability, filename/id consistency, required fields, recognized statuses, unique ids and keys |
| `.\Scripts\Test-PageLinks.ps1` | Markdown page links across `Data/Pages` |
| `.\Scripts\Test-PagePathLiterals.ps1` | Path-literal hygiene for markdown links |
| `dotnet test MemorySmith.slnx -v minimal` | Runtime behavior, API contracts, UI/service logic, search quality, auth, and storage tests |

When the app is already running and a build output is locked, prefer stopping the app or using an absolute temporary output directory outside the repository. Avoid relative `BaseOutputPath` or `BaseIntermediateOutputPath` values under an SDK project, because generated `obj/*.cs` files can be picked up by default compile globs. `TSK-0154` tracks adding a guardrail for this.

## What The Current Validators Cover Well

- Broken markdown links in `Data/Pages`
- Invalid page path literal usage
- Task record parse and identity drift
- Application/service behavior under tests
- Many wiki-backed behaviors because tests copy the live data to temp storage and exercise the same code paths

## What Still Needs Better Coverage

Current gap: there is still no dedicated first-class validator command focused only on the live `Data/Memories` corpus for required fields, filename/id invariants, and other whole-corpus memory contract checks.

That gap is already tracked in `TSK-0003`.

Practical consequence: malformed memory records may still be discovered first through runtime behavior, MCP retrieval, or targeted tests instead of a single whole-corpus validator.

## Runtime Health Checks

Use these runtime surfaces after changing wiki content or configuration:

| Surface | Best use |
| --- | --- |
| `/health` | Readiness, storage path status, maintenance state, semantic provider state |
| `/api/diagnostics` | Effective config, configured URLs, storage status, warnings, source-link roots, telemetry visibility |
| `/memories` | Retrieval sanity for structured records |
| `/pages` | Markdown rendering, page search, page navigation |
| `/chat` | Retrieval behavior and evidence quality |
| `/tags` | Tag policy diagnostics and suggestions |

## Tag Diagnostics And Policy Drift

Tag diagnostics are helpful but should be treated as evidence, not as unquestionable truth.

Interpretation guidance:

- `Info` usually means observed policy drift or suggestions.
- `Warning` means something deserves review before relying on the record heavily.
- `Error` means a save-blocking rule under the current policy mode.

When diagnostics look wrong:

1. Check `Data/Policies/tag-policy.json` on disk.
2. Check the `/tags` policy and suggestion views.
3. Re-test through app/runtime surfaces instead of assuming the stored page or memory is wrong.
4. If retrieval results still report allowlisted tags as unknown, treat that as a freshness or policy-mismatch issue worth investigation.

Known audit gap: the runtime currently preserves a default tag policy fallback for missing or malformed policy files. That is useful for first-run resilience, but policy-load failures need clearer diagnostics so a corrupt file cannot look like an intentional default. This is tracked by `TSK-0150`.

## Source-Link Health

Source links are part of wiki health because retrieval quality depends on them.

Check these when source-grounded workflows behave strangely:

- `MemorySmith:VarsPath` and `/variables`
- allowed and denied source roots
- `MaxReadBytes`
- line context expansion settings
- `/api/diagnostics` source-link root visibility

## Change Review Checklist

Use this checklist after documentation or memory changes:

1. Run `Test-PageLinks.ps1` and `Test-PagePathLiterals.ps1` for page changes.
2. Run `Test-TaskRecords.ps1` if task records changed.
3. Run targeted or full `dotnet test` when runtime-linked wiki behavior changed.
4. Spot-check `/health`, `/memories`, `/pages`, and `/chat` when the local app is running.
5. Record the evidence in a task, tracker, or current-state memory instead of relying on recall.

## Agent Assistance Notes

- Start with the smallest validator that matches the changed wiki surface.
- Treat page-link and task-record validators as cheap gating checks.
- Treat live `Data/Memories` whole-corpus validation as an open improvement area, not a solved problem.
- When an agent reports wiki drift, ask whether the problem is bad content, stale index state, stale policy state, or missing runtime validation.

## Related Pages

- [Operations](operations.md)
- [Configuration Reference](../guides/configuration-reference.md)
- [MemorySmith Pages](../guides/memorysmith.md)