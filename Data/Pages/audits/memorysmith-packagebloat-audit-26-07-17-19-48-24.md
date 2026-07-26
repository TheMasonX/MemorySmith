# MemorySmith Audit — Package Bloat / Supply-Chain Risk (New Findings)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-17
**Method:** read all 6 `.csproj` files in full (`MemorySmith.App`, `.Core`, `.Storage`, `.Tests`, `.Benchmarks`, `.Bridge`), then verified every non-trivial `PackageReference` against actual `using`/type usage in production `.cs` files via targeted grep — package lists alone are not evidence of bloat, actual usage (or its absence) is.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F45 | `Lucene.Net` + `Lucene.Net.Analysis.Common` + `Lucene.Net.Highlighter` (3 packages, all `4.8.0-beta00017` — a long-running beta line) are pulled in for exactly two utility classes, `StandardAnalyzer` (tokenizer) and the search-result `Highlighter` — not for Lucene's actual value proposition (indexing/searching engine), which this project hand-rolls via SQL everywhere else | 80% | Medium (disproportionate dependency footprint + beta-version supply-chain risk for a core, always-loaded feature) | **New** |
| F46 | `Nerdbank.MessagePack`, `MessagePack`, `MessagePack.Annotations`, and `Newtonsoft.Json` — 4 package references, confirmed **zero usage** anywhere in production code (no `using`, no attribute, no serializer call found for any of them) | 95% | Medium (pure supply-chain surface with no offsetting benefit) | **Corrects and extends the Archived TSK-0046**, which named only `Nerdbank.MessagePack` as a suspected-unused candidate and was shelved without resolution |

**Also checked and ruled out this pass:** all 6 `.csproj` files pin exact package versions with no floating/wildcard ranges (`Version="x.y.z"` throughout, no `*` or `[1.0,)`-style ranges) — this is good practice and worth stating positively, since unpinned version ranges are one of the more common real-world supply-chain footguns (a transitive or direct dependency silently pulling a newer, potentially compromised or breaking version on the next restore). The conditional ONNX-runtime package selection (`MemorySmithOnnxRuntimeFlavor` MSBuild property choosing between `Microsoft.ML.OnnxRuntime`/`.Gpu`/`Intel.ML.OnnxRuntime.OpenVino`) is deliberate, well-guarded (an explicit `<Error>` target rejects invalid flavor values), and not bloat — only one variant is ever actually included in a given build.

---

## F45 — Lucene.Net's full engine is pulled in for two utility classes (Medium, 80%)

**Files:** `MemorySmith.App/MemorySmith.App.csproj` (3 package references, all pinned to `4.8.0-beta00017`); usage confirmed in `MemorySmith.App/Services/MemoryApplicationService.cs` only.

**What's actually used**, confirmed via direct grep of every `Lucene.Net.*` reference in the codebase:
```
using Lucene.Net.Analysis.Standard;      // StandardAnalyzer — tokenizer
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;                    // LuceneVersion enum
using Lucene.Net.Index;
using Lucene.Net.Search.Highlight;         // Highlighter — snippet highlighting
using Lucene.Net.Search;
```
Both actual instantiations found (`MemoryApplicationService.cs:1382` and `:1462`) are `new StandardAnalyzer(LuceneMatchVersion)` — used purely as a tokenizer (per the `"lucene-standard-analyzer"` label at line 232, `"Lucene.NET StandardAnalyzer lexical ranking"`) — plus the `Highlighter` class for wrapping matched terms in search-result snippets (with a correctly-noted XSS-safety comment about pre-HTML-encoding before highlighting, which is good practice, not a smell). **No `IndexWriter`, `IndexSearcher`, `Directory`, or any actual Lucene index is created or queried anywhere** — the entire indexing/search-engine half of Lucene, which is what the library exists for and where the bulk of its code, dependencies, and beta-status risk live, is unused. The project's own actual search/ranking logic (confirmed extensively in this engagement's earlier `CodeSearchService.cs` and `MemoryApplicationService.cs` reviews) is hand-rolled directly against SQLite — Lucene.Net's tokenizer is just one input into that hand-rolled scoring pipeline, not the search engine itself.

**Why this is worth flagging as bloat, not just "a dependency exists":** three packages, all pinned to a long-running **beta** release line (Lucene.NET's port to .NET has had an unusually long beta cycle by ecosystem standards — this is common knowledge for anyone who's tracked the project, not a claim about this specific beta build's defects), loaded into every instance of this always-running web app, for functionality (word tokenization + match highlighting) that doesn't inherently require pulling in a full-text-search *engine's* transitive dependency tree. This is the same category of risk called out generally in npm/PyPI supply-chain incidents: every additional package is another maintainer, another release process, and another potential compromise point in the dependency graph, and here the benefit gained (two utility classes) is small relative to what's pulled in to get them.

**Recommendation, not urgent:** if tokenization/highlighting needs ever grow (e.g., this project starts wanting actual Lucene-grade ranking, phrase queries, or a real inverted index), keeping Lucene.Net is the right call and this finding becomes moot. If the need stays exactly what it is today (a stopword-aware tokenizer plus basic term highlighting), consider either: (a) hand-rolling a small tokenizer matching `StandardAnalyzer`'s behavior (a bounded, well-understood algorithm — word-boundary splitting, lowercase, stopword removal) and a simple highlighter (wrap known match offsets in markup), consistent with this project's own pattern of hand-rolling search logic everywhere else, or (b) a smaller, purpose-built tokenization package if one fits, rather than a full search-engine library used at under 5% of its surface. **Not a "must fix" — flagging as a deliberate tradeoff worth revisiting, not an active problem**, since the current usage is at least correct and doesn't show signs of active breakage from the beta status.
**Effort if pursued:** 1-2 days to hand-roll a tokenizer/highlighter replacement plus regression tests against the existing lexical-search test suite proving ranking output is unchanged — this is meaningful effort for a non-urgent cleanup, so treat as a backlog candidate, not a priority item.

---

## F46 — Four package references confirmed unused; corrects and extends the Archived TSK-0046 (Medium, 95%)

**Verified via direct repo-wide grep, each pattern checked independently:**
```
using MessagePack;                              → 0 files
[MessagePackObject] / MessagePackSerializer.     → 0 files
using Nerdbank.MessagePack; / Nerdbank.MessagePack.*  → 0 files
using Newtonsoft.Json; / JsonConvert.            → 0 files
```
All four package references (`Nerdbank.MessagePack` 1.2.30, `MessagePack` 3.1.7, `MessagePack.Annotations` 3.1.7, `Newtonsoft.Json` 13.0.4 — all in `MemorySmith.App.csproj`) have zero corresponding usage anywhere in production `.cs` files.

**This corrects and extends an already-Archived task:** `TSK-0046` ("run dependency hygiene pass and prune stale references," status **Archived**, priority Medium) explicitly names `Nerdbank.MessagePack` in its own problem statement as a "candidate stale reference... that may no longer be needed" — the suspicion existed and was written down, and the task was shelved anyway without resolving it. This pass provides the concrete evidence that suspicion needed: **zero usage, confirmed**. It also surfaces that the actual unused surface is larger than what TSK-0046's description named — it doesn't mention `MessagePack`/`MessagePack.Annotations` (the *other*, independently-developed MessagePack implementation for .NET — worth noting these are two competing libraries that do the same job, so even in a hypothetical world where one of them *was* used, having both referenced would itself be a smell) or `Newtonsoft.Json` at all.

**Why "Archived" is worth revisiting rather than accepting as a final decision:** "Archived" as a status typically signals a deliberate decision not to pursue (as opposed to "Backlog," which signals "not yet gotten to but still intended"). Without visibility into why it was archived, there are two possibilities: (a) someone determined removal was riskier or lower-value than it looked and made a considered call — in which case this finding is moot and the archive decision should stand, or (b) it was archived for sprint-scoping/prioritization reasons unrelated to the merits of the fix itself (deprioritized, not rejected) — in which case having concrete zero-usage evidence in hand now is exactly what would justify reopening it. I don't have visibility into which of these it was, so I'm flagging the question rather than asserting the task should definitely be reopened.
**Recommendation:** revisit the archive decision with this pass's concrete evidence in hand. If removal is pursued: delete all four `PackageReference` entries from `MemorySmith.App.csproj`, confirm a clean `dotnet restore`+`build` (not verifiable in this sandbox per the standing feasibility note from an earlier report — no NuGet access here), and run the full test suite to confirm nothing was relying on a transitive behavior these packages happened to provide incidentally. **Before removing `Newtonsoft.Json` specifically**, double-check it isn't a required transitive dependency of `Swashbuckle.AspNetCore`/`Microsoft.Extensions.ApiDescription.Server` (some older Swagger/OpenAPI tooling generations had a Newtonsoft.Json dependency for schema generation) — if so, it may need to stay as an explicit reference for version-pinning reasons even with zero direct usage, and that should be documented in the csproj with a one-line comment rather than left silently ambiguous (which is exactly the "candidate unused dependencies are removed or explicitly justified" acceptance criterion TSK-0046 itself already specifies).
**Effort:** 2-3 hours for the removal + verification pass, assuming an environment with NuGet access to actually run `dotnet restore`/`build`/`test` (not achievable in this sandbox).
**Confidence (95%):** the zero-usage claim is about as directly verifiable as a claim gets in this engagement — comprehensive grep across the whole tree for every plausible usage signature (namespace import, characteristic attribute, primary API call) for each of the four packages, all returning zero. The 5% held back accounts for the small chance one of these is referenced via reflection-only or a dynamically-loaded-assembly path that wouldn't show up in a text grep — nothing in this codebase's patterns (all read extensively across this engagement) suggested that kind of indirection is in use anywhere, but I can't rule it out with absolute certainty from static text search alone.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not check `MemorySmith.Bridge`/`.Benchmarks`/`.Core`/`.Storage`/`.Tests` csproj files' packages for the same unused-reference pattern beyond confirming they're each small (1-8 packages) and every package in those smaller files had an obvious, confirmable usage purpose on inspection (test frameworks, BenchmarkDotNet, SQLite provider trio, System.CommandLine for the Bridge CLI) — the unused-package risk this pass surfaced is concentrated entirely in `MemorySmith.App.csproj`, the largest and most actively-changed project.
- F46's recommendation to check Newtonsoft.Json's transitive necessity before removal is a caution, not a finding in itself — I did not independently trace Swashbuckle's own dependency tree to confirm or rule this out, since that would require either NuGet access (unavailable) or manually reading Swashbuckle's own `.nuspec`/dependency manifest from a source this sandbox can reach, which wasn't attempted in this pass.
