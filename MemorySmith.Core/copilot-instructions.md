# Copilot Instructions

## Project Guidelines
- User prefers NUnit over xUnit for test projects.
- MVVM with minimal code-behind for UI components.
- Treat `MemorySmith.App` as the active single-host app. `MemorySmith.Worker` and `MemorySmith.Dashboard` are migration history unless explicitly requested.
- Preserve `Data/Memories` as live project wiki content and test fixture source. Tests should copy it before mutation.

## Knowledge Hub - Where to Find Things

Project knowledge is split between `README.md`, the structured wiki under `Data/Memories`, markdown pages under `Data/Pages`, and supporting docs under `MemorySmith.Core/Docs/`.

### Docs Directory Map

| Path | Purpose |
|---|---|
| `../Data/Memories/Core/` | Active structured project wiki records. Read/search these before planning substantial work. |
| `../Data/Memories/Working/` | In-progress structured memories. Verify before treating as authoritative. |
| `../Data/Memories/Unconsolidated/` | Raw memory inbox. Do not treat as authoritative without verification. |
| `../Data/Pages/` | Markdown-backed project wiki pages and longer-form notes. |
| `Docs/Plans/` | Architecture and implementation plans. `MemorySmith_FinalRefactorDesign_20260507.md` is the current broad refactor blueprint. |
| `Docs/Reviews/` | Review/audit reports. Useful, but verify against code because older reports can be stale. |
| `Docs/ProgressReports/` | Historical progress snapshots. Verify against current code before relying on them. |
| `Docs/Prompts/` | Prompts used by the app, including the wiki chat/agent prompt. |

### Key Files to Read First
- `../README.md` - current product shape, routes, configuration, validation commands.
- `../Data/Memories/Core/` - current structured project knowledge.
- `Docs/Plans/MemorySmith_FinalRefactorDesign_20260507.md` - active broad architecture plan.
- `Docs/Plans/SemantingSearch.md` - current semantic search/vector update plan when working on semantic retrieval.

### Reviewing Plans and Progress
When reviewing plans or progress reports, cross-reference with the actual codebase to verify that the documented state matches reality.
Plans may be aspirational and not yet fully implemented, while progress reports may be snapshots that have since evolved.
Always check the latest code for the true source of truth.
Always include any relevant code file paths in your notes.
Always note assumptions and open questions that arise during review to facilitate discussion.
Give confidence levels where appropriate.