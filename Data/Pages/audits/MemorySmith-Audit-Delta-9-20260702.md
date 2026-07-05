# MemorySmith Code Audit — Delta Report #9 (2026-07-02, continued)

**Scope of this document:** deltas only, on top of reports #1–#8. This pass covered `MemorySmith.App/Services/TaskDomainService.cs` (1,267 lines) — the task-board domain service backing the Data/Tasks board this whole audit has been cross-referencing since Report #1. Two new findings.

---

## Headline deltas

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **`Status`, `Priority`, and `Type` on a task have zero membership validation against their own defined constant sets** (`TaskStatuses`, `TaskPriorities`) — `NormalizeOrDefault` only trims/null-checks. A task created or updated with a typo'd or arbitrary status (via the REST/MCP API, not just the UI) silently disappears from **every** status-filtered Kanban-column view — confirmed by reading `ListAsync`'s filter logic, which does an exact `OrdinalIgnoreCase` match against whatever string is passed in, with no fallback bucket for unrecognized values. The task still exists and is reachable via unfiltered list or direct key lookup, so it's data-loss-adjacent (undiscoverable, not deleted) rather than actual loss. | 🟡 New | **88%** |
| 2 | **TOCTOU race in task-attachment file naming** — `TaskAttachmentFiles.GetUniqueFileName` checks `File.Exists` in a loop to find a non-colliding name, then the caller does `File.Create(path)` afterward with a gap in between and no lock protecting this specific path. Two concurrent attachment uploads with the same original filename to the same task can both pass the existence check for the same "unique" candidate name, and `File.Create`'s default `FileMode.Create` semantics silently truncate/overwrite rather than throw — one upload's content is silently discarded. Notably, this file I/O happens **outside** `TaskDomainService`'s own `_gate` lock (which correctly serializes everything else in this file), so the one concurrency protection this file otherwise consistently uses doesn't reach this specific path. | 🟢 New | **80%** |

---

## 1. Unvalidated `Status`/`Priority`/`Type` — a task can silently vanish from every Kanban column

**Evidence — the only normalization applied:**
```csharp
private static string NormalizeOrDefault(string? value, string fallback)
{
    var normalized = NormalizeNullable(value);   // trim / null-if-whitespace, nothing else
    return normalized ?? fallback;
}
...
Status: NormalizeOrDefault(request.Status, TaskStatuses.Backlog),
Priority: NormalizeOrDefault(request.Priority, TaskPriorities.Medium),
```
`SetStatusAsync` uses the same helper with no additional check:
```csharp
var status = NormalizeOrDefault(request.Status, item.Status);
```
Neither `TaskStatuses` nor `TaskPriorities` has an `All`/allow-list `HashSet` at all (contrast with Report #4's finding about `MaintenanceProposalStatuses.All` existing-but-unused — here, the equivalent validation infrastructure was never even built for this domain).

**Traced consequence — `ListAsync`'s filter:**
```csharp
.Where(item => string.IsNullOrWhiteSpace(normalizedStatus) || string.Equals(item.Status, normalizedStatus, StringComparison.OrdinalIgnoreCase))
```
This is a plain exact-match filter with no "else, bucket as Unknown" fallback. A Kanban-style board UI that renders one column per known status (Backlog/Ready/InProgress/Blocked/Rejected/Done/Archived) by calling `ListAsync(status: "Backlog")`, `ListAsync(status: "Ready")`, etc. for each column would never surface a task whose `Status` is anything outside those seven exact strings — it matches zero of the column queries. The task remains fully intact in storage (confirmed: `ListAsync` with no status filter, or `GetAsync` by ID/key, both still return it) — so this is "invisible in the primary navigation view," not "deleted," but for a task-tracking tool, invisible-in-the-board-view is functionally close to lost for anyone who only ever browses by column.

**How this could actually happen in practice:** this service backs an MCP tool surface (per the project's own architecture, mirrored by the `Data/Tasks/*.json` records this audit has referenced throughout) — meaning an LLM agent, not just a human typing into a form, can call `SetStatusAsync`/`CreateAsync` with a `Status` string it generates. A model hallucinating `"Cancelled"` instead of the actual `Rejected` constant, or `"In Progress"` (with a space) instead of `InProgress`, or `"done"` in a context where case sensitivity was assumed to matter when it doesn't (it doesn't — `OrdinalIgnoreCase` — so that specific case is actually fine, but any other lexical variation isn't) — any of these would silently produce a task that's technically saved successfully (no error is thrown anywhere in this path) but effectively orphaned from every column view.

**Recommendation:** Add `TaskStatuses.All`/`TaskPriorities.All` `HashSet<string>(StringComparer.OrdinalIgnoreCase)` sets (mirroring the pattern that already exists, just unused, for `MaintenanceProposalStatuses` per Report #4), and validate in `NormalizeOrDefault`'s call sites for these specific fields — either reject the request with a clear error naming the valid options, or fall back to the default/previous value with a warning, rather than silently accepting anything non-empty. Given this is API/tool-surface input (not just UI-constrained dropdown input), server-side validation is the only real backstop here — a UI dropdown wouldn't protect against a direct API/MCP call.

**Confidence: 88%** — the lack of validation and the exact-match filter behavior are both directly read from source; the "silently vanishes from Kanban board" framing depends on the UI actually querying per-status (I didn't trace the specific Blazor task-board component's query pattern this pass to 100% confirm it queries column-by-column rather than fetching all tasks and bucketing client-side — if it's the latter, the practical impact shifts from "invisible" to "shown in an unlabeled/miscategorized column," which is a better failure mode than full invisibility but still a symptom of the same root validation gap).

---

## 2. TOCTOU race in attachment file naming, outside the file's own locking discipline

**Evidence:**
```csharp
private static string GetUniqueFileName(string directory, string fileName)
{
    var baseName = Path.GetFileNameWithoutExtension(fileName);
    var extension = Path.GetExtension(fileName);
    var candidate = fileName;
    var index = 2;
    while (File.Exists(Path.Combine(directory, candidate)))   // ← check
    {
        candidate = $"{baseName}-{index}{extension}";
        index++;
    }
    return candidate;
}
```
```csharp
// SaveAsync, called after GetUniqueFileName:
var uniqueFileName = GetUniqueFileName(directory, safeFileName);
var path = Path.Combine(directory, uniqueFileName);
await using var target = File.Create(path);   // ← use, with a gap since the check above
await source.CopyToAsync(target, cancellationToken);
```
`File.Create` uses `FileMode.Create`, which silently overwrites an existing file rather than throwing if one appears between the check and the create. Two concurrent `SaveAsync` calls for the same `taskId` with the same original `fileName` can both observe "candidate doesn't exist yet" for the same name before either has created it, then both proceed to write to the identical path — the second write completes last and wins, and the first upload's bytes are gone with no error surfaced to either caller.

**Why this isn't caught by the service's own locking:** `TaskDomainService` consistently wraps all its state mutations in `lock (_gate)` (confirmed across `SetStatusAsync`, `AddAttachmentAsync`, and every other mutating method read this pass) — a reasonable, simple concurrency model for an in-memory-plus-file-backed domain service. But `TaskAttachmentFiles.SaveAsync` is a `static` method, called from a Controller action (the actual file upload) *before* the resulting URI is ever passed to `AddAttachmentAsync`, so it never executes inside `_gate` — and even if it did, `lock` blocks can't contain `await` in C#, so the file I/O couldn't be inside that same lock without a larger refactor (e.g., an async-compatible `SemaphoreSlim` instead of `lock`).

**Realistic likelihood:** this requires two uploads with the *same original filename* to the *same task* landing close enough in time to both pass the `File.Exists` check before either finishes writing — plausible if, say, a script or an agent automation uploads several files with a repeated/templated name in a tight loop, less likely for a single human clicking upload buttons one at a time. Rated as a real but narrower-window bug than Finding 1.

**Recommendation:** Replace the check-then-create pattern with an atomic-create attempt: try `File.Open(path, FileMode.CreateNew)` (which throws `IOException` if the file already exists, rather than silently succeeding) in a retry loop that increments the suffix on collision, instead of pre-checking `File.Exists` and hoping nothing else claims the name in between.

**Confidence: 80%** — the race window and `FileMode.Create`'s overwrite-on-exists semantics are both textbook and directly verified from the code; the discount reflects that I haven't measured how often two attachment uploads with an identical filename to the same task would realistically occur in this tool's actual usage pattern, which affects how much this matters in practice versus in principle.

---

## 3. Coverage note

This completes a full read of `TaskDomainService.cs` (1,267 lines) — closing out the last of the five ">1,200 line" files originally flagged as outstanding after Report #2, aside from the still-partial `CodeSearchService.cs` (~2,000 of 3,115 lines unread). Remaining candidates for continued depth: those `CodeSearchService.cs` lines, the Python/PowerShell training harness scripts (`MemorySmith.Training`, `*.ps1` release/deployment scripts), and the Blazor `TaskBoard`/`Tasks`-related Razor component(s) if one exists, which would let me directly confirm or refute the "per-column query" assumption underlying Finding 1's practical severity.
