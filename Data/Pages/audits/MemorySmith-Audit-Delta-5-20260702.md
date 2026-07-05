# MemorySmith Code Audit — Delta Report #5 (2026-07-02, continued)

**Scope of this document:** deltas only, on top of reports #1–#4. This pass covered `MemorySmith.App/Services/MemoryApplicationService.cs` (1,461 lines) — the memory-record CRUD and search/ranking core — plus a follow-up trace into `MemorySmith.Core/Indexing/MemoryIndex.cs` that materially changes how urgent Report #1's H-3 finding actually is. Everything below is new except the explicit correction in §2.

---

## Headline deltas

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **Validation errors get silently clobbered under the wrong field key.** `ValidateRecord` writes any "too many tags" error to `errors["Tags"]`, then unconditionally overwrites that same dictionary entry with governance-diagnostic errors (which can be about References, Conflicts, duplicate detection — anything) if any exist. If both conditions are true at once, the tags-count error is silently discarded and the caller never sees it. | 🔴 New | **95%** |
| 2 | **Correction/major context update to Report #1's H-3 finding:** `MemoryIndex`'s `ById`/`ByTag`/`ByReference` dictionaries — the ones with the unlocked-`Dictionary`/`Clear()`-then-refill race — have **zero production consumers anywhere in the repo**. Every read path uses `_store.LoadAll()`, confirmed again in this pass across every search/list/CRUD method in `MemoryApplicationService.cs`. The only other reference to `.ByTag`/`.ByReference` in the whole codebase is the index class's own isolated unit test. **The race is real, but nothing can currently observe its effects** — this is scaffolding for a feature that either hasn't been built yet or was already superseded before ever being wired up. | ⚪ Correction (major) | **90%** |
| 3 | **Latent case-sensitivity mismatch in `MemoryIndex`, currently inert per #2 but would break immediately if ever wired up.** Record IDs are explicitly allowed to contain uppercase (`^[A-Za-z0-9_-]+$`) and are stored case-preserved. But `NormalizeRecord` lowercases `References`/`Conflicts` via `NormalizeValues(...).ToLowerInvariant()` before they reach `MemoryIndex.Add`, while `MemoryIndex`'s dictionaries use no comparer (default case-sensitive `Ordinal`). A reference to a mixed-case ID like `"TSK-0042"` gets stored in `ByReference` under the key `"tsk-0042"` — a lookup using the record's actual `Id` would miss. The App-layer's own ad-hoc lookup maps (`MemoryRecordLookup.ToRecordMap`) correctly use `OrdinalIgnoreCase` for exactly this reason; `MemoryIndex` doesn't follow the same convention. | 🟡 New | **85%** |
| 4 | **`SearchAliases` is an arbitrary, asymmetric hand-maintained keyword-expansion table.** E.g. `"mcp"` expands to `["model","context","protocol","tool","tools","integration","json","rpc"]`, but none of `"tool"`, `"tools"`, `"integration"`, `"json"`, or `"rpc"` map back to `"mcp"` — so searching "tool" won't surface MCP-related content the way searching "mcp" surfaces tool-related content. Same pattern in the `"semantic"`/`"embedding"`/`"embeddings"` cluster (partially bidirectional) vs. `"testbase"`/`"friction"` (one-directional, and project-jargon-specific in a way that won't generalize). This isn't a crash bug — it's an unclear, hard-to-extend design choice that quietly makes search relevance depend on which of two synonymous words the user happens to type. | 🟢 New (design) | **80%** |

---

## 1. Governance-diagnostic errors overwrite the tag-count error under the same dictionary key

**Evidence — `ValidateRecord`:**
```csharp
if (record.Tags.Count > _options.Limits.MaxTags)
{
    errors[nameof(MemoryRecord.Tags)] = [$"At most {_options.Limits.MaxTags} tags are allowed."];
}
...
var governanceErrors = GetDiagnostics(record, MemoryRecordLookup.ToRecordMap(_store.LoadAll().Append(record)))
    .Where(diagnostic => string.Equals(diagnostic.Severity, "Error", StringComparison.OrdinalIgnoreCase))
    .Select(diagnostic => diagnostic.Message)
    .ToArray();
if (governanceErrors.Length > 0)
{
    errors[nameof(MemoryRecord.Tags)] = governanceErrors;   // ← same key, unconditional overwrite
}
```
`GetDiagnostics` delegates to `MemoryDiagnosticsService.Analyze`, which (based on its inputs — the record plus a full id-to-record map) is positioned to report on far more than tags: broken/dangling references, conflict-graph issues, duplicate-content detection, and whatever else that service checks. All of that gets bucketed under the `Tags` key in the returned `MemoryValidationException`'s error dictionary — and if a `MaxTags`-count error was already present, it's silently discarded, not merged.

**Concrete failure scenario:** a record with 21 tags (over a `MaxTags: 20` limit) that also references a nonexistent record ID. The caller (API response, or the Blazor edit form) sees only the governance error about the broken reference; the tag-count problem — which is arguably the easier, more obvious thing for the user to notice and fix themselves — vanishes entirely. If they fix the reference and resubmit, only then does the "too many tags" error appear, extending what should be a one-round-trip fix into two.

**Recommendation:** Use a key that actually reflects what the diagnostics can be about — either bucket governance errors under a distinct key (`"Governance"` or similar, matching the existing `nameof(MemoryRecord.SourceLinks)`-style per-concern keying already used elsewhere in this same method), or better, have `MemoryDiagnosticsService.Analyze`'s output carry its own field association per diagnostic (it may already know whether a given diagnostic is about a reference vs. a tag vs. something else) and route each into the matching key. At minimum, append to an existing array rather than assign-and-overwrite:
```csharp
if (governanceErrors.Length > 0)
{
    errors["Governance"] = governanceErrors;
}
```
is a one-line fix that stops the clobbering even without the more thorough per-field routing.

**Confidence: 95%** — the overwrite is unambiguous from the code as written; the only reason it's not higher is I haven't independently confirmed that `MemoryDiagnosticsService.Analyze` ever actually returns an `Error`-severity diagnostic unrelated to tags in practice (I didn't read that service this pass) — if it turns out every current diagnostic *is* tags-related, the bug is real but currently unobservable, similar in spirit to Finding 2 below. Worth a quick check of `MemoryDiagnosticsService.cs` to confirm which severity levels/subjects it actually emits today.

---

## 2. Correction to Report #1's H-3: `MemoryIndex` has no production readers at all

Report #1 characterized the `MemoryIndex` concurrency race as "write-path-only... severity escalates to CRITICAL if search is ever promoted to consult the index," having confirmed at the time that `MemoryApplicationService`'s search methods use `_store.LoadAll()`. This pass went further and checked **every** other place `.ById`, `.ByTag`, or `.ByReference` (the three dictionaries the race affects) are referenced, repo-wide:

```
$ grep -rn "\.ByReference\b|\.ByTag\b|\.ById\b" --include=*.cs .   (excluding MemoryIndex.cs itself)
→ zero results in production code
→ one result in MemorySmith.Tests/MemoryMaintenanceTasksTests.cs, testing MemoryIndex.Add() in isolation
```
Nothing outside `MemoryIndex` itself reads from these dictionaries. `_index.Add`/`_index.Remove`/`_index.Rebuild` are called on every Create/Update/Delete (maintaining the index), but the maintained data has no consumer today.

**Why this matters:** it doesn't make H-3 not-worth-fixing — the race condition is still real, and "nothing reads it yet" is exactly the kind of thing that changes the moment someone builds the feature this index so clearly exists to support (fast tag/reference lookups without an O(n) full-store scan). But it does mean:
1. The concurrency race currently has **zero observable correctness impact** on any user-facing behavior — worth knowing before spending urgency budget on it relative to findings that do have current impact (like §1 above, or Report #3's rate limiter).
2. This is also, in its own right, an "unclear/arbitrary" finding of the kind you asked about: there's a fully-built, tested-in-isolation, kept-perpetually-in-sync-on-every-write indexing subsystem sitting entirely unused. Either it's intended for near-term work (in which case, fine, but worth confirming it's still on a roadmap somewhere — I didn't find a task referencing it directly) or it's speculative infrastructure that's been carried along without anything depending on it, which is worth a conscious decision either way rather than just continuing to maintain it by inertia.

**Recommendation:** Two independent, non-conflicting paths: (a) still fix the underlying `Dictionary` → `ConcurrentDictionary` / atomic-swap issue from Report #1, since it's a cheap, contained fix regardless of current usage, and this is exactly the kind of latent bug that's cheapest to fix before it has a real consumer to break; (b) separately, decide whether `MemoryIndex` should be built out into an actual fast-lookup path (worth it if tag/reference-based queries become a performance concern at scale) or removed/simplified until it's needed (avoiding the maintenance cost of keeping three dictionaries in sync on every write for no current benefit).

**Confidence: 90%** — the "zero consumers" claim is an exhaustive repo-wide grep result, about as solid as static analysis gets; the residual uncertainty is whether some consumer exists via reflection, a DI-resolved strategy pattern, or similar indirection my grep wouldn't catch (I found no evidence of this, but can't prove a negative for indirect invocation).

---

## 3. Latent case-sensitivity mismatch (currently inert, would activate immediately if #2 changes)

**Evidence chain:**
```csharp
// SafeIdRegex — mixed-case IDs are explicitly valid:
[GeneratedRegex("^[A-Za-z0-9_-]+$")]
private static partial Regex SafeIdRegex();

// NormalizeRecord — forces References/Conflicts (but not Id) to lowercase:
record.References = NormalizeValues(record.References);   // NormalizeValues does .ToLowerInvariant()

// MemoryIndex — no comparer specified, defaults to case-sensitive Ordinal:
public Dictionary<string, HashSet<string>> ByReference { get; } = new();
```
If Record A has `Id = "TSK-0042"` and Record B has `References = ["TSK-0042"]`, after `NormalizeRecord(B)` runs, `B.References` becomes `["tsk-0042"]`. `MemoryIndex.Add(B)` then stores this under `ByReference["tsk-0042"]`. Any future lookup using Record A's actual `Id` (`"TSK-0042"`) against `ByReference` would miss, because the dictionary has no `OrdinalIgnoreCase` comparer.

**Contrast with the rest of the codebase's own convention:** `MemoryRecordLookup.ToRecordMap` (the App-layer's own id-to-record map, used throughout `MemoryApplicationService.cs`) explicitly uses `new Dictionary<string, MemoryRecord>(StringComparer.OrdinalIgnoreCase)` — the codebase clearly knows this needs to be case-insensitive elsewhere. `MemoryIndex` (in `MemorySmith.Core`) just doesn't follow that same convention.

**Why this is worth fixing now rather than "when it matters":** per Finding 2, this is currently inert — but it's also a one-line fix (`new Dictionary<string, MemoryRecord>(StringComparer.OrdinalIgnoreCase)` for all three dictionaries in `MemoryIndex`), and fixing it now, while it's cheap and risk-free, is strictly better than fixing it later under time pressure once something actually depends on it and starts silently missing mixed-case-ID backlinks in production.

**Recommendation:** Add `StringComparer.OrdinalIgnoreCase` to all three `MemoryIndex` dictionary declarations. Bundle with the `ConcurrentDictionary` fix from Report #1 §1.4 — the constructor for `ConcurrentDictionary<string, T>` also accepts an `IEqualityComparer<string>`, so both fixes land in the same lines.

**Confidence: 85%** — the mechanism is directly verified; the "would definitely bite in practice" framing depends on the currently-unconfirmed assumption that a real-world memory record would actually be given a mixed-case ID rather than everyone conventionally using lowercase slugs — the regex permits it, but I don't have evidence of how record IDs are actually chosen in practice (auto-generated GUIDs are lowercase; human/LLM-chosen slugs could go either way).

---

## 4. `SearchAliases`: an arbitrary, one-off, partially-bidirectional synonym table

**Evidence:**
```csharp
private static readonly Dictionary<string, string[]> SearchAliases = new(StringComparer.OrdinalIgnoreCase)
{
    ["mcp"] = ["model", "context", "protocol", "tool", "tools", "integration", "json", "rpc"],
    ["model"] = ["mcp"],
    ["context"] = ["mcp"],
    ["protocol"] = ["mcp"],
    ["semantic"] = ["meaning", "concept", "conceptual", "embedding", "embeddings", "vector", "similarity"],
    ["embedding"] = ["semantic", "vector", "similarity"],
    ["embeddings"] = ["semantic", "vector", "similarity"],
    ["search"] = ["find", "query", "lookup", "retrieval", "retrieve"],
    ["wiki"] = ["knowledge", "memory", "memories", "docs", "documentation"],
    ["testbase"] = ["fixture", "fixtures", "test", "tests", "validation", "temp"],
    ["friction"] = ["missing", "issue", "issues", "gap", "pain", "blocker"]
};
```
Used by `ExpandSearchTokens` to widen semantic/hybrid search token sets. Observations:
- `"mcp"` → 8 terms including `"tool"`/`"tools"`/`"integration"`/`"json"`/`"rpc"`, but none of those 5 map back to `"mcp"` — searching "tool" doesn't surface MCP content the way searching "mcp" surfaces tool content, even though the intent (help the two vocabularies find each other) is presumably symmetric.
- `"search"` → `["find","query","lookup","retrieval","retrieve"]`, but none of those individual words have a reverse entry pointing back to `"search"` or to each other — so "query" and "lookup" don't expand toward each other despite both being listed as synonyms of the same third term.
- `"testbase"` and `"friction"` read as specific to this project's own internal vocabulary/jargon (a "Testbase" concept and a "friction log" concept, presumably), which is fine as a targeted improvement but means this table is really "project-specific search tuning notes that happen to live in code" rather than a general synonym system — worth knowing if anyone else ever needs to extend it without already knowing the project's internal terminology.

**Not a bug, but the "unclear/arbitrary" design smell you asked about:** there's no test, comment, or doc explaining the selection criteria (why these 6 concept clusters and not others; why some are made bidirectional and others aren't). Whoever adds the next alias entry has no guidance on whether it should be one-directional or fully cross-linked, and the current table doesn't consistently pick one policy.

**Recommendation:** Not urgent, but worth a short comment block explaining the intended symmetry policy (if any), and — if full bidirectionality is actually the goal — a small helper that expands a directional table into its symmetric closure at startup, rather than hand-maintaining both directions and having them silently drift apart (as `"mcp"`'s already has).

**Confidence: 80%** — the asymmetry is a direct textual observation; whether it was ever *intended* to be fully bidirectional (vs. a deliberate one-directional design, e.g. "narrow acronym expands to broad terms, but broad terms shouldn't over-trigger on the narrow acronym") is a judgment call I can't resolve from the code alone — flagged as a design question, not asserted as definitely wrong.

---

## 5. Things checked in this file and ruled out (for transparency)

- **Reciprocal Rank Fusion scoring (`ReciprocalRankScore`, `ToRankMap`)** — initially suspected a rank-0/"not found" mishandling that would inflate scores for lexical-only-miss records; confirmed the code explicitly guards `rank <= 0 ? 0` and ranks are correctly 1-indexed. No bug.
- **`RankSemanticResults` null-handling of the optional `_semanticEmbeddings` service** — correctly falls back to token-based scoring (`RankTokenSemanticResults`) when the service is null or `TryRank` returns false. No bug.
- **`BuildHighlightedSnippetHtml` (Lucene-highlighted search snippets rendered as raw HTML in the Blazor UI)** — initially looked like a plausible stored-XSS vector (user-controlled memory content → `<mark>`-wrapped HTML → rendered via `MarkupString`). Confirmed the content is explicitly `WebUtility.HtmlEncode`'d *before* the highlighter ever touches it, with an inline comment explaining exactly this reasoning. Well-defended; no issue.
- **`CreateAsync`/`UpdateAsync`/`DeleteAsync` write-then-index-update sequencing** — confirmed this is where the Report #1 H-3 race actually manifests (every single write), but per Finding 2 above, currently has no consumer that could observe corruption.

---

## 6. Coverage note

This completes a full line-by-line read of `MemoryApplicationService.cs` (1,461 lines) — the third of the five ">1,200 line" files flagged as outstanding after Report #2, plus a worthwhile follow-up trace into `MemorySmith.Core/Indexing/MemoryIndex.cs`. Remaining from that original list: `TaskDomainService.cs`, `CodeSearchService.cs` (partially covered across Reports #1–#2), and the Razor component layer.
