# Copilot Instructions

## Project Guidelines
- User prefers NUnit over xUnit for test projects.

## Knowledge Hub — Where to Find Things

All project knowledge is organized under `MemorySmith.Core/Docs/`.

### Docs Directory Map

| Path | Purpose |
|---|---|
| `Docs/Memories/` | **Active knowledge base.** Distilled, up-to-date facts about architecture, decisions, and current state. Start here for context. |
| `Docs/Unconsolidated/` | **Unconsolidated memories inbox.** Raw knowledge fragments (gotchas, fixes, lessons) land here before being distilled. Periodic "dreaming" sessions consolidate them into `Docs/Memories/`. Do not treat as authoritative — these are raw material. |
| `Docs/Plans/` | Architecture and implementation plans. `InitialPlan.md` is the canonical blueprint (mirrored at `Plans/InitialPlan.md` repo root). |
| `Docs/Reviews/` | External and automated review reports against the plans (e.g. deep-research critiques). Cross-reference with codebase before trusting. |
| `Docs/ProgressReports/` | Snapshot reports of implementation progress per phase. |

### Key Files to Read First
- `Docs/Memories/` — current distilled knowledge (check this before any planning session)
- `Docs/Plans/InitialPlan.md` — canonical architecture blueprint

### Reviewing Plans and Progress
When reviewing plans or progress reports, cross-reference with the actual codebase to verify that the documented state matches reality.
Plans may be aspirational and not yet fully implemented, while progress reports may be snapshots that have since evolved.
Always check the latest code for the true source of truth.
Always include any relevant code file paths in your notes.
**Always note any assumptions and open questions that arise during review to facilitate discussion.**
Give confidence levels where appropriate.