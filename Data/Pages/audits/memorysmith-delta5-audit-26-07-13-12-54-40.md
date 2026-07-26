# MemorySmith Audit — Delta Report 5 (Continued Deep Dive)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` · **Commit:** `e8a3065` (confirmed unchanged — re-checked branch HEAD before this pass)
**Report generated:** 2026-07-13
**Relationship to prior reports:** new findings only, numbered F28+ continuing from the CodeSearchService deep dive (F23–F27).

**This pass covered:** full read of `MemorySmith.App/Services/TreeSitterChunkingService.cs` (260 lines, in full), the parser-strategy dispatch chain in `CodeSearchService.cs` (`BuildParsedChunks`, `TryBuildRoslynChunks`, `TryBuildTreeSitterChunks`, `NormalizeParserStrategyOrder`), and a structural pass over `ChatToolCatalog.cs` (1,603 lines — header/records/constructor read in full, all ~24 tool-name registrations checked for collisions, `MergeShardAllowedExtensions` cross-referenced against its counterpart in `CodeSearchService.cs`).

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F28 | `TreeSitterChunkingService` declares full C# grammar support (extension mapping + 8 chunkable node types) that is permanently unreachable — the sole caller explicitly skips `.cs` files before ever invoking it, and separately, Roslyn's error-tolerant parser makes the "fallback" scenario this dead code implies almost never occur in practice anyway | 85% | Low (misleading dead config, not a coverage gap) | **New** |
| F27-addendum | The `.db`/`.sqlite`/`.sqlite3` extension allowlist for shard-merge validation is hardcoded independently in **two** places — `ChatToolCatalog.MergeShardAllowedExtensions` and inline inside `CodeSearchService.MergeShardAsync` itself | 90% | Low (small DRY gap, same area as F27) | **Extends F27's recommended fix**, not a standalone task |

**Also checked and ruled out this pass (noted for completeness, not findings):** tool-name collisions across all ~24 `ChatToolDescriptor` registrations in `ChatToolCatalog.BuildTools()` (`ToDictionary` would throw at startup on a duplicate — none found, and the fail-fast behavior here is worth calling out as *good* defensive design, not a gap); whether `memorysmith_agent_invoke` already has `AvailableInAgent=true` set per TSK-0276's item #3 (the tool descriptor doesn't exist in the catalog yet at all, consistent with TSK-0276 still being Backlog — not a new finding, just confirms the task's current state accurately).

---

## F28 — Dead, misleading C# grammar support in `TreeSitterChunkingService` (Low, 85%)

**Files:** `MemorySmith.App/Services/TreeSitterChunkingService.cs` (declares the support) and `MemorySmith.App/Services/CodeSearchService.cs` (the only caller, which disables it).

`TreeSitterChunkingService.ExtensionToLanguage` maps `.cs → "CharSharp"` (line 50, with an inline comment: *"We use Roslyn for .cs, but make it available"*), and `ChunkableTypes["CSharp"]` lists 8 chunkable node types (`class_declaration`, `struct_declaration`, `interface_declaration`, `enum_declaration`, `method_declaration`, `property_declaration`, `field_declaration`, `record_declaration`). Read at face value, this looks like a genuine fallback: if Roslyn-based chunking fails for a `.cs` file, tree-sitter's AST-based chunker should be able to step in.

**Traced the actual call chain and found it can't happen:**
1. `CodeSearchService.BuildParsedChunks` walks `_options.ParserStrategyOrder` (default `["roslyn", "treesitter", "heuristic", "fixedwindow"]`) and calls `TryBuildRoslynChunks` first for a `.cs` file.
2. If that returns `false`, the loop proceeds to `case "treesitter": TryBuildTreeSitterChunks(...)`.
3. But `TryBuildTreeSitterChunks` (lines 1075-1076) explicitly short-circuits: `// .cs files are already handled by Roslyn; skip tree-sitter for them` followed by an early `return false` for any path ending in `.cs`. **`TreeSitterChunkingService.TryChunk` is never invoked for a `.cs` file, full stop, regardless of whether Roslyn succeeded or failed.**
4. Checked `TryBuildRoslynChunks`'s failure modes to see whether this even matters in practice: `CSharpSyntaxTree.ParseText` is Roslyn's standard error-tolerant parser — it does not throw on malformed/invalid C# syntax (it produces a tree with diagnostic errors attached, which this code doesn't even inspect) — so the only realistic ways `TryBuildRoslynChunks` returns `false` are the feature flag being off, the file not ending in `.cs`, or the file having zero top-level members (e.g., an empty file). There is no realistic "Roslyn choked on valid/near-valid C#" scenario this dead branch would have caught anyway.

**Net effect:** the `.cs`/`"CSharp"` entries in `TreeSitterChunkingService` are unreachable in production, confirmed via the only call site, and even if the skip in step 3 were removed, the scenario it's meant to guard against (Roslyn failing on real C# source) essentially doesn't occur given Roslyn's parser tolerance. This isn't a coverage gap being silently papered over — it's inert configuration that reads as intentional design (a real fallback) when it's actually neither reachable nor, on inspection, particularly necessary.

**Recommendation:** low priority, but a clean, safe removal — delete the `.cs`/`"CSharp"` entries from `TreeSitterChunkingService.ExtensionToLanguage` and `ChunkableTypes`, and replace the `// We use Roslyn for .cs, but make it available` comment with a one-line note in `CodeSearchService.TryBuildTreeSitterChunks` explaining that C# is Roslyn-only by design (matching this project's own "eliminate legacy/compat paths" philosophy — dead-but-plausible-looking config is exactly the kind of thing that costs a future engineer time verifying it's safe to touch). If there's ever an actual desire for tree-sitter as a genuine last-resort C# fallback (e.g., for `.cs` files so large or unusual that Roslyn's zero-members case fires), that's a small, separate, deliberate change — not something to leave lying around implied but unreachable.
**Effort:** 30 minutes; zero behavior change, purely deletes unreachable code and clarifies a comment.

---

## F27-addendum — Shard-merge extension allowlist duplicated in two files

While cross-referencing `ChatToolCatalog.cs` against the `MergeShardAsync` finding from the CodeSearchService report: `ChatToolCatalog.cs` line 76 declares
```csharp
private static readonly string[] MergeShardAllowedExtensions = [".db", ".sqlite", ".sqlite3"];
```
and `CodeSearchService.MergeShardAsync` independently hardcodes the identical check inline (verified in the prior report's F27 investigation, lines ~1715-1717). Same three extensions, same purpose, written twice. This is a small addition to F27's existing recommendation (extract path/extension validation into a shared, service-level check) rather than a new standalone task — when `MergeShardAsync` gains its own root-containment enforcement per F27's recommendation, its extension check should also become the single source of truth, with `ChatToolCatalog`'s copy either removed or reduced to calling the same shared constant, so a future third sqlite-extension variant only needs updating in one place.
**Effort:** folded into F27's existing 3-4 hour estimate; no additional standalone work.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- F28's claim that Roslyn "essentially never" fails on real C# source is based on `CSharpSyntaxTree.ParseText`'s documented error-tolerant behavior and the specific failure conditions visible in `TryBuildRoslynChunks`'s code (no diagnostic-severity check, no explicit throw path beyond a generic `catch (Exception ex)` that Roslyn's API surface for `ParseText` doesn't appear to exercise for malformed-but-well-formed-UTF8 input) — this is a reasoned inference from the code and Roslyn's known API contract, not something empirically fuzz-tested in this pass.
- Did not complete a full line-by-line read of `ChatToolCatalog.cs` (1,603 lines) in this pass — covered its structural surface (records, constructor, all tool-name registrations for collisions) but not each individual tool handler's implementation body. That remains open scope for a future pass if a full-file review of this specific file is wanted next.
