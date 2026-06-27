# Sprint Plan: Branding, Memory Graphs, Task UX — 2026-06-27

**Author:** Agent Smith  
**Date:** 2026-06-27  
**Status:** Proposed  
**Confidence:** 85% (well-scoped, implementation surface is moderate)

---

## Evidence Reviewed

| Source | What It Told Us |
|--------|-----------------|
| Task Audit Council (2026-06-27) | Task store is clean (191 records, 0 duplicates, CI-validated). Backlog triaged. Ready for new work. |
| DashboardPlan_Review (2026-04-23) | Dashboard plan is ~65% stale. Real code diverged from docs. No branding layer exists. |
| Sprint 49 Wave C (2026-06-24) | Dashboard Wave 1 shipped: log sink wired, publisher fixed, REST endpoints, static HTML dashboard. |
| Sprint 48 (2026-06-24) | Audit corrections shipped: bot name detection, max distance blocks, SmeltableMapping. |
| `MemorySmith.App/Components/Layout/MainLayout.razor` | "MemorySmith" hardcoded in app bar (line 15). |
| `MemorySmith.App/Components/Pages/Home.razor` | "MemorySmith" hardcoded in PageTitle (line 5) and heading (line 7). |
| `MemorySmith.App/Components/Pages/Tasks.razor` | "MemorySmith" hardcoded in PageTitle (line 11). Quick-create pane (lines 85-117) shows Title+Slug+Description in vertical stack — no left/right split, no severity/tags fields. |
| `MemorySmith.Core/Models/MemoryRecord.cs` | `References` and `Conflicts` are `List<string>` — flat string IDs, no direction, no relationship type, no metadata. |
| `MemorySmith.Core/Models/MemoryMetadata.cs` | Lightweight list-view model — no graph fields. |
| `MemorySmith.App/Components/TopicMapVisualization.razor` | Simple circular SVG layout, hardcoded 32-node limit, static `TopicMapDocument` input — no interactive graph. |
| TSK-0268 | "Add reverse-reference view for memories and pages" — Backlog, Medium. |
| TSK-0287 | "Add task↔page linking and assignee filtering to MCP tools" — Backlog, Medium. |
| `MemorySmith.App/Services/TaskDomainService.cs` | `TaskCreateRequest` already has `Priority`, `Labels`, `Title`, `Description` fields. Quick-create just doesn't expose priority/labels. |
| `MemorySmith.App/Program.cs` | `MemorySmithOptions` bound from `"MemorySmith"` config section. No `InstanceName`/`Branding` property exists. |
| `MemorySmith.App/appsettings.json` | No branding settings exist. |

---

## Sprint Goals

### Goal 1: Configurable Branding (P1)
Replace hardcoded "MemorySmith" strings across the dashboard UI with a single `InstanceName` configuration value, so the user can label each project instance independently.

### Goal 2: Memory Graph with Cross-Linking (P1)
Evolve the `MemoryRecord.References`/`Conflicts` flat lists into typed graph edges, add reverse-reference lookup, and surface the graph in the dashboard with an interactive visualization.

### Goal 3: Task Quick-Create UX Redesign (P2)
Redesign the quick-create pane on `/tasks` to a two-column layout: left = (name, severity, tags), right = full-height description textbox. Remove slug from quick-create (it's noise for the 80% case).

---

## Implementation Breakdown

### Wave A: Branding (est. 1–1.5 hrs)

**A1. Add `InstanceName` to `MemorySmithOptions`**
- File: `MemorySmith.Core/Models/MemorySmithOptions.cs` (or wherever the options class lives)
- Add `public string InstanceName { get; set; } = "MemorySmith";` with default
- Add to `appsettings.json` under `"MemorySmith"` section:
  ```json
  "InstanceName": "MemorySmith"
  ```

**A2. Replace hardcoded "MemorySmith" strings**
- `MainLayout.razor` line 15: `<MudText Typo="Typo.h6" Class="ml-3">@_instanceName</MudText>`
- `Home.razor` line 5: `<PageTitle>@_instanceName</PageTitle>`, line 7: `<MudText ...>@_instanceName</MudText>`
- `Tasks.razor` line 11: `<PageTitle>Tasks - @_instanceName</PageTitle>`
- `MemoryViewer.razor`: `<PageTitle>Memories - @_instanceName</PageTitle>`
- Audit all `.razor` files for literal "MemorySmith" in PageTitle/headings
- Inject `IOptionsMonitor<MemorySmithOptions>` where needed

**A3. Add browser tab title via layout**
- Update `MainLayout.razor` to set a configurable `<PageTitle>` at layout level
- Or add a cascading parameter for instance name

### Wave B: Memory Graph (est. 2.5–3 hrs)

**B1. Extend `MemoryRecord` with typed cross-references**
- Add `CrossReference` model:
  ```csharp
  public sealed record CrossReference(
      string TargetId,         // ID of the target memory
      string RelationType,     // "supersedes", "contradicts", "supports", "derives-from", "see-also"
      string? Note,            // optional context
      DateTime CreatedAtUtc
  );
  ```
- Add `public List<CrossReference> CrossReferences { get; set; } = new();` to `MemoryRecord`
- Deprecate (but keep for backward compat) flat `References` and `Conflicts` lists
- Add migration: on load, if `CrossReferences` is empty but `References`/`Conflicts` have values, auto-populate with `"see-also"`/`"contradicts"` relation types

**B2. Add reverse-reference lookup API**
- New endpoint: `GET /api/memories/{id}/references?direction=in|out|both`
- Returns `CrossReferenceResult` with resolved target titles
- Add to `MemoryApplicationService` and `MemoriesController`

**B3. Build interactive graph visualization**
- Replace `TopicMapVisualization.razor`'s static circular layout with a force-directed or hierarchical graph
- Use a Blazor-compatible graph library or custom SVG/Canvas rendering
- Support: click-to-navigate, hover tooltips with memory title+status, edge type coloring
- Wire into the memory detail view as a "References" tab panel

**B4. Update `MemoryMetadata` for list-view graph hints**
- Add `int CrossReferenceCount` for quick "has links" indicator

### Wave C: Task Quick-Create UX (est. 1–1.5 hrs)

**C1. Redesign quick-create pane layout**
- Replace the current vertical 3-field stack (Title, Slug, Description) with a 2-column grid:
  - **Left column** (narrower): Title, Severity dropdown, Tags text field
  - **Right column** (fills remaining width): Description `MudTextField` with `Lines="8"` (or auto-height)
- Remove Slug field from quick-create (it auto-generates from title anyway)
- The `CreateQuickTaskAsync` method already accepts `Priority` and `Labels` — just wire the new fields

**C2. Update `CreateQuickTaskAsync`**
- Pass `_createPriority` (from severity dropdown, default Medium) instead of hardcoded `TaskPriorities.Medium`
- Pass `_createLabels` (comma-separated) instead of hardcoded `["future"]`
- If labels are empty, default to `["future"]` (keep backward compat)

**C3. CSS polish**
- Add responsive grid classes for the two-column split
- Ensure the description textbox fills available vertical space
- On narrow viewports, stack vertically

---

## Task Breakdown (to be created)

| # | Task | Wave | Priority | Est. |
|---|------|------|----------|------|
| 1 | Add `InstanceName` to `MemorySmithOptions` + config | A | High | 0.5h |
| 2 | Replace hardcoded "MemorySmith" across all `.razor` files | A | High | 0.5h |
| 3 | Add `CrossReference` model + extend `MemoryRecord` | B | High | 0.5h |
| 4 | Add reverse-reference lookup API endpoint | B | High | 0.5h |
| 5 | Build interactive memory graph visualization | B | Medium | 1.5h |
| 6 | Redesign task quick-create pane (2-column layout) | C | Medium | 1.0h |
| 7 | Wire severity + tags into quick-create submission | C | Medium | 0.5h |

---

## Acceptance Criteria

| # | Criterion | How to Verify |
|---|-----------|---------------|
| AC1 | Setting `InstanceName: "MyProject"` in `appsettings.json` changes the app bar title, home page heading, all `<PageTitle>` values | Visual inspection across Home, Tasks, Memories, Layout |
| AC2 | Default `InstanceName` is `"MemorySmith"` (no config change needed for existing users) | Run with no `InstanceName` set, verify "MemorySmith" appears |
| AC3 | `MemoryRecord` has `CrossReferences` list with typed `CrossReference` records | Schema inspection, JSON round-trip test |
| AC4 | Reverse-reference endpoint returns incoming and outgoing references with resolved titles | `GET /api/memories/{id}/references?direction=both` returns correct data |
| AC5 | Memory graph visualization shows nodes with directional, color-coded edges | Visual inspection on a memory with cross-references |
| AC6 | Task quick-create has left column (Title, Severity, Tags) and right column (full Description) | Visual inspection of `/tasks` quick-create pane |
| AC7 | Creating a task with "High" severity and "bug,ui" tags actually sets those values | Inspect created task JSON |

---

## Out of Scope

- Full graph database migration (stay with file-backed JSON + in-memory index)
- Real-time collaborative graph editing
- Graph export/import
- Changing the task detail edit view (only quick-create is in scope)
- Changing `NavMenu` branding (that's already "Navigation" — generic enough)

---

## Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| `MemorySmithOptions` class location unknown | Low | Search codebase; if it doesn't exist as a POCO, create under `MemorySmith.Core/Models/` |
| Graph visualization library selection | Medium | Start with custom SVG (no dependency). If complexity grows, evaluate Blazor.Diagrams or similar MIT-licensed lib. |
| `CrossReference` backward compat with existing `References`/`Conflicts` | Medium | Auto-migrate on load. Never delete old fields. Write migration tests. |
| PageTitle in Blazor Server may not cascade from layout | Low | Use `HeadOutlet` or inject options into each page. Fall back to cascading parameter. |

---

## Related Tasks (existing)

- TSK-0268: "Add reverse-reference view for memories and pages" (Backlog, Medium) — **this sprint implements it**
- TSK-0287: "Add task↔page linking and assignee filtering to MCP tools" (Backlog, Medium) — **partially addressed; full MCP tool work is out of scope**
- TSK-0169: "Chat Context Dashboard" / "Length-based Chat Eviction" (Backlog) — unrelated
