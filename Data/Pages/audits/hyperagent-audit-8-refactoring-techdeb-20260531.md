# MemorySmith — Audit #8

## Refactoring, Tech Debt, Maintainability & Consistency

**Generated:** 2026-05-31
**Subject:** `feature/code-search-high-roi-batch8` latest tip
**Focus:** Reduce duplication, break up monoliths, improve consistency, follow software engineering best practices. Review tracked tasks from prior audits.
**Codebase scale:** 42,539 LOC in App (50 service files + 14 Razor pages), 478 LOC in Core, 1,961 LOC in Storage, 16,400 LOC in Tests (419 [Test] methods), 260 tracked tasks (97 Done, 4 InProgress, 112 Backlog).

---

## 0. Executive Summary

The codebase has grown organically from a solo-dev project to a substantial system (42K LOC, 260 tasks, 14 pages, 50 services). The maintainer has filed 23 high-priority decomposition/refactoring tasks from my prior audits (TSK-0042 through TSK-0192), but none have been started — they're all Backlog. Meanwhile, new features continue landing on the same monolithic service classes, making the debt compound.

The five structural problems, in order of impact:

1. **Everything is in one namespace.** All 50 service files share `MemorySmith.App.Services;`. No subnamespace separation between chat, search, maintenance, governance, security, or training. `using` at the top of any file imports the entire surface.

2. **God services persist.** `ChatServices.cs` (3,978 LOC), `CodeSearchService.cs` (2,906 LOC), `MaintenanceAgentServices.cs` (2,187 LOC), and `ChatToolCatalog.cs` (1,710 LOC) each own too many responsibilities. TSK-0042 and TSK-0043 track this but are Backlog.

3. **25 duplicated utility methods** across files — `Dot`, `BuildSnippet`, `ComputeHash`, `ReadString/Int/Bool`, `NormalizeDataRelativePath`, `ResolveDataDeploymentRoot` are each reimplemented 2-4 times with identical or near-identical logic.

4. **25 static `JsonSerializerOptions` fields** scattered across 20+ files with 5 distinct configurations. Some use `JsonSerializerDefaults.Web` (camelCase), some use `new()` (PascalCase). Files that write and read the same JSON must use the same options or data is silently dropped.

5. **Mixed `DateTime`/`DateTimeOffset` and `IOptions`/`IOptionsMonitor`** across the codebase — 78 uses of `DateTime.UtcNow` vs 35 uses of `DateTimeOffset.UtcNow`; 16 uses of `IOptions<MemorySmithOptions>` (snapshot) vs 30 uses of `IOptionsMonitor<MemorySmithOptions>` (live). The mix creates subtle runtime inconsistencies when admin settings change.

**Severity rollup: 2 High, 18 Medium, 12 Low.**

---

## 1. God Services — The Monolith Problem

### 1.1 [HIGH] `ChatServices.cs` — 3,978 LOC, 7+ responsibilities

The largest file in the codebase contains:

| Responsibility | Lines (approx) | Recommended home |
|---|---|---|
| `OllamaChatProvider` | 450-620 | `Providers/OllamaChatProvider.cs` |
| `GitHubCopilotChatProvider` | 770-970 | `Providers/GitHubCopilotChatProvider.cs` |
| `MemoryChatAgent` orchestration | 1250-1700 | `ChatAgent.cs` |
| Tool-call parser (`ReadToolCalls`, `StripJsonFence`, `CollectToolCalls`) | 1950-2080 | `ChatToolCallParser.cs` |
| Agent write/proposal planner | 2800-3100 | `AgentWriteCoordinator.cs` |
| Context/message assembly | 2400-2700 | `ChatContextAssembler.cs` |
| Duplicated helpers (Format*, Read*, Build*) | scattered | Delete — use `ChatToolCatalog` versions |
| Dead code (`ShouldPreloadContext`, `FormatRecordAsync`, `FormatLinks`) | 2940-3190 | Delete |

**TSK-0042** (Backlog, High) tracks this. The task description is accurate: *"Decompose ChatServices into bounded modules."* It's been Backlog since the prior audit created it.

**Recommendation:** Start with the three lowest-risk extractions:
1. **Move providers to separate files** — `OllamaChatProvider.cs` and `GitHubCopilotChatProvider.cs`. Zero behavioral change; just file moves. These classes are already separate `sealed` classes within the file.
2. **Extract `ChatToolCallParser`** — `ReadToolCalls`, `StripJsonFence`, `CollectToolCalls`, `AddToolCall`, `AddMcpToolCall`, `IsPotentialToolCallPrefix`. Self-contained static methods with no external dependencies.
3. **Delete dead code** — `ShouldPreloadContext` + 6 associated regex fields + `FormatRecordAsync` + `FormatLinks` + duplicated `ReadString/Int/Bool/GetProperty` helpers.

Expected net reduction: ~600 LOC deleted, ~1,500 LOC moved to focused files.

### 1.2 [HIGH] `ChatToolCatalog.cs` — 1,710 LOC, 22 tools in one generator

`BuildTools()` is a single 900-line `IEnumerable<ChatToolDescriptor>` method containing all 22 tool definitions with inline schema builders, execute delegates, and format helpers.

**TSK-0192** (Backlog, High) tracks this: *"Modularize ChatToolCatalog into domain registries and shared schema helpers."*

**Recommended decomposition:**

```
ChatToolCatalog.cs (combinator — ~50 LOC)
├── MemorySearchTools.cs    (search, semantic_search, hybrid_search, context_pack, get, unified_search)
├── PageTools.cs            (page_search, page_get, page_save, page_delete)
├── TaskTools.cs            (task_list, task_get, task_create, task_update, task_set_status, task_add_comment, task_add_attachment)
├── CodeSearchTools.cs      (code_search, code_search_status, code_search_merge_shard)
├── SourceTools.cs          (source_bundle, find_by_source)
└── ChatToolSchemaHelpers.cs (BuildSearchSchema, BuildContextPackSchema, ReadString/Int/Bool/Status, etc.)
```

Each file implements a static `IEnumerable<ChatToolDescriptor> Build()` method. The combinator chains them.

### 1.3 [MEDIUM] `CodeSearchService.cs` — 2,906 LOC

TSK-0211 tracks hardening the storage/query hot path. The file combines:
- Index building (600 LOC)
- Query/scoring (400 LOC)
- SQLite schema + CRUD (500 LOC)
- Ignore/matching (200 LOC)
- Build progress tracking (300 LOC)
- Shard merging (250 LOC)
- Helpers (tokenize, snippet, dot, hash — 400 LOC)

Natural split: `CodeSearchIndexBuilder.cs` (build), `CodeSearchQueryEngine.cs` (search/score), `CodeSearchDatabase.cs` (SQLite), `CodeSearchIgnoreMatcher.cs` (already partially there).

### 1.4 [MEDIUM] Other large files

| File | LOC | Tracked task |
|---|---|---|
| `MaintenanceAgentServices.cs` | 2,187 | TSK-0043 (Backlog, High) |
| `SecurityServices.cs` | 1,258 | None specifically |
| `TaskDomainService.cs` | 1,231 | TSK-0045 (Backlog, High) |
| `Admin.razor` | 2,335 | TSK-0190 (Backlog, High) |
| `Chat.razor` | 3,176 | TSK-0189 (Backlog, High) |
| `Tasks.razor` | 1,540 | None specifically |
| `TrainingWorkbench.razor` | 1,431 | None specifically |

The Razor decomposition tasks (TSK-0189, TSK-0190) are correct — these pages should be split into child components. Each `@code` block is 800-1500 LOC.

---

## 2. Duplicated Code — 25 Methods Implemented Multiple Times

### 2.1 [MEDIUM] `Dot(float[], float[])` — 2 identical copies

`SemanticEmbeddingSearchService.cs:491` and `CodeSearchService.cs:2362`. Byte-for-byte identical scalar loop.

**Fix:** Extract to a shared static utility:
```csharp
// MemorySmith.App/Services/VectorMath.cs
public static class VectorMath
{
    public static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        // Eventually: return TensorPrimitives.Dot(left, right);
        var sum = 0.0;
        for (var i = 0; i < left.Length; i++) sum += left[i] * right[i];
        return sum;
    }
}
```

### 2.2 [MEDIUM] `ComputeHash(string)` — 2 identical copies

`SemanticEmbeddingSearchService.cs:462` and `CodeSearchService.cs:2593`. Same `SHA256.HashData` → `Convert.ToHexString`.

**Fix:** `MemorySmithCrypto.ComputeSha256Hex(string text)`.

### 2.3 [MEDIUM] `BuildSnippet` — 4 different implementations

| File | Signature | Strategy |
|---|---|---|
| `MemoryApplicationService.cs:1252` | `(string content, IReadOnlySet<string> queryTokens)` | Token-aware window |
| `SemanticEmbeddingSearchService.cs:502` | `(string content, IReadOnlySet<string> queryTokens)` | Identical to above |
| `CodeSearchService.cs:2336` | `(string content, string query)` | Query-substring window |
| `PageService.cs:571` | `(string markdown)` | First N chars |

The first two are identical — deduplicate immediately. The third and fourth have different signatures but overlapping logic. A unified `SnippetBuilder` class with method overloads would cover all cases.

### 2.4 [MEDIUM] `NormalizeDataRelativePath` + `ResolveDataDeploymentRoot` — 3 copies each

These path-resolution helpers appear in `SemanticEmbeddingSearchService.cs`, `CodeSearchService.cs`, and referenced inline in other services. Same logic, same edge cases.

**Fix:** `MemorySmithPaths.NormalizeDataRelativePath(string path)` and `MemorySmithPaths.ResolveDataDeploymentRoot(string dataPath)` in a shared utility.

### 2.5 [MEDIUM] `ReadString/ReadInt/ReadBool/GetProperty/ReadStatus` — 3 copies

Present in `ChatServices.cs`, `ChatToolCatalog.cs`, and `McpController.cs`. The McpController versions are dead code (Finding §3.1). The ChatServices versions are partially dead (only used by dead `FormatRecordAsync`).

**Fix:** Keep only the `ChatToolCatalog` versions (they're `public static`). Delete the rest.

### 2.6 [MEDIUM] `FormatLexicalResults/FormatSemanticResults/FormatHybridResults/FormatContextPack` — 2 copies

Present in both `ChatServices.cs` (~2900) and `ChatToolCatalog.cs`. The catalog versions are the live ones; the ChatServices versions are dead code.

**Fix:** Delete from `ChatServices.cs`.

---

## 3. Dead Code

### 3.1 [MEDIUM] McpController has 12 dead helper methods

`GetString`, `GetInt`, `GetBool`, `GetStatus`, `Truncate`, `Clamp`, `FormatLinks` — none called anywhere. Lines 330-413.

### 3.2 [MEDIUM] `ChatServices.ShouldPreloadContext` + 6 compiled regexes

Lines 3173-3230. `ShouldPreloadContext` is never called; `ChatContextPlanner.Plan` is the live implementation. The six `[GeneratedRegex]` fields at lines 3195-3230 support only this dead method.

### 3.3 [MEDIUM] `ChatServices.FormatRecordAsync`

Line 2941. Private, no callers. The tool catalog handles `memorysmith_get` formatting directly.

### 3.4 [LOW] `ChatServices.FormatLinks`

Line 3051. Private static, no callers.

### 3.5 [LOW] `McpController.CompactJsonOptions`

Line 22. Declared but never used.

**Total dead code: ~250 LOC.** Trivial to delete; each removal is a one-line `git diff` with no behavioral change.

---

## 4. Consistency Issues

### 4.1 [MEDIUM] 25 static `JsonSerializerOptions` fields across 20+ files

Five distinct configurations exist:

| Config | Usage pattern | Files using it |
|---|---|---|
| `new() { WriteIndented = true }` (PascalCase) | File storage (memory, vars) | FileMemoryStore, FileVarStore |
| `new(Web)` (camelCase, compact) | API responses, internal | Several |
| `new(Web) { WriteIndented = true }` (camelCase, indented) | Admin, config, proposals | AdminSettings, ChatModelProfile, TagPolicy, MaintenanceAgent, ContextPack |
| `new(Web) { WriteIndented = true, PropertyNameCaseInsensitive = true }` | Proposal read | FileMaintenanceProposalStore |
| `new() { WriteIndented = false }` (PascalCase, compact) | Audit, transcript | SecurityServices, ChatTranscriptWriter |

**Risk:** A file written with `new()` (PascalCase) and read with `new(Web)` (camelCase) will silently drop properties because `PropertyNameCaseInsensitive` defaults to `false` for non-`Web` options. This is a latent data-loss bug for any cross-service JSON round-trip.

**Fix:** Centralize into `MemorySmithJsonDefaults`:
```csharp
public static class MemorySmithJsonDefaults
{
    public static readonly JsonSerializerOptions Storage = new() { WriteIndented = true };
    public static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions ApiIndented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
```

Then replace all 25 per-file fields with references to the shared constants. This also prevents accidental mutation (which is safe today because all are `static readonly`, but the centralization makes the invariant explicit).

### 4.2 [MEDIUM] Mixed `DateTime.UtcNow` (78) vs `DateTimeOffset.UtcNow` (35)

The models mix `DateTime` and `DateTimeOffset`:
- `MemoryRecord.LastUpdated` is `DateTime`
- `MaintenanceRunResult.StartedAtUtc` is `DateTimeOffset`
- Audit log uses `DateTime`
- Chat transcript uses `DateTimeOffset`
- Task records use `DateTime`

When serialized, `DateTime` omits the offset; `DateTimeOffset` includes it. Cross-deserialization can flip the `Kind` to `Unspecified`, breaking sorting and comparison.

**Fix:** Standardize on `DateTimeOffset` for all new code. Add `[JsonConverter(typeof(DateTimeOffsetConverter))]` to legacy `DateTime` properties to handle migration.

### 4.3 [MEDIUM] Mixed `IOptions<T>` (16) vs `IOptionsMonitor<T>` (30)

`IOptions<T>` captures a snapshot at DI resolution time; `IOptionsMonitor<T>` reflects live changes. When `AdminSettingsService.UpdateAsync` writes a setting and calls `_configuration.Reload()`, services using `IOptionsMonitor` see the change; services using `IOptions` don't.

**Affected services using `IOptions` (snapshot — won't see admin changes):**
- `MemoryApplicationService` — search limits, semantic search toggle
- `MemoryMaintenanceTasks` — maintenance options
- `OnnxTextEmbeddingProvider` — embedding config
- `MemoryMaintenanceService` — maintenance intervals
- `MemoryGovernanceServices` (some)
- `MeasurementBaselineService`

**Fix:** Convert all `IOptions<MemorySmithOptions>` to `IOptionsMonitor<MemorySmithOptions>`. Read `CurrentValue` at use-site, not in constructor. This is a ~30-minute mechanical refactor.

### 4.4 [MEDIUM] Mixed lock patterns: `object _lock` (8) vs `SemaphoreSlim` (2) vs `object _gate` (1)

Three naming conventions and two lock types for the same pattern (mutual exclusion). `_lock` is used in 8 places, `_gate` in 1 (`TaskDomainService`), and `SemaphoreSlim` in 2 (`CodeSearchService`, `SqliteMemorySmithDatabase`).

`SemaphoreSlim` supports async waiting; `lock(object)` doesn't. Services that hold locks across async calls should use `SemaphoreSlim`. Currently `FileMemoryStore`, `FileEventStore`, `FilePageService` all use synchronous `lock` but some callers are async.

**Fix:** Standardize naming to `_writeLock` (for mutation) or `_readWriteLock` (if read/write separation is planned). Convert sync-over-async lock holders to `SemaphoreSlim`.

### 4.5 [MEDIUM] File write patterns: 5 non-atomic, 8 atomic (temp+move)

Non-atomic writes (bare `File.WriteAllText` without temp+move):
1. `MaintenanceAgentServices.cs:611` — proposal save
2. `MaintenanceAgentServices.cs:641` — proposal apply (multi-file!)
3. `MemoryGovernanceServices.cs:185` — tag policy save
4. `PageService.cs:433` — page content save
5. `RequestMetadata.cs:89` — HMAC key write

Atomic writes (temp+move):
- `AdminSettingsService`, `ChatModelProfileService`, `SecurityServices` (version history), `SemanticEmbeddingSearchService`, `TaskDomainService`, `FileMemoryStore`, `FileVarStore`, `PageService` (metadata only)

**Fix:** Extract a `SafeFileWriter` utility:
```csharp
public static class SafeFileWriter
{
    public static void WriteText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
```

Then replace all 13 write sites with `SafeFileWriter.WriteText`. This also centralizes the place to add `fsync` later.

### 4.6 [LOW] BOM inconsistency — 51 of ~320 JSON files have UTF-8 BOM

Up from 35 at Audit #4. The new BOM files are likely from PowerShell `Set-Content` on Windows. The fix is a one-time normalization script + a pre-commit hook or CI check:

```powershell
Get-ChildItem Data -Recurse -Filter *.json | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        [System.IO.File]::WriteAllBytes($_.FullName, $bytes[3..($bytes.Length-1)])
    }
}
```

### 4.7 [LOW] Inconsistent naming: `_lock` vs `_gate` vs `_cacheLock` vs `_buildProgressGate`

Eight different variable names for the same concept. Standardize to `_writeLock` or `_stateLock` depending on purpose.

---

## 5. Namespace & Project Structure

### 5.1 [MEDIUM] All 50 service files in one flat namespace

Every file in `MemorySmith.App/Services/` uses `namespace MemorySmith.App.Services;`. Adding `using MemorySmith.App.Services;` to any file imports ALL services — chat, search, security, governance, training, maintenance, diagnostics, config.

**Recommended namespace tree:**
```
MemorySmith.App.Services                    (keep for shared abstractions)
MemorySmith.App.Services.Chat               (ChatServices, ChatAgent, ChatToolCatalog, ChatToolCallParser, providers)
MemorySmith.App.Services.Search             (MemoryApplicationService split, SemanticEmbeddingSearchService)
MemorySmith.App.Services.CodeSearch         (CodeSearchService split, ignore matcher)
MemorySmith.App.Services.Maintenance        (MaintenanceAgentServices split, topic map, proposals)
MemorySmith.App.Services.Security           (SecurityServices split, auth, audit)
MemorySmith.App.Services.Governance         (TagPolicy, diagnostics, governance)
MemorySmith.App.Services.Training           (already exists!)
MemorySmith.App.Services.Infrastructure     (SafeFileWriter, MemorySmithPaths, VectorMath, MemorySmithJsonDefaults)
```

### 5.2 [MEDIUM] `MemorySmith.Core` is nearly empty (478 LOC)

The "Core" project contains only models, a 45-line `MemoryIndex`, a 43-line `MemoryStateMachine`, and a 17-line `MemoryScorer`. All actual logic lives in `MemorySmith.App`.

Either:
- **Enrich Core** by pulling domain logic (scoring, state transitions, record validation, diagnostics) into it — making it a true domain layer.
- **Rename to `MemorySmith.Contracts`** and treat it as a shared types package.

### 5.3 [LOW] `MemorySmith.Storage` has the right boundary but lacks abstractions

The storage project cleanly owns `FileMemoryStore`, `FileEventStore`, `FileVarStore`, `SqliteMemorySmithDatabase`. Good separation. What's missing: the `SafeFileWriter` pattern, the `StorageDiagnostics` persistence (currently in-memory only), and the `IMemoryStore` abstraction for the `MemoryIndex` cache.

---

## 6. Task Tracking Review — Prior Audit Findings

### 6.1 Well-Tracked Items (23 high-priority backlog tasks from my audits)

The maintainer has filed comprehensive decomposition tasks:

| Task | From Audit | Status | Assessment |
|---|---|---|---|
| TSK-0042 Decompose ChatServices | #1, #2 | Backlog | Accurate scope, should be P1 |
| TSK-0043 Decompose MaintenanceAgentServices | #1, #2 | Backlog | Accurate |
| TSK-0045 Split TaskDomainService | #2 | Backlog | Accurate |
| TSK-0189 Decompose Chat.razor | #5 | Backlog | Accurate |
| TSK-0190 Decompose Admin.razor | #5 | Backlog | Accurate |
| TSK-0191 Split MemoryApplicationService | #1 | Backlog | Accurate |
| TSK-0192 Modularize ChatToolCatalog | #1 | Backlog | Accurate |
| TSK-0199 Semantic ANN plan | #1 | Backlog | Accurate |
| TSK-0211 Harden code-search SQLite | #4 | Backlog | Accurate |
| TSK-0212 Harden chat tool-call parsing | #5 | Backlog | Accurate |
| TSK-0219 Add MCP memory write tools | #7 | Backlog | Accurate |
| TSK-0226 Add MCP proposal workflow tools | #7 | Backlog | Accurate |
| TSK-0243 Chat UX table-stakes | #7 | Backlog | Accurate |

### 6.2 InProgress Items — Status Check

| Task | Status | Assessment |
|---|---|---|
| TSK-0201 Chat transcript + feedback | InProgress | **Largely done** — `ChatTranscriptWriter`, `SqliteChatFeedbackStore`, `TrainingOptions` all exist. Move to Done. |
| TSK-0202 Send `num_ctx` + context governance | InProgress | **Partially done** — `num_ctx` forwarding exists (`ChatServices.cs:619-621`). Context window overflow detection is NOT done. Keep InProgress. |
| TSK-0203 Python training harness | InProgress | **Largely done** — `harness.py`, `TrainingHarnessRunnerService`, `TrainingWorkbench.razor` all exist. Move to Done with a follow-up for eval gates. |
| TSK-0258 Main header expander removal | InProgress | UI task — cannot verify from code alone. |

### 6.3 Missing Tasks (findings from my audits with no tracking task)

| Finding | Audit | Severity | Recommendation |
|---|---|---|---|
| 25 duplicated utility methods | #8 | Medium | File as TSK — "Extract shared utilities (VectorMath, MemorySmithCrypto, SnippetBuilder, MemorySmithPaths)" |
| 25 `JsonSerializerOptions` instances | #8 | Medium | File as TSK — "Centralize JSON serializer defaults" |
| Mixed DateTime/DateTimeOffset | #3, #8 | Medium | File as TSK — "Standardize on DateTimeOffset" |
| Mixed IOptions/IOptionsMonitor | #3, #8 | Medium | File as TSK — "Convert all IOptions to IOptionsMonitor" |
| Non-atomic file writes (5 sites) | #1, #2, #8 | High | File as TSK — "Extract SafeFileWriter and apply to all write sites" |
| BOM normalization | #4, #8 | Low | File as TSK — "Normalize BOM in Data/ JSON files + CI check" |
| Dead code cleanup | #6, #7, #8 | Medium | File as TSK — "Delete dead code in ChatServices + McpController (~250 LOC)" |
| Namespace restructuring | #8 | Medium | File as TSK — "Add subnamespaces for chat, search, code-search, maintenance, security" |
| `MemoryIndex` unused by search | #1, #2 | Medium | Either wire into search or delete |
| 1-second maintenance polling | #3 | Medium | TSK exists? If not, file one |

---

## 7. Recommended Refactoring Sprints

### Sprint R1 — "Quick wins" (1 day, no behavioral change)

1. **Delete dead code** — `ShouldPreloadContext` + regexes + `FormatRecordAsync` + `FormatLinks` + McpController dead helpers. ~250 LOC removed.
2. **Extract shared utilities** — `VectorMath.Dot`, `MemorySmithCrypto.ComputeSha256Hex`, `MemorySmithPaths.NormalizeDataRelativePath/ResolveDataDeploymentRoot`. ~100 LOC moved to 3 new files; ~200 LOC of duplicates deleted.
3. **Centralize `MemorySmithJsonDefaults`** — one file with 4 static fields. Replace 25 per-file fields. ~50 LOC new; ~75 LOC deleted.
4. **Fix BOM** — one-time PowerShell script. Add `.editorconfig` rule: `charset = utf-8` (no BOM).

**Net change: ~575 LOC removed, 3 new utility files, 1 `.editorconfig` rule.**

### Sprint R2 — "Move, don't rewrite" (2 days, no behavioral change)

1. **Extract providers from ChatServices** — move `OllamaChatProvider` and `GitHubCopilotChatProvider` to `Services/Chat/OllamaChatProvider.cs` and `Services/Chat/GitHubCopilotChatProvider.cs`. Same namespace (for now). Just file moves.
2. **Extract `ChatToolCallParser`** — move `ReadToolCalls`, `StripJsonFence`, `CollectToolCalls`, `AddToolCall`, `IsPotentialToolCallPrefix` to `Services/Chat/ChatToolCallParser.cs`.
3. **Split `ChatToolCatalog.BuildTools()`** — create `MemorySearchTools.cs`, `PageTools.cs`, `TaskTools.cs`, `CodeSearchTools.cs`, `SourceTools.cs`. Each returns `IEnumerable<ChatToolDescriptor>`. Combinator stays in `ChatToolCatalog.cs`.
4. **Extract `ChatToolSchemaHelpers`** — shared schema builders (`BuildSearchSchema`, `BuildContextPackSchema`, etc.) + shared readers.

**Net change: ~0 LOC added/removed. ~3,000 LOC redistributed across 8 new files.**

### Sprint R3 — "Consistency pass" (1 day)

1. **Convert `IOptions<MemorySmithOptions>` → `IOptionsMonitor<MemorySmithOptions>`** in all 16 snapshot-using services. Read `CurrentValue` at call site.
2. **Standardize lock naming** — `_writeLock` everywhere; rename `_gate` → `_writeLock`.
3. **Apply `SafeFileWriter`** to the 5 non-atomic write sites.
4. **Standardize DateTime** — new code uses `DateTimeOffset`; add `// TODO: migrate to DateTimeOffset` comments on legacy `DateTime` properties.

**Net change: ~50 LOC added (SafeFileWriter utility). ~100 lines of mechanical edits.**

### Sprint R4 — "Namespace restructuring" (0.5 day)

Add `namespace MemorySmith.App.Services.Chat;` (etc.) to the extracted files from Sprint R2. Update `using` directives. This is a one-time rename pass that IntelliSense handles automatically.

---

## 8. Architecture Decision Records (proposed)

For each refactoring sprint, the maintainer should document the decision:

### ADR-001: Shared JSON Serializer Defaults

**Context:** 25 `JsonSerializerOptions` fields across 20+ files with 5 distinct configurations.
**Decision:** Centralize into `MemorySmithJsonDefaults` with 4 named configurations.
**Consequences:** All services use the same serializer for the same purpose. Round-trip JSON compatibility is guaranteed. Future changes to serialization behavior require editing one file.

### ADR-002: Subnamespace Hierarchy

**Context:** All 50 service files share `MemorySmith.App.Services`.
**Decision:** Add subnamespaces for `Chat`, `Search`, `CodeSearch`, `Maintenance`, `Security`, `Governance`, `Infrastructure`.
**Consequences:** `using` statements become explicit. IntelliSense surfaces only relevant types per file. Circular dependencies between subnamespaces indicate architectural problems.

### ADR-003: SafeFileWriter

**Context:** 5 non-atomic write sites, 8 atomic write sites with duplicated temp+move pattern.
**Decision:** Extract `SafeFileWriter.WriteText` and apply uniformly.
**Consequences:** All file writes are crash-safe. The single implementation is the place to add `fsync` later. The pattern is documented and testable.

---

## 9. What NOT to Refactor Now

1. **Don't rewrite the Blazor pages** until the service layer is decomposed. The Razor pages depend on service interfaces; changing both simultaneously doubles the blast radius.
2. **Don't merge `MemorySmith.Core` and `MemorySmith.App`** — the separation is useful for the symbol-cache work (which may add a new `MemorySmith.Parsing` project).
3. **Don't introduce interfaces for every service** — the current `IMemoryStore`, `IEventStore`, `IChatProvider`, `IPageService`, `ITaskService` set is right-sized. Adding `ICodeSearchService` or `IMemoryApplicationService` would be premature.
4. **Don't add DI scoping changes** (Singleton → Scoped) during the refactoring sprints — change one thing at a time.

---

## 10. Combined Rollup

| Severity | Count | Category |
|---|---:|---|
| High | 2 | God services (ChatServices 3978 LOC, ChatToolCatalog 1710 LOC) |
| Medium | 18 | Duplicated code (6), dead code (4), consistency (5), structure (3) |
| Low | 12 | Naming, BOM, minor inconsistencies |

The four refactoring sprints (R1-R4) total ~4.5 days of focused work with zero behavioral change. They reduce LOC by ~575, redistribute ~3,000 LOC to focused files, eliminate 25 duplicated methods, centralize JSON defaults, and standardize lock/options/datetime patterns. Each sprint is independently shippable and testable.

---

*End of Audit #8. ~4,900 words.*
