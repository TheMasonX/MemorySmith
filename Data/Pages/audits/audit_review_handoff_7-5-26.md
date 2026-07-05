# External Audit Review Handoff — 2026-07-05

**Source:** External auditors ran ~35 independent audit passes across both `MemorySmith` and `MemorySmith.Agent` repos. The auditors did not reliably distinguish between the two, so files have been sorted manually (see below). This document is a breadcrumb for Agent Smith (or a human) to critically review these findings, prioritize them, and create actionable tasks.

**Important caveat:** Many of these audit files share overlapping content. Some are rounds/deltas of the same audit session repeated to build evidence depth. Treat them as source material to be **synthesized**, not 35 independent verification passes.

---

## Files Moved Here

All files below are now in `Data/Pages/audits/`. They were moved from `C:\Users\Luke\Downloads\Audits\`.

### Primary Reports

| File | Length | Focus |
|---|---|---|
| `memory_smith_audit_report_gpt1.md` | ~10k+ | First-pass audit. Silent data loss in storage layer, `FileMemoryStore` ID mutation, UI capability labeling drift, no schema migration, admin settings concurrency, request guard test harness fragility. |
| `MemorySmith-Audit-20260702.md` | ~15k+ | Full re-verification audit. **Executive summary is the best starting point.** 10 headline findings including: leaked secrets still live (P0), OAuth bootstrap ungated (P0), CSRF protection absent (High), `MemoryIndex` race (High), `FixedTimeEquals` ×3 copies. Also verified 5 in-progress tasks against source. |
| `MemorySmith-Audit-Delta-2-20260702.md` | ~10k+ | `ChatServices.cs` dead code (10 orphaned methods, ~230 lines). TreeSitter C# key mismatch (`"CSharp"` vs `"c_sharp"` — unreachable). `SplitThinking` only strips first `<think>` block. Provider consolidation opportunities. ChatServices dead-code cluster is the highest-value finding. |
| `MemorySmith-Audit-Delta-3-20260702.md` | ~10k+ | `SecurityServices.cs` full read. **Global login rate limiter (not per-client)** — 5 attempts lock out entire app. **Progressive lockout is a phantom feature** — settings exposed in UI, zero code enforces them. `AutoEditorForAuthenticatedUsers` grants more than described. OAuth bootstrap fix pattern *already exists in codebase* — just not applied to OAuth path. |
| `MemorySmith-Audit-Delta-4-20260702.md` | ~8k+ | `MaintenanceAgentServices.cs` full read. `DirectWrite` is phantom (no code branches on it). `RiskLevel` unvalidated free-from LLM output. `MaintenanceProposalStatuses.All` unused. `ExtractJsonObjectPayload` brace extraction naive but fails safe. |
| `MemorySmith-Audit-Delta-5-20260702.md` | ~8k+ | `MemoryApplicationService.cs` full read. **Validation errors silently clobbered under wrong key.** `MemoryIndex` has **zero production consumers** (race is real but unobservable). Case-sensitivity mismatch in `MemoryIndex` (inert today). `SearchAliases` asymmetric. |
| `MemorySmith-Audit-Delta-6-20260702.md` | ~6k+ | XSS surface review. **`AllowRawHtml` + `AutoEditorForAuthenticatedUsers` compose into stored-XSS risk** (both independently reasonable, dangerous together, no warning). UI rendering layer is surprisingly well-defended. |
| `MemorySmith-Audit-Delta-7-20260702.md` | ~5k+ | `Admin.razor` code-behind. **No guardrail against total auth self-lockout** — disabling all sign-in methods permanently locks everyone out. Settings export doesn't leak secrets (ruled out). |
| `MemorySmith-Audit-Delta-8-20260702.md` | ~5k+ | `Chat.razor` non-rendering logic. **Deleting actively-streaming session silently orphans the response** (transcript data loss, not wiki content data loss). Attachment safety checked and ruled out. |
| `MemorySmith-Audit-Delta-9-20260702.md` | ~5k+ | `TaskDomainService.cs` full read. **Unvalidated Status/Priority/Type can make tasks vanish from Kanban columns** (task still exists, but invisible in per-column queries). TOCTOU race in attachment file naming. |
| `MemorySmith-Audit-Delta-10-20260702.md` | ~5k+ | Python training harness. **`warmupSteps` default contradicts its own documented fix** (code says 0, docstring says 10). Boolean-coercion duplicated 3-5 times. LoRA `target_modules` hardcoded to Llama-family. |
| `memorysmith-audit-delta-20260703.md` (Delta Round 2) | ~10k+ | HTTPS cert password via argv. `OllamaGpuSlotScheduler` uses `IOptionsMonitor` but only reads once. Code-search warm reuse trusts mtime, not content hash. Maintenance-agent prohibited path doesn't block `.cs`. **Settings UI writes live secrets to code-recognized config path** (escalation of F1). Tag-policy silent ancestor search. `PageAccessLevels.ResolveStoredMinimumRole` fails open. |
| `memorysmith-audit-delta-round3-20260703.md` (Delta Round 3) | ~8k+ | **Settings UI writes live secrets to a code-recognized path (artifacts/)** — explains *why* secrets re-appear post-rotation. Tag-policy has 8-directory-ancestor filesystem search. Agent tool filtering vulnerability. |
| `memorysmith-deep-audit-20260703.md` | ~15k+ | Deep-dive covering all prior audit findings against current HEAD. OAuth admin bootstrap still open. Secrets in 3 locations. CSRF: `UseAntiforgery` added but controllers still unprotected. **No schema migration framework** (TSK-0201/0202 blocker). TSK-0042 status confirmed. TSK-0202 sends `num_ctx` from global setting, not per-model profile. MemoryIndex dead code confirmed. Silent catches in ChatServices (cross-repo request partially stale). Search guide docs still reference removed tools. |
| `memorysmith_deep_audit_2026-07-02.md` | ~6k+ | Silent data loss on corrupt files, `FileMemoryStore.Save` mutates caller's record, search UI labeling drifting, corpus loading is scaling cliff, admin settings concurrency implicit. |
| `memorysmith_audit_deltas_2026-07-05_v11.md` | ~3k+ | Newest v11 deltas: Agent tool filtering vulnerability (D-001), code search cache/provider coupling (D-002), task lifecycle invariants (D-003), task creation validation (D-004). |
| `memorysmith_delta_audit_v2_gpt1.md` | ~3k+ | Configuration changes not actually live (storage paths are singleton-bound), config override failures silently disappear. |

### Duplicates

| File | Note |
|---|---|
| `MemorySmith-Audit-Delta-10-20260702-duplicate.md` | Byte-identical duplicate of `MemorySmith-Audit-Delta-10-20260702.md`. Kept for completeness. |

---

## Key Themes Across MS Findings

### Security (Critical)
1. **Leaked secrets still live** — API key and GitHub OAuth ClientSecret in 3 locations, unrotated 16+ days after P0 directive. Root cause: settings UI writes live secrets to `artifacts/` path with no `.gitignore` coverage.
2. **OAuth first-user-is-Admin bootstrap ungated** — fix pattern already exists in `SecurityServices.CreateFirstAdminAsync` but not applied to `GitHubOAuthCallbackHandler`.
3. **Zero CSRF protection** on API controllers — `UseAntiforgery()` added but no `[ValidateAntiForgeryToken]` anywhere.
4. **Global login rate limiter** — 5 attempts lock out entire application, not per-client. Progressive lockout is phantom (settings exist, code doesn't enforce).
5. **`AllowRawHtml` + `AutoEditorForAuthenticatedUsers`** compose into stored-XSS-to-everyone risk.

### Architecture & Data Integrity
6. **`ChatServices.cs`** — 10 dead private methods (~230 lines), mid-decomposition (TSK-0042), dead methods are actively misleading.
7. **`MemoryIndex`** — race condition is real but has zero production consumers. Currently dead weight carrying live risk.
8. **No schema migration framework** — single hardcoded migration in `SqliteMemorySmithDatabase.cs`. TSK-0201/0202 need tables/columns.
9. **TreeSitter C# chunking key mismatch** — `"CSharp"` vs `"c_sharp"` means TreeSitter fallback silently uses generic chunking.
10. **Login rate limiter unpartitioned** — global bucket, 5 attempts total locks out everyone.
11. **No guardrail against total auth lockout** — disabling all sign-in methods permanently locks everyone out.
12. **Settings UI writes to ungitignored path** — `artifacts/MemorySmith.App/appsettings.LocalOverrides.json` is a code-recognized discovery candidate with no `.gitignore` coverage.

### Less Urgent
13. **BOM inconsistency** — 56/284 task files with UTF-8 BOM (backlog TSK-0281).
14. **`SearchAliases` asymmetry** — hard-to-extend keyword expansion table.
15. **Validation error clobbering** in `MemoryApplicationService.ValidateRecord`.
16. **TOCTOU race in attachment naming** — `File.Exists` check then `File.Create` with gap.
17. **Unvalidated task Status/Priority** — typo'd status makes task vanish from Kanban columns.
18. **Stale README docs** — `memorysmith_unified_search` documented as live, removed in TSK-0271.
19. **`DirectWrite` phantom setting** — described as auto-apply, never branches on it.

### Already Tracked (verified status)
| Task | Status | Notes |
|---|---|---|
| TSK-0042 — Decompose ChatServices | Step 1 (tool loop) landed. Steps 2+ not started. 10 dead methods found. | 
| TSK-0271 — Remove semantic/unified_search | Code complete. README + 2 wiki guides still reference removed tools. |
| TSK-0281 — CI JSON wellformedness lint | Backlog. BOM evidence is concrete. |
| TSK-0283 — Provider-name leak | Still open, 8 call sites. |
| TSK-0157 — Ceremonial store interfaces | Not re-verified this pass. |

---

## Suggested Action Items for Agent Smith

### Immediate (fix in <1hr each)
1. Rotate API key + GitHub OAuth ClientSecret
2. Add `artifacts/` to `.gitignore`
3. Switch login rate limiter to per-client partitioning (one-line code change)
4. Fix TreeSitter key: `"c_sharp"` → `"CSharp"`

### This Sprint
5. Gate OAuth first-admin with bootstrap token / loopback check (reuse existing `SecurityServices` pattern)
6. Add global `[AutoValidateAntiforgeryToken]` MVC filter
7. Fix `warmupSteps` default in training harness (10, not 0)
8. Delete 10 dead methods from `ChatServices.cs`
9. Scrub README and search guides of removed tool references
10. Add schema migration framework before TSK-0201/0202 land new fields

### Next Sprint
11. Fix `MemoryIndex` concurrency (or delete until needed)
12. Fix login rate limiter and implement/enable progressive lockout
13. Cross-reference `AllowRawHtml` + `AutoEditorForAuthenticatedUsers` descriptions
14. Delete stale planning docs that still reference Worker/Dashboard split-host assumptions

### Design Questions to Resolve
- Should `AllowRawHtml` be scoped to Admin-only page authorship?
- Should `DirectWrite` be implemented (auto-apply bypass) or removed from settings UI?
- Should `MemoryIndex` be wired to search or deleted?

---

## Cross-Reference: Prior Audit Documents

This repo already had extensive audit history before this batch. The most important prior documents:
- `external-deep-research-audit-20260617.md` + its council review — this is the audit this batch re-verified against.
- `hyperagent-audit-9-architecture-deepening-20260611.md` — architecture audit many of these deltas build on.
- `Data/Pages/council/external-audit-council-review-20260617.md` — the council review that set P0/P1 on secrets and OAuth bootstrap.

**When synthesizing, cross-reference against these existing docs to avoid duplicating already-tracked work.**
