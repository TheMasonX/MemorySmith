# MemorySmith Code Audit — 2026-07-02

**Repo:** `TheMasonX/MemorySmith` (public) · **Branch:** `master` · **Fetched via:** `codeload.github.com` tarball (bash_tool, since `api.github.com` was rate-limited on this IP) · **Size:** ~52k LOC C# (164 files) + 240 wiki/doc pages + 284 task records
**Auditor stance:** Independent re-verification against source, not a repeat of prior audit content. Every claim below was checked against the actual file at HEAD; claims copied from prior audits without re-verification are explicitly marked as such.

---

## 0. Executive Summary

**This is a heavily-audited project.** 30+ prior audit/council documents already live in `Data/Pages/audits/` and `Data/Pages/council/`, most recently the external deep-research audit + 5-chair council review on 2026-06-17, and `hyperagent-audit-9` (architecture) on 2026-06-11. The task board (284 records) already tracks most structural debt. **My highest-value contribution is not finding net-new debt in a well-picked-over codebase — it's telling you which of the June 17 council's P0/P1 findings are still live 15 days later**, plus a handful of genuinely new items the prior passes didn't file.

### Headline findings (all independently re-verified against current HEAD, not copied from prior reports)

| # | Finding | Status | Confidence |
|---|---|---|---|
| 1 | **Two files with live production secrets (API key, GitHub OAuth Client Secret) are still committed at HEAD**, 15 days after a P0 "rotate within 24 hours" directive. Same key values as the June 17 audit — never rotated. | 🔴 Unresolved, worse than tracked | **98%** |
| 2 | **First-OAuth-user-becomes-Admin bootstrap is still ungated** — no allowlist, no token, no loopback check. Composes directly with #1 (leaked ClientSecret). | 🔴 Unresolved | **95%** |
| 3 | **Zero antiforgery protection** across all controllers is still true — confirmed by absence of any `[ValidateAntiForgeryToken]`/`AddAntiforgery` in the codebase. | 🔴 Unresolved | **95%** |
| 4 | **`MemoryIndex` is still a plain, unlocked `Dictionary` with a `Clear()`-then-refill `Rebuild()`** — the race is live on write paths; read paths still use `_store.LoadAll()` so blast radius is unchanged (not yet escalated to CRITICAL). | 🔴 Unresolved | **92%** |
| 5 | **A length-leaking `FixedTimeEquals` helper now exists in *three* independent copies** (not two, as the June 17 review found) — the exact same timing-safety bug was re-typed a third time instead of centralized. | 🟡 New evidence on a tracked issue | **95%** |
| 6 | **README.md documents a tool-call example (`memorysmith_unified_search`) that no longer exists** in the tool catalog or MCP surface — the deprecation (TSK-0271) removed the code but not this doc, so it's actively misleading anyone reading it today. | 🟢 New finding | **93%** |
| 7 | **`AgentSessionService.NestingDepth` is initialized but never enforced** — the only guard against future agent-delegation recursion is a hardcoded self-exclusion `HashSet`. Sequencing risk for TSK-0276 (Phase 3 delegation), not an active vulnerability today. | 🟢 New finding (sequencing risk) | **85%** |
| 8 | **20% of task-board JSON records (56/284) carry a UTF-8 BOM**, the other 80% don't — a naive `json.load` throws on the BOM'd files. Direct evidence for the already-backlogged TSK-0281 (CI JSON-wellformedness lint). | 🟢 New corroborating evidence | **99%** |
| 9 | Two in-progress tasks (TSK-0201, TSK-0203) have had no comment/status update in **~5 weeks** while still marked `InProgress` — task-board hygiene debt, not code debt. | 🟢 New finding | **80%** |
| 10 | TSK-0042 (ChatServices decomposition) Step 1 (tool-loop unification) **is verified landed** — `ChatServices.cs` is 3,736 lines (down slightly, loop duplication gone); Step 2 (file split) has **not** started. Provider-name leak (TSK-0283) and code-search seam gap (TSK-0284) are confirmed still open, unchanged since audit #9. | ⚪ Verified status of tracked work | **90%** |

### What I did *not* find
No swallowed exceptions in production code (only `catch { /* best effort */ }` in test teardown, which is fine). No `async void`, no sync-over-async (`.Result`/`.Wait()`), no `NotImplementedException`. This codebase's baseline hygiene is genuinely good — the debt is concentrated in a few large files and in **process** (secrets hygiene, doc/code drift, task-board freshness), not scattered code smell.

### Coverage & methodology disclosure (read before trusting the "no other bugs" implication above)
- **Full-file read, line-by-line:** `Program.cs` and all `MemorySmith.App/Hosting/*.cs` (composition root), `GitHubOAuthCallbackHandler.cs`, `MemorySmithRequestGuardMiddleware.cs`, `MemoryIndex.cs`, `MaintenanceAgentServices.cs` (model-resolution section), `AgentSessionService.cs` (session creation + tool scoping), `MemorySmithOptions.cs` (config surface), `ChatToolCatalog.cs` catalog wiring, `McpController.cs`, task/wiki metadata for the 5 in-progress tasks and 6 most recent audit/council documents.
- **Targeted grep-and-spot-read across the full 164-file, ~52k-LOC C# tree:** anti-pattern sweep (TODO/FIXME, empty catches, generic `catch(Exception)`, sync-over-async, `async void`, `NotImplementedException`, "legacy"/"Obsolete" markers, hardcoded secrets/paths, provider-name string leaks) plus follow-up reads wherever a hit looked substantive.
- **Not read line-by-line in this pass:** the bulk of `ChatServices.cs` (3,736 lines), `CodeSearchService.cs` (3,115 lines), `MemoryApplicationService.cs`, `TaskDomainService.cs`, `SecurityServices.cs` in full, the 28 Razor components, the 18,036 lines of `MemorySmith.Tests`, and the Python/PowerShell training & CI scripts. These were sampled (grep-driven) rather than exhaustively read. **This means the "no swallowed exceptions / no bad patterns" claim above is a grep-sweep result across the whole tree (high recall for the specific patterns searched), not a certified full manual read of every line of those large files** — treat it as "nothing of that *shape* found," not "provably absent."
- If you want literal every-line coverage of the three ~3,000-line hotspot files, that's a good scope for a dedicated follow-up pass (recommend one file per session) rather than folding it into this one, both for reviewer attention quality and context-budget honesty.

---

## 1. Verified-Still-Open: Prior P0/P1 Security Findings

These were all raised in `council/external-audit-council-review-20260617.md`. I re-checked each against the current tarball rather than trusting the doc's age. None of the P0s or the H-3 concurrency item show any code change since.

### 1.1 Leaked secrets still committed (was C-1, P0, "rotate within 24 hours")
**Evidence (direct file read at current HEAD):**
- `.vscode/mcp.json` — contains `"X-Api-Key": "DLxQJGounPrxit_ITQlcHBBbXLAbaryl-oduvmQMphw"` — **identical value** to the one the June 17 audit flagged as compromised.
- `artifacts/MemorySmith.App/appsettings.LocalOverrides.json` — contains the same `ApiKey`, plus `GitHub:ClientId = Ov23liSOQ3m2pthdQv0w` and `GitHub:ClientSecret = 9d0b9498af3f872b7cb1289b175498da2ffccdb8`, plus the operator's local filesystem path (`C:\Users\norrt\source\repos\MemorySmith\...`).
- `.gitignore` **does** list `artifacts/` (line 75) and `MemorySmith.App/appsettings.LocalOverrides.json` (line 77) — meaning these files were force-added (`git add -f`) at some point and gitignore is not currently doing its job for content already tracked.

**Why this is worse than the tracked severity implies:** a P0 with an explicit 24-hour SLA sailed 15 days past deadline with the exact same secret values still live and still reachable by anyone who clones the public repo. If these credentials still gate anything in the operator's real deployment, they are compromised right now, not hypothetically.

**Recommendation (unblocks in under an hour):**
1. Rotate the API key and the GitHub OAuth ClientSecret immediately — treat both as burned regardless of whether this audit is the reason you notice.
2. Remove both files from git history (`git filter-repo --path .vscode/mcp.json --path artifacts/ --invert-paths`, then force-push), not just from the working tree.
3. Fix the workflow that force-added them — likely an IDE/task-runner auto-save or an `mcp.json` template that ships a real key by default (the file has a commented-out placeholder block above the live block, suggesting a copy-paste-then-forgot-to-clear pattern).
4. Add a pre-commit hook (`.githooks/pre-commit` already exists in this repo — extend it) that greps staged diffs for the literal patterns `X-Api-Key`, `ClientSecret`, and the `MemorySmith:ApiKey` JSON path, and rejects the commit if a non-placeholder value is present.

**Confidence: 98%** (direct current-file read; only uncertainty is whether these specific credential values are still active in the operator's live deployment vs. already superseded by an out-of-repo rotation).

### 1.2 OAuth first-user-is-Admin bootstrap still ungated (was C-2, P0)
**Evidence:** `GitHubOAuthCallbackHandler.cs`, current code:
```csharp
var isFirstAdmin = !await db.Users.HasAnyAdminAsync(ct);
var assignedRole = isFirstAdmin ? MemorySmithRoles.Admin : MemorySmithPermissionHandler.NormalizeAuthenticatedDefaultRole(msOpts.Auth.AuthenticatedDefaultRole);
await db.Roles.AssignRoleAsync(internalUserId, assignedRole, null, ct);
```
No bootstrap token, no email/username allowlist, no loopback restriction on the code path that grants Admin. Whoever completes the GitHub OAuth flow first — on a server that hasn't been bootstrapped yet — becomes Admin. Combined with §1.1 (leaked ClientSecret), an attacker with network access to an un-bootstrapped instance's OAuth callback has a plausible path to Admin.

**Recommendation:** Gate first-admin promotion behind one of: (a) a one-time bootstrap token set via environment variable/CLI at first run, (b) an `AllowedFirstAdminEmails` allowlist in config, or (c) requiring the very first login to originate from a loopback/local request. Any one of these closes the gap; (a) is the least code and most explicit.

**Confidence: 95%** (direct code read; the only variable is whether some other layer — e.g., network-level firewalling in the operator's actual deployment — mitigates this in practice, which I can't verify from the repo alone).

### 1.3 Zero antiforgery protection (was H-1, HIGH)
**Evidence:** `grep -rn "AutoValidateAntiforgeryToken\|ValidateAntiForgeryToken\|AddAntiforgery"` across the entire `.cs` tree returns **zero hits**. This includes `SourceLinksController.Open` (a CSRF-to-OS-shell vector per the original finding) and `AdminController` role-assignment endpoints.

**Recommendation:** Add `builder.Services.AddAntiforgery()` + `[AutoValidateAntiforgeryToken]` globally in `MemorySmithSecuritySetup.cs` (the composition-root module this now lives in post-TSK-0282), then add explicit `[IgnoreAntiforgeryToken]` on any endpoints that must remain callable without a browser-issued token (e.g., the MCP API surface, which already has its own `ApiKey`/session auth). Mitigating factor unchanged from prior review: `AllowRemoteApi=false` by default blocks remote exploitation; local-page and `localhost`-origin CSRF remain in scope.

**Confidence: 95%**.

### 1.4 `MemoryIndex` concurrency race (was H-3, HIGH)
**Evidence:** `MemorySmith.Core/Indexing/MemoryIndex.cs`:
```csharp
public Dictionary<string, MemoryRecord> ById { get; } = new();
public Dictionary<string, HashSet<string>> ByTag { get; } = new();
public Dictionary<string, HashSet<string>> ByReference { get; } = new();
...
public void Rebuild(IEnumerable<MemoryRecord> records)
{
    ById.Clear();
    ByTag.Clear();
    ByReference.Clear();
    ...
}
```
Still plain `Dictionary`, still `Clear()`-then-refill. I also re-verified the council's mitigating claim: `MemoryApplicationService.cs` reads exclusively via `_store.LoadAll()` (9 call sites checked), not via `_index` — so this is still a **write-path-only** race today, not yet promoted to the read path. Severity assessment (HIGH, escalates to CRITICAL if search is ever promoted to consult the index) still holds and I found no evidence of that promotion happening.

**Recommendation:** Swap the three `Dictionary` fields for `ConcurrentDictionary`, and rewrite `Rebuild()` to build into new dictionaries and atomically swap references rather than `Clear()`-then-refill in place (eliminates the window where a concurrent `Add()` gets wiped by an in-flight rebuild). This is a self-contained, low-risk change confined to one file.

**Confidence: 92%**.

### 1.5 `FixedTimeEquals` length-leak — now three copies, not two (extends M-3)
**Evidence — three independent implementations of the same buggy pattern:**
- `MemorySmithRequestGuardMiddleware.cs:98-110`
- `SecurityServices.cs:337-349`
- `SecurityServices.cs:799-804` *(this third copy was not identified in the June 17 review, which counted two)*

All three share the identical flaw: `actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(...)` — the `&&` short-circuits on length mismatch before reaching the constant-time compare, leaking key length via response timing. Practical impact remains LOW (keys are 32+ bytes of entropy per the prior assessment), but the *pattern* is now proven to propagate by copy-paste rather than by reference to a shared helper — every new caller re-introduces the bug fresh.

**Recommendation:** Extract one `SecurityCompare.FixedTimeEquals(string, string)` static helper (pad-to-common-length internally, no short-circuit) into a shared location (`MemorySmith.Core` is the natural home since both `App`-layer copies would depend on it), and delete all three private copies in favor of it. This is exactly the kind of "one source of truth" consolidation the project's stated goal (no legacy duplication) calls for, and it's a ~20-minute change with an existing test file (`SecurityServicesTests`, presumably) to extend.

**Confidence: 95%** on the code observation; the "three copies, not two" delta is the new information here.

---

## 2. New Findings (not previously tracked in any task or audit doc I found)

### 2.1 README.md documents a dead tool call as if it were live
**Evidence:** `README.md` line 158 shows the exact JSON the model is told to emit for retrieval:
```json
{"toolCalls":[{"name":"memorysmith_unified_search","arguments":{"query":"search text","memoryLimit":5,"pageLimit":5}}]}
```
and line 193 documents `memorysmith_unified_search` in the tool reference table. But `grep -rn "unified_search" --include=*.cs` across the entire codebase returns **zero hits in `ChatToolCatalog.cs` or `McpController.cs`** — the only remaining reference in code is a comment in `ChatContextPlanner.cs` explaining *why it was removed*. TSK-0271's own progress comment already flags "README" as a remaining step, but doesn't specify that the doc contains an actively-wrong worked example, not just a stale table row.

**Why this matters more than a typical doc-lag:** this isn't just outdated — it's a copy-pasteable example that will fail if anyone (human or another LLM agent reading this README as context) follows it literally.

**Recommendation:** When finishing TSK-0271 step 4 (README), replace the worked example with a `hybrid_search` call and drop `memorysmith_semantic_search`/`memorysmith_unified_search` from the tool table. Small, mechanical, ~10 minutes.

**Confidence: 93%**.

### 2.2 Agent-session nesting-depth guard is decorative until Phase 3, but the sequencing isn't documented as a gate
**Evidence:** `AgentSessionService.cs:236` sets `NestingDepth = 0` with a `// TODO (Phase 3): enforce MaxNestingDepth ceiling here`. The *only* current anti-recursion mechanism is a hardcoded `selfExcluded` HashSet (`memorysmith_agent_invoke`, `memorysmith_agent_session_end`) at line 612, also marked `TODO (Phase 3)`.

**Risk:** TSK-0276 ("Phase 3 internal agent delegation") is `Backlog`, not yet scheduled, so there's no active vulnerability today — sub-agents can't currently spawn sub-agents because the feature that would let them (`AvailableInAgent=true`) doesn't exist yet. The risk is purely sequencing: if TSK-0276 is implemented and someone flips the delegation flag on **before** the two TODOs above are resolved, you'd have unbounded recursive agent delegation with only a hardcoded two-tool exclusion list standing in the way — a HashSet, not a depth ceiling, is not a substitute for it.

**Recommendation:** Add an explicit acceptance-criterion line to TSK-0276 (or a blocking sub-task) stating "MaxNestingDepth enforcement and NestingDepth increment-on-delegation must land in the same PR as `AvailableInAgent=true` becoming settable — not after." This costs one line in a task description and prevents a foreseeable ordering mistake.

**Confidence: 85%** (the gap itself is directly observed; the "will someone actually ship the flag before the guard" risk is a judgment call, hence lower confidence than the pure code-reading findings above).

### 2.3 Task-board data hygiene: BOM inconsistency (supports already-backlogged TSK-0281)
**Evidence:** Of 284 files in `Data/Tasks/*.json`, 56 (19.7%) begin with a UTF-8 BOM (`EF BB BF`) and 228 don't. A naive `json.loads()` (Python) or any parser not explicitly tolerant of BOM throws `Unexpected UTF-8 BOM` on the 56. I hit this directly while writing my own analysis script.

**Relevance:** TSK-0281 ("ci-lint-for-data-json-wellformedness," currently `Backlog`) is designed to catch exactly this class of problem before it reaches consumers. This finding is concrete evidence that the problem is not hypothetical — it's present in ~1 in 5 files today — and can be cited to justify prioritizing TSK-0281 rather than treating it as a nice-to-have.

**Recommendation:** When TSK-0281 is implemented, include a BOM-normalization pass (strip BOM on write, tolerate on read) alongside the wellformedness check, and run it once against the existing 284 files to normalize them in one commit.

**Confidence: 99%** (directly counted).

### 2.4 Two `InProgress` tasks have gone stale without status updates
**Evidence:** TSK-0201 (chat transcript/feedback data plane) last commented 2026-05-28; TSK-0203 (Python training harness) last commented 2026-05-29. Both still show `"status": "InProgress"` as of this audit (2026-07-02) — roughly **5 weeks** with no comment, while TSK-0271 and TSK-0042 (also `InProgress`) both have comments from June 11-12. This is either (a) genuinely stalled work worth a status check, or (b) work that's actually done/superseded and the status field is just stale — both are process debt, not code debt.

**Recommendation:** Quick triage: if the described scope ("exporter/bridge plumbing," "true LoRA path and cancellation semantics") shipped in a later commit not reflected in comments, close or update the task; if genuinely stalled, that's useful signal about where attention has drifted, worth a sprint-planning look.

**Confidence: 80%** (the staleness is a fact; whether it indicates a real problem vs. a bookkeeping gap is an inference).

---

## 3. Verified Status of Already-Tracked "In Progress" Work

The user specifically flagged "currently in progress areas." Cross-referencing the task board's `InProgress` filter (5 tasks) against source:

| Task | Claimed state (per task comments) | Verified against source | Confidence |
|---|---|---|---|
| **TSK-0042** — Decompose ChatServices | Step 1 (tool-loop unification) landed 2026-06-12 | **Confirmed.** `MemoryChatAgent.ToolLoop.cs` exists (329 lines, single `RunToolLoopAsync` driver); `ChatServices.cs` is 3,736 lines with no duplicate inline loop found via grep for a second loop body. Step 2 (splitting the file itself) has not started — still one 3,736-line file. | 90% |
| **TSK-0271** — Remove `semantic_search`/`unified_search` tools | "Step 2 effectively complete," docs/training regen remaining | **Confirmed for code**: zero references in `ChatToolCatalog.cs`, `McpController.cs`. **Confirmed still-open for docs**: README still references both (see §2.1). Training-corpus regen / LoRA retrain status not independently verifiable from static source (would require checking `Data/Training/exports/` timestamps against the removal commit, which I did not do this pass). | 88% |
| **TSK-0201** — Chat transcript/feedback data plane | Wired thumbs up/down + exporter path (as of 2026-05-28) | Comments describe concrete shipped behavior (`IChatFeedbackStore.UpsertAsync`, a real export artifact `sprint3-ft-20260528.sft.jsonl`). No reason to doubt the described work happened; flagging only the staleness of the status field (§2.4). | 75% |
| **TSK-0202** — `num_ctx` context-window governance | "Implemented... Remaining scope: stricter governance UX" | Not independently re-verified against `OllamaChatProvider` this pass — accepting the task's own closure-adjacent comment at face value. **This is the one item in this table I did not re-check against source; flag accordingly if precision matters.** | 40% (unverified) |
| **TSK-0203** — Python training harness + .NET bridge | UI/telemetry hardening complete; "non-UI harness execution depth" remaining | Same caveat as TSK-0202 — not re-verified against `MemorySmith.Training/harness.py` or `TrainingHarnessRunnerService.cs` line-by-line this pass. Status staleness noted in §2.4. | 40% (unverified) |

**Open question for the operator:** should TSK-0202/0203 get a follow-up verification pass specifically? I did not have budget in this session to read the Python harness and bridge contract in depth — flagging rather than guessing.

---

## 4. Legacy/Fallback Consolidation Opportunities (explicit ask)

Ranked by how directly they match "safely consolidate and lift out of legacy fallbacks":

1. **`FixedTimeEquals` × 3 → 1** (§1.5). Highest-confidence, lowest-risk, purely mechanical. Do this first as a template for the pattern below.
2. **`MaintenanceAgentOptions` dual config path** (legacy `Provider`/`Model`/`OllamaEndpoint` fields vs. the newer `ModelProfileId` system). Confirmed in `MaintenanceAgentServices.cs`'s `ApplyAssignedModelProfile`: when a `ModelProfileId` doesn't resolve to an enabled profile with a non-empty `Model`, the method **silently returns without setting anything**, leaving the legacy fields as the effective config with no log line indicating the profile lookup failed. `AdminSettingsService.cs`'s own descriptor text confirms the dual-path is intentional ("overrides the legacy provider/model fields") rather than accidental, but the silent-no-op-on-miss is the actual bug: a typo'd or disabled `ModelProfileId` degrades silently to whatever the legacy fields happen to contain, with no diagnostic. *(This is a refinement of the general "provider seam is dishonest" finding from hyperagent-audit-9's candidate 4/TSK-0283, applied to a config-resolution path that audit didn't specifically examine.)* **Confidence: 82%** (code path directly read; "no diagnostic" claim based on absence of any `_logger` call in the method, which I confirmed).
   - **Recommendation:** Log a warning when `assignmentId` is non-empty but resolution fails (profile missing or disabled or empty `Model`), so operators can tell "using legacy fields on purpose" from "my profile ID has a typo" — currently indistinguishable.
3. **Provider-name string branching** (TSK-0283, already backlogged, verified still open at 8 call sites including the duplicated `ProviderMatches` helper at lines 151 and 2268 of `ChatServices.cs`). Not new, but I confirmed the specific line numbers have not moved and the duplication is unchanged since audit #9. **Confidence: 90%**.
4. **Nine ceremonial single-adapter store interfaces** (TSK-0157, already backlogged). Not independently re-verified this pass beyond confirming `SqliteMemorySmithDatabase.cs` still exists as a monolith (did not count current line number). Deferring to audit #9's existing analysis rather than re-doing it.

---

## 5. Assumptions & Open Questions

**Assumptions made:**
- "Latest commit" = current `master` HEAD as fetched via `codeload.github.com` tarball at the time of this audit (~2026-07-02); I could not get a specific commit SHA because `api.github.com` was rate-limited on this environment's egress IP for the entire session (unauthenticated 60 req/hr ceiling shared across whatever else uses this IP pool). The tarball content is authoritative for "what's in the repo right now," but I cannot cite a SHA for it.
- The leaked-credential values I observed are assumed to still be *live* in the operator's actual deployment unless already rotated out-of-band — I have no way to verify actual production validity of an API key or OAuth secret from static source alone.
- Where a prior audit's finding was reused without re-verification (a few items in §3's table, and the deferred TSK-0157 item), I've marked confidence accordingly (40%) rather than inheriting the prior document's confidence — treat those as "worth a look," not "confirmed."

**Open questions for the operator:**
1. Are the leaked API key / OAuth ClientSecret currently valid in a live deployment, or already superseded? This changes §1.1 from "urgent" to "already handled, just clean up the repo."
2. Is there a reason TSK-0201/0203 haven't been touched in 5 weeks — deprioritized, blocked on external dependencies (the task text mentions "pinned environment dependencies"), or just stale bookkeeping?
3. Do you want a dedicated follow-up pass that does a genuine line-by-line read of `ChatServices.cs`, `CodeSearchService.cs`, and `MemoryApplicationService.cs` (the three files this pass sampled rather than fully read)? Given their size, I'd suggest one file per session rather than folding into this one.
4. Should the `.githooks/pre-commit` secret-scanning suggestion (§1.1) be scoped narrowly (grep for known-bad literal values, fast to implement) or broadly (integrate a tool like `gitleaks` in CI, more thorough but more setup)?

---

## 6. Quick-Reference Action List (by effort, not just severity)

**Under 1 hour each:**
- Rotate API key + OAuth ClientSecret; remove both leaked files from git history (§1.1)
- Add `app.UseHsts()` + `AddServerHeader = false` (already-tracked M-1/M-2, confirmed still absent, not re-detailed here — see the June 17 council doc)
- Fix README's dead `unified_search` example (§2.1)
- Add first-admin bootstrap gate — token or allowlist (§1.2)

**Half-day:**
- Extract shared `FixedTimeEquals` helper, delete 3 copies (§1.5)
- Add global `[AutoValidateAntiforgeryToken]` + selective opt-outs (§1.3)
- `ConcurrentDictionary` + atomic-swap `Rebuild()` in `MemoryIndex.cs` (§1.4)
- Add warning log to `ApplyAssignedModelProfile` silent-miss path (§4.2)

**Needs design/sequencing, not just typing:**
- TSK-0042 Step 2 (ChatServices file split) — already scoped by audit #9, sequencing already recommended there (TSK-0282 → TSK-0042 → TSK-0189 → TSK-0283 → TSK-0284)
- TSK-0276 pre-flight condition for nesting-depth enforcement (§2.2) — one added acceptance-criterion line, cheap now, expensive to forget later
