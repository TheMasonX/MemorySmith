# MemorySmith Code Audit — Delta Report #2 (2026-07-02, same-day follow-up)

**Scope of this document:** *deltas only* — new findings, corrections, and ruled-out candidates from a continued deep-dive since the first report (`MemorySmith-Audit-20260702.md`). Read that report for methodology, the still-open P0/P1 security items, and general findings — not repeated here.
**This pass's focus:** full line-by-line read of `ChatServices.cs` (3,736 lines) and `TreeSitterChunkingService.cs` (260 lines); targeted deep reads of `CodeSearchService.cs` SQL layer and `SemanticEmbeddingSearchService.cs` scoring/caching logic; cross-repo reference-counting to detect dead code.

---

## Headline deltas

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **10 confirmed-dead private methods in `ChatServices.cs`** (~230 lines), each one an orphaned pre-decomposition implementation with a live, separately-maintained duplicate now doing the real work in `ChatToolCatalog.cs` / `ChatContextPlanner.cs`. This is a *pattern*, not one bug — every piece extracted out of `ChatServices.cs` so far has left its old copy behind instead of deleting it. | 🔴 New, major | **97%** |
| 2 | **`TreeSitterChunkingService`'s C# entry is unreachable** due to a key-format mismatch (`"CSharp"` vs. `"c_sharp"`), and the broken path is reachable in production: on any Roslyn parse failure for a `.cs` file, the *default* strategy order falls through to TreeSitter, silently downgrading from declaration-level chunking to generic top-level-node chunking with no log distinguishing the two. | 🔴 New | **88%** |
| 3 | **`SplitThinking` only strips the first `<think>...</think>` block** (`Regex.Match`, not all matches) — a model that emits more than one thinking segment per turn will leak raw `<think>` tags into the visible reply from the second occurrence onward. | 🟡 New | **80%** |
| 4 | **`GitHubCopilotChatProvider.ListModelsAsync` silently swallows any exception** when `GitHubModels` is configured (`catch when (chatOptions.GitHubModels.Count > 0)`), with zero logging — the only place in this file's error handling where a failure produces no diagnostic trail at all. | 🟡 New | **90%** |
| 5 | **`ResolveStreamIdleTimeout` is a byte-for-byte duplicate** between `OllamaChatProvider` and `GitHubCopilotChatProvider`; the Ollama `CompleteAsync`/`StreamAsync` pair also duplicates ~15 lines of endpoint/payload/tool-building logic between them. | 🟢 New (consolidation) | **95%** |
| 6 | **Correction/upgrade:** TSK-0202 (`num_ctx` governance) is now **verified true** — `BuildOllamaRequestOptions` genuinely wires `OllamaContextWindowTokens` → `num_ctx` on both the complete and streaming payloads. Previous report flagged this as "unverified, 40% confidence" — raise to **92%**. | ⚪ Correction | **92%** |
| 7 | **Non-finding (checked and ruled out):** semantic-search cosine similarity is implemented correctly — both query and document embeddings are unit-normalized (`Normalize()`) before the raw dot product is taken, so the score is mathematically valid cosine similarity, not magnitude-biased. Flagging this only because it's the kind of thing that's *often* buggy and I want you to know it was checked, not assumed. | ⚪ Ruled out | **93%** |
| 8 | **Non-finding (checked and ruled out):** the CodeSearchService SQL layer's apparent string-interpolated queries are safe — interpolation is used only for generated parameter *names* (`@token0`, `@token1`, ...) with values passed through `AddWithValue`, and the one raw-interpolated `ALTER TABLE ADD COLUMN` call site takes only hardcoded, non-configurable column names. No injection path found. | ⚪ Ruled out | **90%** |
| 9 | **Minor hardening gap:** `SemanticEmbeddingSearchService.Dot(float[], float[])` iterates using `left.Length` with no check that `right.Length` matches, so a real dimension mismatch would throw `IndexOutOfRangeException` rather than fail gracefully. In practice this looks unreachable — every call site validates `embedding.Length == expectedLength` before calling `Dot`, so this is defense-in-depth, not a live bug. | 🟢 New (low severity) | **75%** |

---

## 1. The `ChatServices.cs` dead-code cluster (highest-value finding this pass)

**Method:** for every `private` method in the file, count in-file references; for any with ≤1 reference (i.e., only its own declaration), search the *entire* repo (all `.cs`/`.razor`) for other callers before concluding it's dead. This caught several false positives (methods legitimately called from the `MemoryChatAgent.ToolLoop.cs` partial-class file) which were correctly ruled out — see the "ruled out" list below for transparency.

**Confirmed dead** (zero callers anywhere in the repo, including tests) — each with its live replacement:

| Dead method (ChatServices.cs) | Line | Live replacement |
|---|---|---|
| `ShouldPreloadContext` (+ its 6 private `[GeneratedRegex]` fields: `ExactReplyRegex`, `SimpleNoContextRegex`, `LocalKnowledgeRegex`, `AgentWriteCommandRegex`, `EvidenceSeekingRegex`, `AgentContextRegex`) | 2913–2960 | `ChatContextPlanner.Plan()` in `ChatContextPlanner.cs` — which re-declares **byte-for-byte identical** copies of all 6 regex patterns rather than sharing them |
| `FormatRecordAsync` | 2681 | `ChatToolCatalog.cs:213`, inline `memorysmith_get` executor |
| `ReadLexicalQuery` | 2695 | `ChatToolCatalog.ReadLexicalQuery` (public, line 1291) |
| `ReadSemanticQuery` | 2701 | `ChatToolCatalog.ReadSemanticQuery` (public, line 1297) |
| `ReadHybridQuery` | 2707 | `ChatToolCatalog.ReadHybridQuery` (public, line 1303) |
| `ReadContextPackQuery` | 2713 | `ChatToolCatalog.ReadContextPackQuery` (public, line 1309) |
| `FormatLexicalResults` | 2724 | `ChatToolCatalog.FormatLexicalResults` (public, line 1517) |
| `FormatSemanticResults` | 2735 | `ChatToolCatalog.FormatSemanticResults` (public, line 1524) |
| `FormatHybridResults` | 2746 | `ChatToolCatalog.FormatHybridResults` (public, line 1531) |
| `FormatContextPack` | 2757 | `ChatToolCatalog.FormatContextPack` (public, line 1538) |

**Verification evidence (example, `ReadHybridQuery`):**
```
./MemorySmith.App/Services/ChatServices.cs:2707:    private static HybridMemorySearchQuery ReadHybridQuery(JsonObject arguments) => new(   ← dead
./MemorySmith.App/Services/ChatToolCatalog.cs:170:   var results = await ctx.Memories.HybridSearchAsync(ReadHybridQuery(args), ct);         ← live call site
./MemorySmith.App/Services/ChatToolCatalog.cs:1303:  public static HybridMemorySearchQuery ReadHybridQuery(JsonObject arguments) => new(     ← live definition
```
Same shape confirmed individually for all 10 entries in the table above — no false positives.

**Why this matters more than "unused code":**
1. **It's a process signal, not an isolated bug.** Every one of these 10 methods was made obsolete by the *same* refactor (extracting tool execution and context-planning out of the monolithic `ChatServices.cs`/`MemoryChatAgent` into `ChatToolCatalog`/`ChatContextPlanner`). None were deleted. If TSK-0042 Step 2 (the planned full file split) proceeds without an explicit "delete superseded code" checklist item, this will keep happening to whatever's extracted next.
2. **It actively misleads a reader (human or LLM).** Anyone — or any future audit, including an AI agent working from this file — reading `ChatServices.cs` top-to-bottom will reasonably conclude `ShouldPreloadContext`'s regex-based gating logic is what decides whether context gets preloaded. It isn't; `ChatContextPlanner.Plan()` is, and it has slightly *more* logic than the dead copy (tool-recommendation routing that references TSK-0271's search-tool removal — see its code comment at `ChatContextPlanner.cs:78`). The dead copy is a stale, confusing decoy sitting right next to the code that still matters.
3. **It's a template for finding more.** I did not have budget this pass to run the same reference-count sweep against `CodeSearchService.cs`, `MaintenanceAgentServices.cs`, `TaskDomainService.cs`, or `SecurityServices.cs` at the same level of manual verification I applied to `ChatServices.cs` (I ran the automated heuristic — see §4 below — and it returned zero candidates for those files, which is a reasonably strong negative signal, but `ChatServices.cs`'s decomposition-in-progress status makes it a special case worth re-checking after each future extraction).

**Recommendation:** Delete all 10 methods and the 6 regex fields. This is almost risk-free — the compiler will immediately flag it if I'm wrong about any reference (I'm not, per the verification above), and no test references these private symbols directly (tests exercise behavior through `ChatToolCatalog`/`ChatContextPlanner`'s public surface). Suggest folding this into TSK-0042 as an explicit, separately-committable sub-step ("Step 1.5: delete superseded private members") rather than waiting for Step 2's full file split — it's zero-risk cleanup that doesn't need to wait on the harder structural work.

**Confidence: 97%** (every claim here is a direct grep/read result, not an inference; the only reason it's not ~99% is that I did not execute a full build to have the compiler confirm zero remaining references — a `dotnet build` after deletion would be the actual proof).

---

## 2. TreeSitter C# chunking key mismatch (new, live-reachable)

**Evidence:**
```csharp
// ExtensionToLanguage:
[".cs"] = "CSharp",   // We use Roslyn for .cs, but make it available

// ChunkableTypes (StringComparer.OrdinalIgnoreCase):
["c_sharp"] = [ "class_declaration", "struct_declaration", ... ]
```
`ChunkableTypes.TryGetValue(languageName, ...)` is looked up with `languageName = "CSharp"` (from `ExtensionToLanguage`). `"CSharp"` and `"c_sharp"` differ by more than case (an underscore), so `StringComparer.OrdinalIgnoreCase` does **not** consider them equal. The C# entry in `ChunkableTypes` can never be reached by any input derived from `ExtensionToLanguage`.

**Why this isn't just cosmetic:** `CodeSearchService.BuildParsedChunks` walks a configurable strategy order, **defaulting to `["roslyn", "treesitter", "heuristic", "fixedwindow"]`** (confirmed in `MemorySmithOptions.cs:221`). On any `.cs` file where Roslyn chunking fails or yields nothing (partial/invalid syntax mid-edit, an unsupported construct, or any other Roslyn edge case), execution falls through to `TryBuildTreeSitterChunks`. Because of the key mismatch, the "has a specialized declaration list for this language" branch (`hasChunkableType`) misses, and the code silently takes the generic "no language-specific rules — chunk all top-level named children" path instead of the properly-scoped class/method/property boundaries the `c_sharp` entry was clearly meant to provide. There is no log line distinguishing "used the C#-specific rules" from "used the generic fallback," so this degradation would be invisible in normal operation — you'd just get worse-than-intended chunk boundaries for whatever `.cs` file triggered the Roslyn failure, with no signal that anything went wrong.

**Recommendation:** Either rename the dictionary key from `"c_sharp"` to `"CSharp"` (trivial, one-line fix, restores the intended specialized chunking as a Roslyn-failure fallback), or — given the code comment already says "we use Roslyn for .cs" — remove the `.cs`/`"CSharp"` entries from `TreeSitterChunkingService` entirely if C#-via-TreeSitter was never meant to be a real fallback path, to stop the dead configuration from implying a capability that doesn't work. I'd lean toward the one-line rename: a working fallback is strictly better than either a broken one or none at all, and the fix is nearly free.

**Confidence: 88%** (the key mismatch itself is 100% certain from direct string comparison; the "reachable via default strategy order" claim is also directly verified from config defaults and the fallthrough loop; the residual uncertainty is about how *often* Roslyn actually fails on real `.cs` files in this codebase's usage pattern in practice, which I have no telemetry to measure — it's a real bug, but I can't tell you its real-world hit rate).

---

## 3. Chat provider layer: three smaller findings

### 3.1 `SplitThinking` drops only the first `<think>` block
```csharp
[GeneratedRegex("<think>(.*?)</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
private static partial Regex ThinkingPatternRegex();

private static (string Content, string? Thinking) SplitThinking(string content, string? thinking)
{
    var match = ThinkingPatternRegex().Match(content);   // ← single Match, not Matches
    ...
}
```
If a model emits two or more `<think>...</think>` segments in one response (plausible with reasoning models that "think" before each of several steps within a single generation), only the first is extracted into `Thinking`/stripped from the visible content — every subsequent occurrence stays in the user-visible reply verbatim, tags and all.
**Recommendation:** Use `Regex.Matches` (or a global replace with capture concatenation) to strip and aggregate all occurrences, not just the first.
**Confidence: 80%** (the code logic is directly confirmed; confidence isn't higher only because I don't have empirical confirmation that any of the operator's actual configured models emit multiple `<think>` blocks per turn — if none do, this is currently benign).

### 3.2 Silent exception swallow in `GitHubCopilotChatProvider.ListModelsAsync`
```csharp
try
{
    ...
    return MergeConfiguredModels(discovered, chatOptions.GitHubModels);
}
catch when (chatOptions.GitHubModels.Count > 0)
{
    return MergeConfiguredModels([], chatOptions.GitHubModels);
}
```
This is the one exception-handling site in the file that doesn't log. Any exception type (network failure, SDK bug, auth failure, cancellation) is caught and discarded as long as `GitHubModels` has at least one configured entry — the caller gets a plausible-looking model list back with no indication that live discovery actually failed. If `GitHubModels.Count == 0`, the same exception is *not* caught and propagates — meaning identical failures produce different behavior (silent vs. thrown) purely based on an unrelated config list's length, which is also just a confusing coupling on its own.
**Recommendation:** Add `_logger?.LogWarning(ex, ...)` inside the catch (capture the exception via `catch (Exception ex) when (...)`) so a real discovery failure is at least visible in logs even when the configured fallback quietly saves the user experience.
**Confidence: 90%**.

### 3.3 Duplicated timeout/payload-building logic
- `ResolveStreamIdleTimeout(ChatOptions)` — identical implementation in both `OllamaChatProvider` (line ~630) and `GitHubCopilotChatProvider` (line ~1200-ish). Same 3-line body, same clamp math, same variable names.
- Within `OllamaChatProvider` alone, `CompleteAsync` and `StreamAsync` separately rebuild the endpoint `Uri`, the `payload` dictionary (model/stream/messages), the `tools` array via `BuildOllamaTools`, and `requestOptions` via `BuildOllamaRequestOptions` — the only difference between the two call sites is `stream: false` vs. `stream: true` and what happens to the HTTP response afterward.
**Recommendation:** Extract a shared `BuildOllamaPayload(chatOptions, model, request, stream: bool)` helper for the second item; for `ResolveStreamIdleTimeout`, either promote it to a `private protected static` method on a shared base/interface default, or a `static` helper in a small internal utility class both providers reference. Neither is urgent (no bug today), but both are exactly the "arbitrary duplication with no reason for it to be separate" pattern you asked me to flag for consolidation.
**Confidence: 95%** (pure code-duplication observation, no interpretation required).

---

## 4. Methodology note: what the dead-code sweep did and didn't cover

I ran the same "count private-method references, flag ≤1" heuristic against `CodeSearchService.cs`, `MemoryApplicationService.cs`, `MaintenanceAgentServices.cs`, `TaskDomainService.cs`, and `SecurityServices.cs`. **Zero candidates surfaced in any of those five files.** I read this as a reasonably strong (not certain) signal that `ChatServices.cs`'s dead-code problem is specific to its being mid-decomposition (TSK-0042), rather than a codebase-wide pattern — the other large files don't have an equivalent in-flight extraction effort right now. I did not manually verify each of those five files' full contents this pass (see the first report's coverage disclosure — that still applies), so this is "the cheap automated check found nothing," not "I read every line and confirmed no dead code exists."

**Confidence in the "isolated to ChatServices.cs" claim: 70%** — lower than the other findings, because it rests on a fast heuristic (in-file reference counting) that would miss dead code called only via reflection, DI registration, or a naming convention my grep patterns didn't anticipate.

---

## 5. Updated assumptions & open questions (additions to report #1)

- Assumed that deleting the 10 dead `ChatServices.cs` methods is safe without a build step to confirm — reasonably confident given the reference-count verification, but a `dotnet build` after the deletion is the actual proof and I'd recommend running it before committing.
- Open question: is there a reason `ChatContextPlanner.cs`'s regex definitions weren't extracted to a shared location when it was created, rather than being copy-pasted from the (now-dead) `ChatServices.cs` version? If there's a build-ordering or assembly-boundary reason, that context would change my consolidation recommendation from "share the regexes" to "keep them separate, just delete the truly-dead copy."
- Open question (same shape as the first report's TSK-0202/0203 caveat): I did not get to a full line-by-line read of `MaintenanceAgentServices.cs`, `TaskDomainService.cs`, `SecurityServices.cs` (all >1,200 lines) or the Razor components this pass either — the automated dead-code heuristic covered them at low fidelity, but a real read is still outstanding if you want the same depth of confidence there that `ChatServices.cs` now has.
