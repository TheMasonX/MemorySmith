# MemorySmith Audit — `MaintenanceAgentServices.cs` Remaining Classes: Exhaustive Pass (Majors + Nits)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-19
**Method:** read `MaintenanceAgentConfigService`, `MaintenanceResourceProbe`, the remainder of `MaintenanceWritePermissionService`, and `MaintenanceTopicMapService`'s graph-building/analysis methods (`BuildAsync`, `BuildClusters`, `FindOrphans`, `BuildSupersessionChains`, `FindDependencyCycles`, `BuildStalenessHeatmap`) in full — closing out the last major unread section of this 2,187-line file.

**Important process note surfaced this pass:** partway through, this file's graph/topic-map logic turned out to already be the subject of an extremely thorough **existing internal audit document**, `Data/Pages/audits/kb-graph-rag-audit-10-07-26-00-00-00.md` (11 findings, G-1 through G-11, two already converted to tracked tasks — TSK-0321, TSK-0323). This document was not surfaced by this engagement's earlier `Data/Tasks/*.json` keyword searches because several of its findings (G-1, G-3, G-4/5, G-6, G-8, G-10, G-11) haven't been converted into individual `TSK-####` tickets yet — they exist only as line items inside this one audit page. **Process lesson for future passes on this repo: check `Data/Pages/audits/` directly, not just `Data/Tasks/*.json`, since a finding can be real, documented, and prioritized without yet having its own ticket.** This pass cross-checked every candidate finding against that document before writing anything up, which is what kept this report to genuinely new material instead of re-deriving G-6/G-7/G-8's already-well-scoped findings from scratch.

---

## Priority-Tiered Summary (for triage)

| # | Finding | Tier | Confidence | Relationship to existing tasks/audits |
|---|---|---|---|---|
| F57 | `MaintenanceTopicMapService.BuildAsync`'s page-to-record `Mentions` edge extraction is an unbounded `O(pages × records)` full-text substring scan, with each unit of work scanning the entire page markdown — a real, previously-undocumented cost on top of the already-tracked topic-map performance concerns | **P1 — new, real, undocumented elsewhere** | 85% | **New** — not covered by G-1 through G-11 in `kb-graph-rag-audit`, confirmed via direct search of that document |
| — | `FindDependencyCycles`'s recursive `Visit` function carries the same uncatchable-`StackOverflowException` crash risk as F56 (`MaintenanceDiffService`), on a graph confirmed genuinely reachable via tag-based `depends-on:` extraction | **P1 — extends an already-tracked task** | 90% | **Extends TSK-0321/G-7** — that task correctly identifies the redundant-work/duplicate-cycle-reporting cost but frames it as a performance concern ("needlessly expensive"), not a crash-safety one; worth adding explicitly to its scope given F56 already established this exact failure mode is realistic in this same file |
| — | `BuildSupersessionChains` only emits raw 2-element `[source, target]` pairs per edge, never actually walking multi-hop chains | **Info — already correctly tracked** | — | **Confirms G-6** exactly as described; no new information to add |
| — | `BuildClusters` has no size ceiling on tag-based clustering (a broadly-shared tag produces a giant, low-value cluster) | **Info — already correctly tracked** | — | **Confirms G-8** exactly as described; no new information to add |
| N1 | `MaintenanceWritePermissionService.IsProhibitedPath` is a denylist of specific dangerous extensions/filenames, not an allowlist of expected content types — a config change widening `MaintenanceAgent.Write`/chat-agent write roots beyond their current defaults would not be caught by this check for file types the list's author didn't think to name (e.g. `.cs`, `.sh`, `.ps1`, `.exe`) | **P2 — real gap, low practical exposure today** | 75% | **New** |
| N2 | `MaintenanceAgentConfigService`'s `Write`-roots normalization dictionary reuses the constant `DefaultReadPagesRoot` as a key (rather than a same-value `DefaultWritePagesRoot`) — functionally correct today only because the two roots happen to share the same literal path string, but the naming actively misleads a reader into thinking this might be a bug | **Nit** | 90% | **New** |
| N3 | `MaintenanceResourceProbe.NormalizeProcessName` calls `.ToLowerInvariant()` even though every comparison consuming its output already uses `StringComparer.OrdinalIgnoreCase` — harmless, purely redundant work | **Nit** | 95% | **New** |

---

## F57 — Unbounded page×record substring scan in `BuildAsync` (P1, 85%)

**File:** `MemorySmith.App/Services/MaintenanceAgentServices.cs`, `MaintenanceTopicMapService.BuildAsync`, lines 1031-1035:
```csharp
edges.AddRange(ExtractPageLinks(nodeId, page.Markdown));
foreach (var recordId in recordIds.Where(id => page.Markdown.Contains(id, StringComparison.OrdinalIgnoreCase)))
{
    edges.Add(new TopicMapEdge(nodeId, recordId, "Mentions", page.RelativePath));
}
```
For every page (outer loop, line 1011), this scans **every memory record ID** against that page's full markdown text via `string.Contains` — an `O(pages × records)` substring search where each unit of work is a full linear scan of the page's content length. For a KB with, say, a few hundred pages and a couple thousand memory records (a plausible size for a KB that's been running a while, consistent with this project's own stated trajectory toward more content and cross-linking over time — the same growth assumption already used to justify several other findings in this engagement, including the already-tracked G-9 incremental-build concern), this is potentially hundreds of thousands to low millions of substring scans on every single topic-map rebuild — and `BuildAsync` runs on every maintenance run (weekly-scheduled **and** on-demand, with no exclusivity guard between them per this engagement's F55 finding, meaning this cost can also compound if two runs overlap).

**Why this is additive to, not a duplicate of, the existing `kb-graph-rag-audit` findings:** that document's G-9 ("make topic-map builds incremental") addresses the *frequency* of full rebuilds (currently weekly/on-demand, proposed to become incremental-on-edit) but doesn't specifically call out *this* substring-scan step as a distinct cost worth its own attention even within a full rebuild — G-9's fix would substantially mitigate this finding as a side effect (an incremental update touching one page would only need to check that one page against the record set, not re-scan every page against every record), but until G-9 lands, this specific step is a real, currently-unaddressed cost inside every full rebuild that happens in the meantime.
**Recommendation:** two options, not mutually exclusive: (a) as a standalone mitigation independent of G-9's timeline, build a single `Aho-Corasick`-style multi-pattern matcher (or, more simply given .NET's standard library, a `HashSet`-based tokenization of the page markdown compared against record IDs, rather than N separate `Contains` calls) so each page is scanned once regardless of record count, turning this into `O(pages × page_length)` instead of `O(pages × records × page_length)`; (b) treat this as a specific implementation detail worth calling out explicitly when G-9's incremental-build work is scoped, so whoever picks that up knows this is one of the concrete costs incremental updates are expected to eliminate, not just a vague "rebuilds are slow" concern.
**Effort:** half a day for option (a) alone; effectively free (just a documentation note) if folded into G-9's existing scope instead.
**Confidence (85%):** the algorithmic claim is directly read from the code. The 15% held back reflects not having a concrete current record/page count for a representative deployment of this project — same calibration caveat already applied to the structurally similar F44 (tag-governance) finding earlier in this engagement.

---

## Extension to TSK-0321/G-7 — `FindDependencyCycles`'s recursion carries the same crash risk as F56

**File:** same file, lines 1215-1253 (`FindDependencyCycles`/`Visit`). G-7 (and its ticketed form, TSK-0321) already precisely and correctly identifies that this DFS clones a fresh `seen` set per call rather than sharing one visited-set with proper recursion-stack tracking, that this is "needlessly expensive on dense `depends-on:` graphs," and that a single cycle gets reported once per rotation rather than deduplicated. All of that is accurate and doesn't need re-deriving.

**What this pass adds:** `Visit` is a **recursive** local function, and — independently verified this session, not assumed — `DependsOn` edges are genuinely reachable in production (populated from tag-based extraction, `ExtractTagRelationships`, whenever a record carries a `depends-on:`/`dependson:`-prefixed tag; confirmed via direct grep, not inferred). This means the exact same class of risk already documented for `MaintenanceDiffService` in this engagement's prior report (F56: unbounded recursion depth → uncatchable `StackOverflowException` → whole-process crash, not just a failed request) applies here too, on a graph structure that's plausible to grow dense and cyclic over time given this project's own tagging/cross-linking trajectory. G-7's own framing ("needlessly expensive... worst-case blowup") reads as primarily a *performance* concern with a perf-test as its acceptance criterion — it doesn't explicitly flag that the *specific* failure mode at the far end of that "worst-case blowup" is an uncatchable process crash rather than just a slow response.
**Recommendation:** when TSK-0321's fix is implemented, make sure the rewritten shared-visited-set DFS is either iterative (an explicit stack, same pattern already recommended for `MaintenanceDiffService.AppendDiff` in F56) or has an explicit recursion-depth guard that fails gracefully (skip further traversal past a configurable depth, log a warning) rather than assuming the redundant-work fix alone is sufficient — a shared-visited-set DFS is *usually* written recursively by default in a straightforward rewrite, and would still carry the same stack-depth exposure for a sufficiently long dependency **chain** (as opposed to redundant cycle-checking, which the shared-visited-set fix does eliminate) even after TSK-0321's described fix lands exactly as written.
**Effort:** effectively no additional effort if folded into TSK-0321's existing implementation work — this is a "build it this specific way" note for whoever already has that task, not a separate piece of work.
**Confidence (90%):** the recursion-depth/crash-mode claim rests on the same well-established .NET runtime facts as F56, applied to a structurally similar recursive function; the `DependsOn`-reachability claim was independently re-verified this session via direct grep, not assumed from adjacent findings.

---

## N1 — `IsProhibitedPath` is a denylist, not an allowlist (P2, 75%)

**File:** same file, lines 515-531. `IsProhibitedPath` blocks a specific, named set of extensions (`.csproj`, `.sln`, `.slnx`, `.props`, `.targets`, `.yaml`, `.yml`) and filenames (`appsettings*`, `maintenance_agent.json/.yaml`) plus any path segment named `Schemas`. This correctly stops a maintenance proposal from touching the specific file types someone thought to name. It does **not** stop a proposal from writing to, say, a `.cs`, `.sh`, `.ps1`, `.py`, or `.exe` file, as long as the target path is under one of the configured `Write` roots. Today's default `Write` roots (`Data/Memories/Working`, `Data/Pages`, per `MaintenanceAgentConfigService.Normalize`) make this a low-practical-exposure gap — those directories aren't likely to contain executable code by default — but the check itself provides no structural protection if an admin ever widens the configured write roots, or if a future chat-agent-proposal write-root expansion (this file already has a separate `GetChatProposalWriteRoots` method for a related-but-different write-permission surface) ever includes a broader directory.
**Recommendation:** consider inverting this to an allowlist of expected content extensions for this feature (`.md`, `.json` seem to cover the actual intended use — wiki pages and memory records) rather than a denylist of remembered-dangerous ones. This is a design-level suggestion, not an urgent fix, given today's actual configured roots — but it's the more robust shape for a feature whose explicit purpose is letting an LLM propose file writes.
**Effort:** a few hours if pursued — mostly deciding the right extension allowlist and checking it doesn't break any legitimate existing proposal shape.

## N2 — Misleadingly-named dictionary key reuse (Nit, 90%)

**File:** same file, lines 276-283. `Write`-roots normalization reuses `DefaultReadPagesRoot` as a dictionary key when mapping legacy default paths to their current equivalents. This is **not a functional bug** — `DefaultReadPagesRoot`'s literal value (`"../Data/Pages"`) happens to be identical to what a hypothetical `DefaultWritePagesRoot` would be, since pages are read from and written to the same root — but the name actively suggests something read-specific is being used in a write-path context, which is confusing on a skim and worth a one-line comment (`// pages share one root for both read and write normalization`) or a second, identically-valued constant with a name that matches its actual use here.
**Effort:** 10 minutes.

## N3 — Redundant case-normalization call (Nit, 95%)

**File:** same file, line 406. `NormalizeProcessName` calls `.ToLowerInvariant()` on the process name, but every place that consumes the result (`configuredNames`, a `HashSet<string>(StringComparer.OrdinalIgnoreCase)`, and the `Contains` check against it) already ignores case. The lowercase call does nothing except a small amount of wasted string allocation on every process enumerated. Harmless, but removable.
**Effort:** 2 minutes.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- This closes out the graph-building/analysis portion of `MaintenanceTopicMapService`; `SubmitOutputProposalsAsync`, `TryRunLlmReviewAsync`, `ReviewProposalAsync`, and the remainder of `MaintenanceAgentService`'s own orchestration methods (roughly the file's last ~700 lines) remain open scope for a further pass.
- F57's severity depends on real record/page counts for a representative deployment, same caveat as this engagement's F44 finding — flagged as P1 based on the shape of the risk and this project's stated growth trajectory, not a measured current cost.
- Did not exhaustively re-verify every one of G-1 through G-11's other findings (G-1/G-2 reverse-reference wiring, G-3/TSK-0268, G-4/G-5 typed-edge migration, G-11 graph-reranking evaluation) against current code in this pass — read enough of the document to confirm G-6/G-7/G-8 specifically match what this pass independently found in the same methods, and to confirm F57 isn't already covered elsewhere in it, but did not re-audit the full document's other claims from scratch.
