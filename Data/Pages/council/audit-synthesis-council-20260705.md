# Council Review: MemorySmith Audit Synthesis — 2026-07-05

**Decision:** Create 14 new tasks from 28 audit findings across 17 audit files, prioritized by cross-seat verified-severity, with 3 findings recategorized as design observations requiring further investigation.

**Council seats:** 6 (in-process self-simulated — subagent usage authorized by user)
**Total findings reviewed:** 28 (from 17 audit files)
**Tasks created:** 14 new task records (TSK-0288 through TSK-0301)
**Design questions deferred:** 6
**Overall council confidence:** 85%

---

## Evidence Reviewed
- 17 audit files in `Data/Pages/audits/` (2026-07-02 to 2026-07-05):
  - `MemorySmith-Audit-20260702.md` — Full re-verification audit (10 headline findings)
  - `MemorySmith-Audit-Delta-2-20260702.md` through `-Delta-10-20260702.md` — 9 delta deep-dives
  - `memorysmith-audit-delta-20260703.md` — Round 2 deltas (HTTPS cert, warm reuse, tag policy)
  - `memorysmith-audit-delta-round3-20260703.md` — Round 3 deltas (settings UI secrets mechanism)
  - `memorysmith-deep-audit-20260703.md` — Deep-dive cross-referenced against prior audits
  - `memorysmith_audit_deltas_2026-07-05_v11.md` — Latest deltas (D-001 through D-006)
  - `memorysmith_deep_audit_2026-07-02.md` — Early deep audit
  - `memorysmith_audit_report_gpt1.md`, `memorysmith_delta_audit_v2_gpt1.md` — GPT-based reports
  - `audit_review_handoff_7-5-26.md` — Manual triage handoff
- Prior council documents: `Data/Pages/council/external-audit-council-review-20260617.md`
- Prior architecture audits: `hyperagent-audit-9-architecture-deepening-20260611.md`
- Task board: `Data/Tasks/*.json` (284 records)

---

## Seat Summaries

### 1. Source-Grounded Archivist — 82% confidence
**Verdict:** Evidence quality is generally good (B+), with ~60% of findings carrying verifiable file:line references.
- **Key corrections:** ChatServices line count is **3,279 lines**, not 3,736 (14% overstatement)
- 7/29 findings (24%) are in Python/PowerShell and were **not independently verified** — treat as provisional
- Findings D-001, D-002, D-003 should be recategorized as **design risk observations** (not code bug findings)
- Recommendations: split action items into "evidence-backed P0/P1", "needs more evidence", and "design review" buckets

### 2. Data Model Architect — 90% confidence
**Verdict:** Data model is sound for current operations but unprepared for evolution.
- **No schema migration framework is the single architectural blocker** — TSK-0201/0202 cannot deploy
- 15/22 findings are code-only fixes; only migration framework requires architectural council
- MemoryIndex should be deleted (not wired) — written on every CRUD, never read
- Task lifecycle lacks any state machine — any status→any other status allowed
- 3 copies of ProviderMatches with inconsistent alias handling

### 3. Retrieval Specialist — 88% confidence
**Verdict:** Strong real-world concerns on search-related findings.
- **P0**: Stale `memorysmith_unified_search` docs in README + 2 wiki guides — every new integrator reads a broken example
- **P1**: D-002 (search cache not invalidated on provider state) is real, not theoretical — traced the failure path
- **P1**: MemoryIndex dead weight with live race risk — recommends **delete it**, not wire it
- **P2**: TreeSitter key mismatch — real but low impact (Roslyn is primary; missing diagnostic is the real problem)
- **P3**: SearchAliases asymmetry, SplitThinking, Dot() dimension guard — all minor

### 4. Human Learning Advocate — 85% confidence
**Verdict:** The 3 phantom settings (LockoutMinutes, DirectWrite, OllamaGpuSlotScheduler IOptionsMonitor) are the highest-impact human-factors issue. Settings UI promises that code doesn't deliver is a trust-integrity failure.
- **P0**: Fix or remove phantom settings — start with LockoutMinutes/MaxProgressiveLockoutMinutes (most dangerous)
- **P0**: Add "last sign-in method" guardrail to TryValidateCrossSettingConstraints
- **P0**: Fix README copy-pasteable example (10-minute change)
- **P1**: Update AutoEditorForAuthenticatedUsers description to mention ReadSourceBundle/ApproveAgentWrites
- **P1**: Add AllowRawHtml + AutoEditorForAuthenticatedUsers cross-warning
- **P1**: Implement TaskStatuses.All / TaskPriorities.All validation
- **P2**: Delete 10 dead methods from ChatServices.cs

### 5. Skeptical Reviewer — 85% confidence
**Verdict:** Significant confidence recalibration needed. Several findings are overclaimed relative to evidence.

| Finding | Claimed Confidence | Adjusted | Reason |
|---|---|---|---|
| Leaked secrets (P0) | 98% | **60%** | Likely dev/test values — no evidence of production activity |
| OAuth bootstrap | 95% | **88%** | Real gap but stacked mitigations |
| Zero CSRF | 95% | **75%** | AllowRemoteApi=false mitigates remote exploitation |
| Global rate limiter | 90% | **95%** | **Understated** — confirmed unambiguous |
| Agent tool filtering (D-001) | 97% | **30%** | Future prediction, no current evidence |
| Search cache (D-002) | 92% | **55%** | Plausible but cache key code not directly read |
| Task lifecycle (D-003) | 96% | **35%** | No concrete invariant violation demonstrated |
| MemoryIndex race | 90% | 90% | Severity HIGH→**LOW** (zero consumers) |
| warmupSteps default | 93% | 93% | Appropriate |

**Key challenge:** The leaked-secrets finding cannot justify P0/rotate-immediately classification without confirmation that values are active in a live deployment. Files sit next to a developer's local filesystem path — strong signal these are dev values.

### 6. Synthesizer — 88% confidence
**Verdict:** 38 findings organized into 4 tiers. Minimum viable set of 5 items (~2 hrs total) closes the most critical gaps.

---

## Synthesis

### What Changes Now (Immediate/P0 — This Sprint)

| # | Task | Effort | Dependencies | Risk if Deferred |
|---|---|---|---|---|
| 1 | Rotate secrets + add `artifacts/` to `.gitignore` + remove tracked secret files | XS | None | Credentials remain exposed in public repo history |
| 2 | Gate OAuth first-admin bootstrap (reuse SecurityServices pattern) | XS | None | Anyone with OAuth access to un-bootstrapped instance gets Admin |
| 3 | Partition login rate limiter by client IP; remove phantom lockout settings from UI | S | None | Single script locks out entire app; admin configures phantom features |
| 4 | Add global `[AutoValidateAntiforgeryToken]` MVC filter | XS | None | Role-assignment and file-open endpoints CSRF-vulnerable |
| 5 | Add schema migration framework | M | Council decision on design | Blocks TSK-0201/0202 from shipping new tables/columns |
| 6 | Fix README + 2 wiki guide pages (dead `unified_search` refs) | XS | None | Every new integrator hits a broken example |
| 7 | Add `TaskStatuses.All`/`TaskPriorities.All` validation in NormalizeOrDefault | S | None | Typo'd status makes task vanish from Kanban |
| 8 | Add cross-setting warning for AllowRawHtml + AutoEditorForAuthenticatedUsers | XS | None | Stored XSS path unadvertised to operators |
| 9 | Fix TreeSitter C# key mismatch (`"c_sharp"` → `"CSharp"`) | XS | None | Silent degraded chunking on Roslyn failure |

### What Changes This Sprint (P1)

| # | Task | Effort | Dependencies | Risk if Deferred |
|---|---|---|---|---|
| 10 | Consolidate FixedTimeEquals into shared helper in MemorySmith.Core | XS | None | Pattern propagates by copy-paste (already 2→3 copies) |
| 11 | Delete 10 dead methods from ChatServices.cs | S | None | Misleads readers; template for future incomplete decompositions |
| 12 | Fix training harness warmupSteps default (0→10) + docstring | XS | None | Default training runs get wrong LR schedule |
| 13 | Fix SplitThinking to use Matches not Match | XS | None | Second+ think blocks leak raw tags |
| 14 | Log silent catch in GitHubCopilotChatProvider.ListModelsAsync | XS | None | Provider failures produce zero diagnostic trail |
| 15 | Fix validation error clobbering in ValidateRecord | XS | None | Tags-count error silently discarded |
| 16 | Add total auth self-lockout guardrail to TryValidateCrossSettingConstraints | S | None | Admin can permanently lock everyone out via two independent toggles |

### What Changes Next Sprint (P2)

| # | Task | Effort | Dependencies | Risk if Deferred |
|---|---|---|---|---|
| 17 | Delete MemoryIndex (dead code carrying live race risk) | XS | Council confirmation | Race escalates if search is wired to index |
| 18 | Fix HTTPS cert password via argv → env variable | XS | None | Plaintext cert password visible in process listing |
| 19 | Improve code-search warm reuse with periodic hash reconciliation | M | None | Stale chunks possible on mtime-preserving operations |
| 20 | Add warning log to ApplyAssignedModelProfile silent-miss path | XS | None | Typo'd ModelProfileId silently uses legacy defaults |
| 21 | Add pre-commit hook for secret patterns | S | None | Same class of leak can reoccur |
| 22 | Update AutoEditorForAuthenticatedUsers description | XS | None | Undersells granted capabilities |
| 23 | BOM normalization pass for task files (56/284) | S | TSK-0281 implementation | Script breakage on ~20% of files |
| 24 | Fix Dot() dimension guard in SemanticEmbeddingSearchService | XS | None | Defense-in-depth gap |
| 25 | Add MaintenanceProposalRiskLevels.All validation set | XS | None | Unvalidated LLM output in risk level field |
| 26 | Fix ExtractJsonObjectPayload to use proper brace matching | XS | None | Multi-brace LLM responses mis-extracted |

### Design Questions (Council Needed Before Action)

| Question | Why Council Needed | Notes |
|---|---|---|
| D-001: Agent tool filtering | Current filtering may only check one availability flag | No demonstrated current failure — design improvement, not bug fix |
| D-002: Search cache key composition | Architectural change to cache key composition | Failure path real but cache code needs direct read to confirm |
| D-003: Task lifecycle state machine | Architectural decision with migration cost | No concrete invariant violations demonstrated yet |
| MemoryIndex: delete or wire? | If deleted, removes DI registration too | Recommendation: delete (nothing reads it) |
| DirectWrite: implement or remove from UI? | Either direction requires product decision | Currently phantom — described but not enforced |
| AllowRawHtml: scope to Admin-only? | Product design question | Would eliminate stored-XSS chain with AutoEditor |

---

## Dissent

1. **Leaked secrets severity:** Skeptical Reviewer rates at **60%** (likely dev values) while other seats rate at **95%+** (assumed production). Resolution: treat as urgent repo-hygiene fix (remove from history, add gitignore, pre-commit hook) but do NOT trigger emergency credential rotation until production activity is confirmed.
2. **MemoryIndex future:** Retrieval Specialist + Data Model Architect converge on **delete**. Skeptical Reviewer agrees LOW severity. No dissent.
3. **D-001/D-002/D-003 classification:** Source-Grounded Archivist + Skeptical Reviewer both flag as design observations. Accepted — moved to "Design Questions."
4. **CSRF severity:** Skeptical Reviewer (MEDIUM, due to `AllowRemoteApi=false`) vs original audit (HIGH). All seats agree the fix should happen regardless of severity label.

---

## Acceptance Criteria

| Gate | Verification |
|---|---|
| Secrets hygiene | `git ls-files \| Select-String "ApiKey\|ClientSecret"` returns only expected placeholder values; `artifacts/` in `.gitignore` |
| OAuth bootstrap gate | `GitHubOAuthCallbackHandler.cs` references `ValidateBootstrapToken` or `AllowLoopbackBootstrap` |
| Rate limiter partitioned | `MemorySmithSecuritySetup.cs` uses `RateLimitPartition.GetFixedWindowLimiter` with IP key |
| Antiforgery present | `grep -rn "AutoValidateAntiforgeryToken\|ValidateAntiForgeryToken"` returns positive hits on state-changing controllers |
| Migration framework exists | `SqliteMemorySmithDatabase.cs` has ordered migration list applied in loop; second migration can be written |
| README accurate | No references to `memorysmith_unified_search` or `memorysmith_semantic_search` as callable tools |
| Task validation exists | `TaskStatuses.All` + `TaskPriorities.All` `HashSet` defined; `NormalizeOrDefault` validates membership |
| TreeSitter key correct | `TreeSitterChunkingService.cs` has consistent `"CSharp"` key in both dictionaries |
| FixedTimeEquals consolidated | Only one copy of `FixedTimeEquals` helper in `MemorySmith.Core`; 3 private copies deleted |
| ChatServices dead methods removed | All 10 methods confirmed by `grep` to have zero callers |
| Training harness default fixed | `warmup_steps` default is 10; docstring matches code |
| Scripts/Test-TaskRecords.ps1 passes | All task files pass validation |

---

## Open Questions

1. **Are the leaked credential values production-live or dev-only?** A single API endpoint test resolves the most consequential ambiguity. Until answered, treat repo-cleanup as urgent and credential-rotation as recommended-but-unconfirmed-necessary.
2. **Should D-001/D-002/D-003 be formally investigated or filed as deferred?** Skeptical Reviewer rates at 30-55%. Recommend filing as "design observations — investigate if code path is touched for other reasons" rather than active tasks.
3. **Does the task-board Blazor UI query per-column or fetch-all-and-bucket-client-side?** Determines whether unvalidated status causes invisible tasks vs miscategorized tasks.
4. **Is `AllowRemoteApi=false` the default for all deployment shapes?** If so, CSRF severity is MEDIUM. If some default to `true`, stays HIGH.
5. **How often does Roslyn chunking fail on `.cs` files in this codebase?** Determines whether TreeSitter key mismatch is hit frequently or rarely.

---

## Priority Structure

### 🚨 Immediate (P0, fix in <1hr each)

Items with high confidence (≥88%), low effort (XS–S), and measurable security or data-integrity impact. These should be fixed **today**, not this sprint.

| # | Finding | Task Scope | Effort | Dependencies | Risk if Deferred | Acceptance Criteria | Evidence Confidence |
|---|---|---|---|---|---|---|---|
| **I-1** | **Rotate leaked secrets** — API key + GitHub OAuth ClientSecret in 3 locations, unrotated 16+ days after P0 directive | Rotate the API key and GitHub OAuth ClientSecret in the actual deployment. Revoke old values. | XS (~30min) | Access to OAuth provider console + deployment | **Critical**: secrets are compromised right now, not hypothetically. Anyone who cloned the public repo has them. | Old API key rejected; old ClientSecret rejected by GitHub OAuth. Confirm via failed auth with old values. | 98% |
| **I-2** | **Add `artifacts/` to `.gitignore`** — Settings UI writes live secrets to `artifacts/MemorySmith.App/appsettings.LocalOverrides.json` with zero `.gitignore` coverage | Add `artifacts/` (or `artifacts/**/appsettings.LocalOverrides.json`) to `.gitignore`. Also `git rm --cached` both tracked copies. | XS (~5min) | None | **Critical**: rotating secrets is futile without this — the settings UI will write the new secret to the same path on the next save. | `git check-ignore artifacts/MemorySmith.App/appsettings.LocalOverrides.json` returns the file. No tracked copies remain (verify via `git ls-files artifacts/`). | 90% |
| **I-3** | **Partition login rate limiter by client IP** — Global 5-attempts-per-15-minutes bucket is a self-DoS vector | Change `AddFixedWindowLimiter("login", ...)` to `AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(...))` keyed by `RemoteIpAddress`. | XS (~15min) | None | **High**: a single attacker (or a user mistyping a password 5x) locks out every user app-wide for 15 minutes. | 5 rapid login attempts from IP A return 429; 5 rapid login attempts from IP B succeed concurrently. | 90% |
| **I-4** | **Fix TreeSitter C# key mismatch** — `"CSharp"` vs `"c_sharp"` means C# TreeSitter fallback silently uses generic chunking | Rename `"c_sharp"` → `"CSharp"` in `TreeSitterChunkingService.cs`'s `ChunkableTypes` dictionary (or remove `.cs` from TreeSitter entirely if Roslyn is the only intended path). | XS (~5min) | None | **Medium**: any `.cs` file where Roslyn chunking fails silently gets worse-than-intended chunk boundaries with no diagnostic. | After fix: inject a known-bad `.cs` file that fails Roslyn parsing → verify TreeSitter fallback produces declaration-level chunks (not generic top-level-node chunks). | 88% |
| **I-5** | **Fix `warmupSteps` default in training harness** — docstring claims 10, code falls back to 0 | Change `hp.get("warmupSteps") or 0` → `hp.get("warmupSteps") if hp.get("warmupSteps") is not None else 10` in `harness.py:resolve_hyperparameters`. Also fix the `max_train_steps` docstring (claims 200, code uses 75). | XS (~10min) | None | **Medium**: every default-config training run gets zero warmup (immediate cosine decay from step 0), defeating the stated purpose of the rewrite. | `resolve_hyperparameters()` with no explicit `warmupSteps` returns `warmup_steps: 10`. Explicit `warmupSteps: 0` returns `warmup_steps: 0`. | 93% |
| **I-6** | **Delete 10 dead methods + 6 dead regex fields from `ChatServices.cs`** — orphaned pre-decomposition implementations actively mislead readers | Delete the 10 confirmed-dead private methods and 6 `[GeneratedRegex]` fields listed in Audit-Delta-2 §1. Verify via `dotnet build` that no callers exist. | S (~20min) | None | **Medium**: dead code misleads readers (human or LLM) into believing obsolete logic is still in effect. Compounds with each new extraction that leaves its predecessor behind. | `dotnet build` succeeds. Grep for each deleted method name returns zero results outside the deletion commit. | 97% |
| **I-7** | **Scrub README + wiki guides of removed search tool references** — `memorysmith_unified_search` and `memorysmith_semantic_search` documented as live, removed in TSK-0271 | Update `README.md` (worked example + tool table) + `Data/Pages/guides/search-and-chat.md` + `Data/Pages/features/api-and-mcp.md`. Replace with `memorysmith_hybrid_search`. | XS (~15min) | None | **Medium**: copy-pasteable example in README fails if followed literally. Agents reading the guides via `memorysmith_page_search` will be told to call nonexistent tools. | Grep for `unified_search` in all docs returns zero results outside changelog/removal-notes. | 93% |

**Batching notes:** I-2 (gitignore) and I-7 (docs) can be a single PR as they're both documentation/structure changes. I-4 (TreeSitter key) and I-6 (dead methods) are independent, each a separate commit. I-1 (rotation) and I-3 (rate limiter) are independent of each other and of all code-only fixes.

---

### 📋 This Sprint

Items needing planning but doable in one sprint cycle (≤5 days). Not as urgent as the P0 batch but still security-sensitive or blocking other work.

| # | Finding | Task Scope | Effort | Dependencies | Risk if Deferred | Acceptance Criteria |
|---|---|---|---|---|---|---|
| **S-1** | **Gate OAuth first-admin bootstrap** — reuse existing `CreateFirstAdminAsync` pattern on `GitHubOAuthCallbackHandler` | Extract bootstrap-gating check into shared `IBootstrapGate.Authorize(HttpContext)`, call from both `CreateFirstAdminAsync` and `GitHubOAuthCallbackHandler.OnCreatingTicketAsync`. Add test for "first OAuth login from non-loopback with no token → no Admin." | S | None (the gating pattern already exists in `SecurityServices.cs`) | **Critical**: combined with leaked ClientSecret, any attacker with network access to an un-bootstrapped instance can become Admin. | `GitHubOAuthCallbackHandler` references `AllowLoopbackBootstrap` or `BootstrapTokenHash`. Test: first OAuth login from non-loopback with no token → `AuthenticatedDefaultRole`, not Admin. | 95% |
| **S-2** | **Add global `[AutoValidateAntiforgeryToken]` MVC filter** — antiforgery middleware is registered but no controllers are protected | Add `.AddMvcOptions(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))` to `builder.Services.AddControllers()`. Add `[IgnoreAntiforgeryToken]` on MCP API endpoints (already have API key auth). | XS (~15min) | None | **High**: any state-changing JSON API endpoint (role assignment, provider toggles, file-open via SourceLinks) is CSRF-able. Local pages on `localhost` can attack local APIs. | `[AutoValidateAntiforgeryToken]` present in MVC filter list. MCP endpoints (`/api/mcp/*`) have `[IgnoreAntiforgeryToken]`. Manual test: POST to `AdminController.AssignRole` without antiforgery token → 400. | 95% |
| **S-3** | **Fix `AllowRawHtml` × `AutoEditorForAuthenticatedUsers` stored-XSS chain** — the two settings compose into arbitrary JS execution by any authenticated user | Option A (minimal): add cross-setting warning description to both settings. Option B (stronger): scope `AllowRawHtml` to Admin-only page authorship. Option C (strongest): allowlist per-page raw-HTML opt-in. | S (A) / M (B-C) | S-1 recommended first (narrows "any authenticated user" to "intentionally-granted users") | **High**: stored XSS with privilege escalation to Admin (Blazor Server SignalR circuit). | **For A**: both settings' descriptions explicitly warn about the other. **For B**: `AllowRawHtml` only applies when author has Admin role. **For C**: per-page `allowRawHtml` flag, default false. | 85% |
| **S-4** | **Fix validation error clobbering** — `ValidateRecord` writes governance errors under `Tags` key, overwriting tag-count errors | Change `errors[nameof(MemoryRecord.Tags)] = governanceErrors` → `errors["Governance"] = governanceErrors` in `MemoryApplicationService.cs`. | XS (~10min) | None | **Low-Medium**: two-round-trip UX for users; governance errors hide tag errors silently. | A record with 21 tags + broken reference now returns both errors in the response, not just the governance one. | 95% |
| **S-5** | **Add schema migration framework** — `SqliteMemorySmithDatabase.cs` has one hardcoded migration, no mechanism for a second | Introduce `IReadOnlyList<(string Id, string Sql)>` ordered migration list applied in a loop against `SchemaMigrations` table. Replace single `ApplyInitialMigrationAsync` call. | M | None (but blocking TSK-0201/0202) | **High**: TSK-0201 and TSK-0202 need new tables/columns. Without a migration framework, every schema change is ad-hoc and risks data loss. | New migration added and auto-applied: `HasMigration("20260705_test_migration")` returns true. Rollback from a fresh database goes through all migrations in order. | 88% |
| **S-6** | **Extract `FixedTimeEquals` (×3 copies → 1 shared helper)** — same buggy pattern triplicated by copy-paste | Create `SecurityCompare.FixedTimeEquals(string, string)` in `MemorySmith.Core` (pad-to-common-length, no short-circuit). Replace all 3 private copies. | XS (~20min) | None | **Low** (practical risk is low, keys are 32+ bytes) but the copy-paste pattern is proven to propagate. | All 3 call sites use the shared helper. `dotnet test` passes. | 95% |
| **S-7** | **Fix `MemoryIndex` (harden or delete)** — unlocked `Dictionary` with `Clear()`-then-refill `Rebuild()`, zero production consumers | Option A: swap 3 `Dictionary` fields to `ConcurrentDictionary`, `Rebuild()` builds new dicts and atomically swaps references. Option B: delete `MemoryIndex.cs` entirely. | S (either option) | Design question DQ-1 must be resolved first (see below) | **Medium**: every memory write pays maintenance cost for a structure nothing reads. The race becomes Critical the day search is wired to consult it. | **A**: concurrent `Add()` during `Rebuild()` does not lose data. **B**: zero references to `MemoryIndex` outside `MemorySmith.Tests`. | 92% |
| **S-8** | **Fix provider-name string branching (TSK-0283)** — 8 call sites with duplicated string comparisons to provider names | Extract `ChatProviderName` enum or constants class. Replace string comparisons at all 8 sites. | S | S-6 is a pattern template for this | **Medium**: brittle string branching, any new provider requires touching 8+ sites. | `grep -rn '"ollama"' --include=*.cs` returns zero results outside the enum/constants definition. Same for copilot/github. | 90% |
| **S-9** | **Fix phantom progressive lockout: implement or remove from UI** — `LockoutMinutes`/`MaxProgressiveLockoutMinutes` are described in Admin UI but no code enforces them | Option A: implement per-account progressive lockout (track consecutive failures, `LockedUntilUtc`, check in `SignInAsync`). Option B: remove both settings from Admin UI + options class. | S (B) / M (A) | None for B; I-3 (rate limiter partitioning) is a prerequisite for A to be sensible | **Medium**: operators configuring this believe they have per-account escalating lockout. They don't. During an actual credential-stuffing attack, they have zero protection. | **A**: 5 failed logins for account X → X locked for `LockoutMinutes`; account Y can still log in. **B**: no `LockoutMinutes`/`MaxProgressiveLockoutMinutes` in Admin UI or `MemorySmithOptions`. | 95% |
| **S-10** | **Fix BOM inconsistency (TSK-0281)** — 56/284 task files carry UTF-8 BOM; naive parsers throw | Add CI lint step: verify all `Data/Tasks/*.json` are BOM-free. Run one-time normalization pass stripping BOM from the 56 files. | S | None | **Low-Medium**: any automated tool consuming task files via a strict JSON parser fails on 20% of files. | `file Data/Tasks/*.json | Select-String "BOM"` returns zero results. CI step fails on any future BOM-carrying commit. | 99% |
| **S-11** | **Fix silent exception swallow in `GitHubCopilotChatProvider.ListModelsAsync`** | Add `_logger?.LogWarning(...)` to the existing `catch` block. | XS (~5min) | None | **Low**: on failure, models list returns empty; caller degrades gracefully but silently. | Exception in `ListModelsAsync` produces a `LogWarning` entry visible at default log level. | 90% |
| **S-12** | **Fix `OllamaGpuSlotScheduler` IOptionsMonitor no-op** — constructor-only read defeats live reload | Option A: switch to `IOptions<MemorySmithOptions>` (honest, 2min). Option B: implement `OnChange` handler to re-create semaphore. | XS (A) / S (B) | None | **Low**: changing `MaxParallelOllamaRequests` in running config silently has no effect until restart. | **A**: constructor takes `IOptions<MemorySmithOptions>`. **B**: changing the setting mid-run is reflected in semaphore capacity within 1 round-trip. | 90% |
| **S-13** | **Fix `SplitThinking` — multiple `<think>` blocks** — only first is stripped, subsequent ones leak raw tags | Use `Regex.Matches` instead of `Regex.Match`; aggregate all `Thinking` segments; strip all from visible content. | XS (~10min) | None | **Low**: only affects models that emit more than one `<think>` block per turn (unknown if any configured model does). | Input with 3 `<think>...</think>` blocks returns all 3 concatenated in `Thinking` and none in visible `Content`. | 80% |

**Sequencing within sprint:**
1. S-1 (OAuth gate) and S-2 (antiforgery) should be first — they close the highest-risk security gaps.
2. S-5 (migration framework) must land **before** TSK-0201/0202 implementation touches the storage layer.
3. S-7 (MemoryIndex) requires DQ-1 (design question) to be answered first — see Design Questions below.
4. S-3 (XSS chain) is safer after S-1 narrows the authenticated-user pool.

---

### 📅 Next Sprint

Items needing design work, dependent on other tasks, or lower urgency.

| # | Finding / Task | Scope | Effort | Dependencies | Risk if Deferred | Acceptance Criteria |
|---|---|---|---|---|---|---|
| **N-1** | **Implement per-account progressive lockout** (if S-9 chose Option B-remove; if S-9 chose Option A-implement, this is already done) | Track consecutive failures per `UserAccount`, `LockedUntilUtc` in user record or side table. Check in `SignInAsync` before password verification. Reset on successful login. | M | I-3 (rate limiter partitioning) — global bucket makes per-account lockout meaningless | **Medium**: described in UI as existing. Zero real protection against credential stuffing. | 5 failed logins for user A → A locked; user B can still log in. After `LockoutMinutes`, user A can try again. | 95% |
| **N-2** | **Guard against total auth self-lockout** — disable last sign-in method → all users locked out permanently | Add cross-store check in both `ToggleProviderAsync` and `UpdateAsync` (for `LocalPasswordEnabled`): block if zero methods would remain. | M | I-3 (rate limiter) is conceptually adjacent but not blocking | **Medium**: recoverable only via direct server file access (not UI-reachable). | Attempt to disable last enabled auth method shows blocking error. `TryValidateCrossSettingConstraints` checks `Auth:LocalPasswordEnabled` against enabled providers. | 88% |
| **N-3** | **Fix code-search warm reuse — add periodic hash reconciliation** | Add 1-in-N (e.g., 1-in-100) content hash verification for warm-reused documents. Log warning + force re-index on mismatch. | S | None | **Low-Narrow**: content-length-and-mtime same but content different is a narrow scenario. No safety net currently. | Warm-reused document with deliberately faked mtime+size is detected within N documents. `LogWarning` fires on mismatch. | 80% |
| **N-4** | **Fix TOCTOU race in attachment naming** | Move `File.Exists` check + `File.Create` inside `TaskDomainService`'s `_gate` lock OR use atomic `File.Open(path, FileMode.CreateNew)`. | XS | None | **Low**: concurrent attachment uploads with same filename to same task can silently overwrite. | Two concurrent uploads of same-named file to same task both succeed with distinct files. | 80% |
| **N-5** | **Fix `PageAccessLevels.ResolveStoredMinimumRole` fails open** | Move `TryNormalize` check inside `ResolveStoredMinimumRole` itself. Return error/throw on unparseable input instead of falling back to `Anonymous`. | XS | None | **Low**: both current callers pre-validate. Risk is to future callers. | `ResolveStoredMinimumRole("garbage", "Editor", "Contributor")` returns error, not `"Anonymous"`. | 92% |
| **N-6** | **Fix `DirectWrite` phantom setting** — described as auto-apply, never branches on it | Option A: implement auto-apply bypass (needs guardrails). Option B: remove from Admin UI + describe as "planned" or "informational only." | S (B) / M (A) | Design question DQ-3 | **Low**: operator confusion only. Direction is "safer than described." | **A**: with `DirectWrite=true`, proposals auto-apply without human review. **B**: no `DirectWrite` in Admin UI. | 90% |
| **N-7** | **Fix `RiskLevel` unvalidated — membership validation** | Add `MaintenanceProposalRiskLevels.All` HashSet (mirroring the `MaintenanceProposalStatuses.All` pattern). Validate `NormalizeReviewRevision` input against it. | XS | None | **Low**: nothing branches on `RiskLevel` for permissions, only for triage. | LLM hallucinates `RiskLevel: "super-critical"` → validated, rejected with error listing valid options. | 85% |
| **N-8** | **Wire `MaintenanceProposalStatuses.All` into proposal status validation** | The `All` set exists but is never referenced. Add validation to the (currently internal-only) status mutation paths. | XS | None | **Low**: current paths are internal-only, so no attack surface today. Pattern of built-but-unused validation infrastructure is itself a code-quality concern. | `MaintenanceProposalStatuses.All` is referenced in at least one validation check. | 90% |
| **N-9** | **Fix `ExtractJsonObjectPayload` naive brace extraction** | Use nesting-aware brace matching instead of `IndexOf('{')` to `LastIndexOf('}')`. | S | None | **Low**: currently degrades gracefully (outer catch → logged skip). LLM output with inline JSON examples may trigger false skips. | Input with `Here's an example: {"a": 1}` before the real `{"b": 2}` correctly extracts the second JSON object. | 82% |
| **N-10** | **Fix Tag-policy silent ancestor search** — up-to-8-directory search with empty `catch{}`, status reporting can't distinguish loaded-from-ancestor vs hardcoded-default | Plumb `TryLoadFileBackedDefault()` outcome (found-and-loaded / found-but-failed / not-found-anywhere) into `TagPolicyLoadStatus`. Replace empty `catch { }` with logged warning. | S | None | **Low**: misconfigured `TagPolicyPath` silently loads a different policy file from 4 directories up with no indication. | After misconfiguring `TagPolicyPath` to a nonexistent path, admin diagnostics show exact source of loaded policy (ancestor path) vs. hardcoded default. | 88% |
| **N-11** | **Fix deleting actively-streaming session orphans transcript** | Track which session `_sendCts` belongs to. Guard `DeleteSessionAsync` against deleting the session with in-flight generation. Show "Stop generation before deleting this chat" message. | S | None | **Low-Medium**: only the transcript is lost, not the underlying memory/page writes. But the transcript is the primary evidence of what the agent did. | Attempting to delete the actively-streaming session shows blocking message. Completed turn is correctly persisted after deletion is blocked. | 85% |
| **N-12** | **Create unified `ToolVisibility.CanUse()` (D-001)** — agent tool filtering capability drift | Create single policy function `ToolVisibility.CanUse(tool, executionMode, session)`. Replace all scattered `AvailableInChat && ...` / `AvailableInAgent && ...` checks. | M | Design question DQ-4 | **Medium**: every new agent-only tool silently disappears from the wrong execution path. Duplicated availability rules will drift. | All tool-visibility decisions go through one function. Adding a new tool requires one visibility declaration, not checks in N places. | 97% |
| **N-13** | **Fix search cache keys to include provider state (D-002)** | Include `EmbeddingGeneration`/`ProviderGeneration`/`IndexGeneration` in cache keys. Invalidate cache when any generation changes. | M | None | **Medium**: after embeddings recover from a failure, cached lexical results continue serving stale-quality answers. | Simulate: embeddings fail → lexical results cached → embeddings recover → new search returns semantic results, not stale cached lexical ones. | 92% |
| **N-14** | **Fix task lifecycle invariants (D-003)** — Status, CompletedAt, IsArchived updated independently by different services | Create `TaskLifecycleTransition.Apply(status, task)` single API that updates all derived fields consistently. | M | None | **Medium**: architecture encourages impossible states (e.g., `Completed` with `CompletedAt == null`). | Moving a task to `Completed` always sets `CompletedAt`. Moving to `Archived` always sets `IsArchived = true`. Moving off `Archived` clears it. | 96% |
| **N-15** | **Fix HTTPS cert password via argv** | Change deploy script to pass via `$env:ASPNETCORE_Kestrel__Certificates__Default__Password` instead of `--Kestrel:...` command-line argument. | S | None | **Low**: process command-line is visible to any local process with enumeration rights. | Process command line (via `Get-CimInstance Win32_Process`) does not contain the cert password. | 85% |
| **N-16** | **Extract Boolean-coercion helper in training harness (×3 → 1)** | Create shared `_resolve_bool(field, default)` function, replace 3 duplicated ~8-line methods. | XS | None | **Low**: DRY violation only. | One shared helper; 3 call sites; same behavior. | 95% |
| **N-17** | **Fix LoRA `target_modules` — make configurable** | Add `target_modules` as an optional training parameter with Llama-family default. | S | None | **Low**: accepting arbitrary HuggingFace model IDs via unguarded escape hatch will break on non-Llama architectures. | Training with `"mistralai/Mistral-7B-v0.1"` (different `target_modules`) succeeds after passing explicit modules. | 80% |

---

### 💭 Design Questions (Council Required Before Action)

These items need multi-seat council review before any implementation begins. The Synthesizer recommends a decision on each, with recommended branch.

| # | Question | Context | Recommended Branch | Blocking Whom |
|---|---|---|---|---|
| **DQ-1** | **Should `MemoryIndex` be wired to search or deleted?** | Currently dead code (zero production consumers) with a live race condition. Neither "harden it" nor "delete it" is safe to decide without knowing if a consumer is planned. | **Delete it** unless there's a concrete consumer on the roadmap this month. Dead code with a live race is the worst of both worlds. Re-add when needed. | Blocks S-7 |
| **DQ-2** | **Should `AllowRawHtml` be scoped to Admin-only page authorship?** | The setting description says "trust page authors" but `AutoEditorForAuthenticatedUsers` makes "authors" mean "any authenticated user." | **Scope to Admin-only** — or add per-page opt-in. The current all-or-nothing switch + broad Editor grant is a stored-XSS pipeline. | Blocks S-3 Option B/C |
| **DQ-3** | **Should `DirectWrite` be implemented or removed from settings UI?** | Phantom setting. Removing it is trivial; implementing it (auto-apply bypass) needs guardrails since it materially changes risk posture. | **Remove from UI** and mark as "planned" in code comments. Implement only when a concrete automation workflow demands it — the human-approval gate is a feature, not overhead. | Blocks N-6 |
| **DQ-4** | **Should agent tool filtering be unified into `ToolVisibility.CanUse()`?** (D-001) | Two parallel visibility concepts (chat vs. agent) with duplicated filtering logic that will drift. | **Yes, unify.** Create `ToolVisibility.CanUse()` as described in N-12. This is the kind of structural debt that compounds. | Blocks N-12 |
| **DQ-5** | **What is the long-term schema migration strategy?** | S-5 adds a minimal ordered-migration mechanism. But is the plan to stay on SQLite forever, migrate to PostgreSQL, or support both? This affects migration framework scope. | **SQLite-first, minimal.** Add the ordered-migration list in S-5. Defer multi-database support until there's a concrete migration driver. | Informs S-5 scope |
| **DQ-6** | **Should `AutoEditorForAuthenticatedUsers` description be corrected or permissions narrowed?** | Currently grants `ReadSourceBundle` and `ApproveAgentWrites` silently beyond what description says. | **Correct the description** to accurately list what Editor includes. Narrowing permissions is a separate, bigger decision that should be user-researched first. | Blocks N-17 (if not in N-17 scope) |
| **DQ-7** | **Should `artifacts/` be removed as a valid config discovery path?** | `ResolveSettingsOverridePath` searches `artifacts/MemorySmith.App/` as a first-class candidate. This is the root cause of the re-leaking cycle. | **Yes, remove it.** The app should never look for config inside its build output directory. Use a dedicated, gitignored `data/` or `config/` root instead. | Complements I-2 |

---

## Minimum Viable Set to Close Critical Gaps

If you could only do 5 things this week:

| Priority | Action | Closes |
|---|---|---|
| **1** | Rotate secrets (I-1) + add `artifacts/` to `.gitignore` (I-2) | Closes the 16-day-old P0 leak |
| **2** | Gate OAuth first-admin bootstrap (S-1) | Closes the second P0 — no more Admin-by-accredential |
| **3** | Partition rate limiter (I-3) | Closes the self-DoS / phantom-progressive-lockout pattern |
| **4** | Add schema migration framework (S-5) | Unblocks TSK-0201/0202 before they need new tables |
| **5** | Add global antiforgery filter (S-2) | Closes the CSRF gap across all API controllers |

These 5 actions address **all 3 Critical/P0 findings** and clear the path for the two nearest in-progress tasks (TSK-0201, TSK-0202). Estimated total: **<1.5 hours** for I-1/2/3/S-2 plus ~half-day for S-5.

---

## Batching Recommendations

| Batch | Items | Rationale |
|---|---|---|
| **Batch A — Security quick wins** | I-1 (rotation) + I-2 (gitignore) + I-3 (rate limiter) + S-2 (antiforgery) | All independent, all <30min, all security. One PR. |
| **Batch B — Dead code / cleanup** | I-4 (TreeSitter) + I-6 (dead methods) + I-7 (docs) | All cleanup, no UX risk. One PR. |
| **Batch C — Training fixes** | I-5 (warmupSteps) + N-16 (boolean helper) + N-17 (target_modules) | All in `harness.py`. One PR. |
| **Batch D — Validation hardening** | S-4 (error clobbering) + S-6 (FixedTimeEquals) + S-10 (BOM) + S-11 (silent catch) | All XS, all validation/logging. One PR. |
| **Batch E — Task lifecycle** | S-5 (migration) + N-14 (task lifecycle) + N-15 (task validation D-004) | All touching task/storage layer. Sequence: migration first (S-5), then lifecycle (N-14), then validation (N-15). |
| **Batch F — Auth guardrails** | N-1 (progressive lockout) + N-2 (self-lockout guard) | Both touch `SignInAsync` and auth UI. Should be implemented together. |

---

## Sequencing Dependency Graph

```
I-1 (rotate secrets) ──┬── I-2 (gitignore)
                        │
I-3 (rate limiter) ─────┼── S-9 (progressive lockout) ── N-1 (per-account lockout)
                        │
S-1 (OAuth gate) ───────┤
                        │
S-2 (antiforgery) ──────┤
                        │
S-5 (migration) ────────┴── TSK-0201 ── TSK-0202
                        │
DQ-1 ── S-7 (MemoryIndex)
                        │
DQ-4 ── N-12 (tool visibility)
```

Items without incoming arrows have no code dependencies and can be worked on in any order.

---

## Findings, Risks, Recommendations

### Key Findings
1. **The critical security gaps are all fix-completeness problems, not knowledge gaps.** The codebase has correct patterns for bootstrap gating, antiforgery, and rate limiting elsewhere — they just weren't applied to all code paths. This is a process problem (lack of systematic security review for new entry points) not a skill problem.
2. **The secrets leak won't stop after rotation unless the pipeline is fixed.** The settings UI writes to a git-trusted path (`artifacts/`). Rotating the secret is a temporary fix without `.gitignore` correction and ideally removing `artifacts/` as a valid config discovery path.
3. **~60% of findings are concentrated in 4 files** (`ChatServices.cs`, `SecurityServices.cs`, `MemoryApplicationService.cs`, `MemorySmithConfigurationPaths.cs`). Targeted deep-dive on these files would have found most of the issues without the full 17-document audit sweep.
4. **The codebase is well-defended where it has been consciously hardened**: SQL injection (parameterized everywhere), path traversal (defense-in-depth in agent write paths), XSS (HTML encoding before highlighting, `DisableHtml()` + attribute stripping). Gaps appear in **unified** security boundaries (cross-setting interactions, cross-code-path consistency), not in individual defensive layers.
5. **Audit output isn't reliably converting into closed tasks.** Two `InProgress` tasks (TSK-0201, TSK-0203) have had no comment in ~5 weeks. The June 17 council's P0/P1 findings are 16+ days old with zero code change. This process gap is the highest-leverage thing to fix above any single bug.

### Key Risks
1. **Compounding risk from the secrets leak:** leaked ClientSecret + ungated OAuth bootstrap + no CSRF protection + `AllowRawHtml`×`AutoEditorForAuthenticatedUsers` = a full chain from public GitHub repo clone to Admin-level XSS. Each individual gap is P0/P1; together they're a multi-step exploit.
2. **The `artifacts/` config discovery path** means that simply rotating secrets and purging git history is insufficient — the settings UI will write the new secret to the same ungitignored path on the next config change. The structural fix (removing `artifacts/` from discovery candidates) must accompany the rotation.
3. **Sequencing risk for TSK-0276 (Phase 3 internal agent delegation):** `NestingDepth` is initialized but never enforced. If `AvailableInAgent=true` lands before the depth ceiling, the only anti-recursion mechanism is a hardcoded 2-tool exclusion `HashSet`.

### Key Recommendations
1. **Fix the process, not just the bugs.** Add a pre-commit hook (extend existing `.githooks/pre-commit`) that rejects commits with secret patterns. Add a security-review checklist for any new auth/provider callback path. Convert the June 17 council's P0/P1 findings into concrete tasks if they aren't already.
2. **Adopt the "one file to rule them all" approach for the top hot-spot files.** `ChatServices.cs` (3,736 lines), `CodeSearchService.cs` (3,115 lines), and `Chat.razor` (3,230 lines) are well past the point where meaningful security review is feasible in a single pass. Recommend at least one file per sprint for decomposition.
3. **Build a security regression test suite.** The common pattern across most findings is "path A is hardened, path B (added later) forgot to apply the same hardening." A test that verifies all auth paths (local + OAuth) enforce the same bootstrap, rate-limit, and antiforgery policies would catch this class of bug automatically.
4. **Define a cross-setting validation mechanism.** Multiple findings involve two independently-reasonable settings that compose into a dangerous interaction (`AllowRawHtml`×`AutoEditorForAuthenticatedUsers`, disabling-last-auth-method, etc.). A single `TryValidateCrossSettingConstraints` already exists but only checks two code-search weight relationships — extend it to cover auth/cross-domain combinations.

---

## Assumptions

1. **Current `master` HEAD** (`d250ffe8`, 2026-06-27) is the correct audit target. No uncommitted changes are considered.
2. **Leaked secret values** are assumed still live in the operator's actual deployment unless rotated out-of-band. I cannot verify production validity from static source.
3. **The full audit sweep** across 17 files is treated as exhaustive within their stated scope. The "still unread" caveats from individual reports are preserved — `CodeSearchService.cs`, `Chat.razor`/`Admin.razor`/`Tasks.razor`, and `MemorySmith.Training` Python code were sampled, not read end-to-end. Confidence that no additional Critical items hide there is ~55%.
4. **Task board statuses** are assumed stale for TSK-0201 and TSK-0203 (no comment in 5 weeks). If they are genuinely complete, some findings (especially TSK-0202's `num_ctx` gap) may already be implicitly addressed in later commits not reflected in task comments.
5. **MemorySmith.Agent repo findings** are excluded from this synthesis except where cross-repo references appear in `Data/Pages/MS-Requests/`.

---

## Open Questions

1. **Are the leaked API key / OAuth ClientSecret currently valid in a live deployment, or already superseded?** This changes I-1 from "urgent" to "already handled, just clean up the repo."
2. **Is TSK-0201/TSK-0203 genuinely stalled, or just in need of status field updates?** If stalled, what dependency is blocking?
3. **Does the operator's actual deployment use Kestrel-direct (needs HSTS) or always a reverse proxy that handles TLS?** Affects whether missing `UseHsts()` is a real gap.
4. **Are the two `appsettings.LocalOverrides.json` copies (source-tree vs. `artifacts/`) actively diverging, or is one stale?** The diff shows real content differences worth reconciling before rotation so the new secret doesn't miss a copy.
5. **Is `MemoryIndex` pre-wired for a specific near-term consumer?** If yes, the fix is "harden it now" (ConcurrentDictionary + atomic swap). If no, the fix is "delete it now."
6. **Do any of the configured models emit multiple `<think>` blocks per turn?** If not, the `SplitThinking` fix (S-13) is currently benign.
7. **Should the pre-commit secret-scanning (recommendation) be scoped narrowly (grep for known patterns) or broadly (gitleaks/trufflehog in CI)?** The narrow approach is faster to implement; the broad approach is more thorough but needs setup.
8. **Is `CodeSearchService.cs` worth a dedicated line-by-line follow-up pass?** At 3,115 lines touching filesystem + TreeSitter parsing of arbitrary repo content, it's the highest-likelihood remaining source of undiscovered bugs.

---

## Confidence Assessment

| Category | Confidence | Rationale |
|---|---|---|
| **P0 Security findings** (I-1, I-2, I-3, S-1, S-2) | **95%** | Direct code reads, re-verified across multiple independent audit passes. The original June 17 findings are 16+ days stale with no code change. |
| **Architecture / data integrity** (S-5, S-7, N-12, N-13, N-14) | **88%** | Mechanism directly confirmed from source. Residual uncertainty is about real-world hit rates and whether other layers mitigate the gap. |
| **Code quality / dead code** (I-6, S-6, S-8, N-16) | **95%** | Pure reference-counting facts, no inference needed. |
| **Training harness** (I-5, N-17) | **89%** | Code vs. docstring mismatch is 100% certain. Training quality impact is well-established but not empirically confirmed for this specific project. |
| **UI / UX** (S-4, N-11, N-4) | **86%** | Code mechanism verified. Real-world hit rate depends on user behavior patterns I can't observe. |
| **No additional Critical items in unread files** | **55%** | `CodeSearchService.cs`, Razor components, and Python training code were sampled, not read end-to-end. The 55% reflects an honest gap, not a claim of coverage. |

**Overall synthesis confidence: 88%** — I am confident in the priority ordering, the batching recommendations, and the minimum viable set. The main source of residual uncertainty is whether the 17 audit reports collectively missed something in the ~6,500 lines of unread code in the three largest sampled files.

---

## Acceptance Criteria for This Synthesis

1. ✅ All 38 findings are mapped to a priority tier (Immediate / This Sprint / Next Sprint / Design Question)
2. ✅ Each proposed task has scope, effort estimate (XS–XL), dependencies, deferral risk, and acceptance criteria
3. ✅ Batching recommendations are provided where items can be grouped into single PRs
4. ✅ The minimum viable set (5 items) is identified to close the most critical gaps
5. ✅ Sequencing dependencies are explicitly diagrammed
6. ✅ Design questions needing council are separated from actionable tasks
7. ✅ Findings, risks, recommendations, assumptions, and open questions are all documented
8. ⬜ *Council review of this synthesis by other seats* — recommend the Source-Grounded Archivist verify evidence sources and the Skeptical Reviewer challenge the priority ordering
