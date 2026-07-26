# MemorySmith Audit — Delta Report 4 (Continued Deep Dive)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` · **Commit:** `e8a3065` (confirmed unchanged from Delta Report 3 — re-checked branch HEAD before starting this pass)
**Report generated:** 2026-07-11
**Relationship to prior reports:** contains only new findings not present in the original audit or Delta Reports 1–3. New items numbered F19+ for stable cross-referencing.

**This pass expanded scope to:** full read of `MemorySmith.App/Services/VarResolver.cs` (403 lines), the write-permission half of `MemorySmith.App/Services/MaintenanceAgentServices.cs` (`MaintenanceWritePermissionService`), a repo-wide TODO/FIXME sweep, and a scripted cross-check of all 284 `MemorySmithOptions` properties against actual usage sites (including `.razor` files, to avoid false positives from UI-only bindings) to find configuration that's declared and documented but never enforced.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F19 | Two independently-implemented "is this path under this allowed root" security checks exist — `VarResolver.IsUnderRoot` (source-link reads) and `MaintenanceWritePermissionService.IsUnderPath` (maintenance-agent read/write) — nearly identical logic, never shared | 90% | Medium (security-boundary duplication — a future hardening fix to one is easily missed in the other) | **New** — no existing task covers consolidating these two |
| F20 | `VarResolver.ReadSourceAsync`'s default read window (when no line range is requested and unrestricted reads are off) is a hardcoded literal `49` (50 lines), the only non-configurable read-limiting parameter in a class where every sibling limit (`MaxReadBytes`, `ReadContextLinesBefore/After`, `AllowUnrestrictedSourceReads`) is admin-configurable | 90% | Low (inconsistent design, arbitrary magic number) | **New** |
| F21 | `MaxNestingDepth` (agent session config, default `1`) is a live, admin-editable setting with zero enforcement anywhere in the codebase — confirmed by three separate `// TODO (Phase 3)` markers admitting it isn't wired up yet | 90% | Medium (silently non-functional security-adjacent setting) | **Extension to TSK-0276** ("Phase 3: internal agent delegation," Backlog/Medium) — recommends an interim mitigation, not a new task |
| F22 | The same "declared, documented, never enforced" pattern recurs at least twice more: `MaxParallelOpenAIRequests` (doc comment explicitly claims it limits burst concurrency; zero semaphore/throttle exists anywhere in `OpenAICompatibleChatProvider.cs` or elsewhere) and `MemorySmithOptions.SettingsOverridePath` (bound as a typed option but every real consumer reads the raw `IConfiguration["MemorySmith:SettingsOverridePath"]` string instead, bypassing the bound property entirely) | 85% | Medium (recurring category, not isolated incidents) | **New** — recommend a dedicated repo-wide sweep task rather than three one-off fixes |

---

## F19 — Duplicate path-containment security checks (Medium, 90%)

**Files:**
- `MemorySmith.App/Services/VarResolver.cs`, `IsUnderRoot` (lines 394-399):
  ```csharp
  private static bool IsUnderRoot(string fullPath, string root)
  {
      var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
  }
  ```
- `MemorySmith.App/Services/MaintenanceAgentServices.cs`, `MaintenanceWritePermissionService.IsUnderPath` (lines 508-513):
  ```csharp
  private static bool IsUnderPath(string path, string root)
  {
      var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
          string.Equals(path.TrimEnd(...), normalizedRoot.TrimEnd(...), StringComparison.OrdinalIgnoreCase);
  }
  ```

Same algorithm, same edge-case handling (trailing separator normalization), same comparison mode, written twice by hand in two different security-critical files (one gates source-link file reads, the other gates maintenance-agent file writes and reads). Verified both are correct in isolation and functionally equivalent for the cases I traced — this isn't a "one is buggy" finding, it's a duplication finding: any future fix (e.g., adding symlink-resolution hardening, handling UNC paths, or hardening against the classic trailing-separator/prefix-match trick where `/allowed-root-evil` would wrongly match a root of `/allowed-root` without the separator normalization these two functions already apply) has to be applied twice, by two different people, in two different PRs, and there's nothing forcing that to happen — the two files don't reference each other or a shared base.

**Also worth noting for the fix:** `MaintenanceWritePermissionService.IsUnderPath` additionally checks exact-equality after trimming, which `VarResolver.IsUnderRoot` doesn't have as an explicit branch — verified this doesn't matter functionally (both append a separator before comparing, so an exact match still satisfies `StartsWith` in `VarResolver`'s version), but it does mean the two implementations aren't even literally identical, just equivalent — reinforcing that this is organic duplication rather than a deliberate shared pattern.

**Recommendation:** extract a single `PathSecurity.IsUnderRoot(string candidatePath, string root)` static helper (e.g. in `MemorySmith.Core/Security/` alongside `SecurityCompare.FixedTimeEquals`, which this project already treats as the right home for shared security primitives) and have both call sites use it. Low effort, meaningfully reduces the surface area for a future path-boundary regression.

---

## F20 — Hardcoded, non-configurable default read window in `VarResolver` (Low, 90%)

**File:** `MemorySmith.App/Services/VarResolver.cs`, lines 93-102:
```csharp
else if (_options.SourceLinks.AllowUnrestrictedSourceReads)
{
    startIdx = 0;
    endIdx = null;
}
else
{
    startIdx = 0;
    endIdx = 49;          // ← magic number: first 50 lines, hardcoded
}
```

Every other read-limiting knob in this same method is admin-configurable: `MaxReadBytes` (clamped via `ClampReadBytes`, backed by `_options.SourceLinks.MaxReadBytes`), `ReadContextLinesBefore`/`ReadContextLinesAfter`, and the `AllowUnrestrictedSourceReads` escape hatch itself. The one case this doesn't cover — "no explicit line range was requested, and unrestricted reads are off" — falls back to a bare literal `49` with no accompanying option, no named constant, and no comment explaining why 50 lines specifically. An admin who wants a 20-line default preview, or a 200-line one, has no way to configure it short of editing this source file, which is inconsistent with how every neighboring behavior in this exact method is designed to be tunable.

**Recommendation:** promote this to `_options.SourceLinks.DefaultReadLineCount` (or similar), defaulting to `50` to preserve current behavior, and reference it here instead of the bare literal. Small, low-risk, and removes the only unexplained magic number in an otherwise carefully-configurable class.

---

## F21 — `MaxNestingDepth` is a live setting with zero enforcement (Medium, 90%)

**File:** `MemorySmith.App/Services/MemorySmithOptions.cs`, line 499: `public int MaxNestingDepth { get; set; } = 1;` — a normal, bindable, admin-editable integer option (no `[Obsolete]`, no "not yet implemented" marker visible from the settings surface).

**Confirmed via repo-wide grep**, the only other references to this property are three doc/TODO comments admitting it isn't enforced yet:
- `AgentSessionService.cs:236` — `// TODO (Phase 3): enforce MaxNestingDepth ceiling here when internal delegation is enabled.`
- `AgentSession.cs:65` — `/// TODO (Phase 3): AgentSessionService.CreateSessionAsync must enforce MaxNestingDepth ceiling here.`
- `MemorySmithOptions.cs:497` — `/// TODO (Phase 3): AgentSessionService.CreateSessionAsync enforces this ceiling.`

This is already correctly captured by **TSK-0276** ("Complete Phase 3 of the memorysmith_agent_invoke feature... see docs/PHASE3.md for full acceptance criteria and TODO entry points," status Backlog/Medium) — not a new task. The extension I'd suggest: TSK-0276 is Backlog with no committed date, and in the meantime this setting is live and editable today with a name and default value that imply active protection against runaway agent-delegation recursion. Anyone reading `appsettings.json` or (if it's admin-UI-editable — not confirmed either way in this pass) the Settings page has no signal that changing this number currently does nothing.

**Recommendation (interim, cheap, doesn't block TSK-0276 itself):** add a one-line startup diagnostic/log warning (this project already has `OperationalDiagnosticsService.cs` and `LoggingObservabilityService.cs` as natural homes for this) when `MaxNestingDepth != 1` is configured, noting the ceiling isn't enforced pre-Phase-3. Cheaper than blocking on the full feature, and prevents a false sense of security in the interim.

---

## F22 — The "declared, documented, never enforced" pattern recurs beyond `MaxNestingDepth` (Medium, 85%)

Scripted a cross-check of all 284 properties on `MemorySmithOptions` and its nested option classes against usage sites repo-wide (including `.razor` files, to rule out UI-only bindings — `Branding.ShortLabel/LogoUrl/FaviconUrl` initially looked unused but are consumed in `MainLayout.razor`, confirmed as false positives and excluded). Two more genuine cases surfaced beyond `MaxNestingDepth` (F21):

1. **`MaxParallelOpenAIRequests`** (`MemorySmithOptions.cs:407`, default `1`), doc comment: *"Maximum number of parallel OpenAI-compatible requests. Default is 1 (serial) to stay within burst rate limits common on API tiers."* Grepped `OpenAICompatibleChatProvider.cs` (713 lines, the only plausible consumer) for `SemaphoreSlim`, `Parallel`, `MaxConcurren*`, `throttl*` — **zero matches**. No concurrency gate exists anywhere in the codebase referencing this property (in fact, no code anywhere reads the property at all). This is a second live setting whose name and doc comment promise a specific protective behavior it doesn't deliver, distinct from `MaxNestingDepth`'s "planned but staged" framing — this one reads as a setting that either regressed (throttling removed, setting left behind) or was always aspirational. No task currently tracks it.

2. **`SettingsOverridePath`** (`MemorySmithOptions.cs:20`, `public string? SettingsOverridePath { get; set; }`) — this one is *not* dead functionality (the path it names is very much alive and load-bearing — it's the mechanism behind TSK-0181's malformed-override-fallback concern and TSK-0288's secret-leak incident, both already tracked), but the **bound options property itself is never read**. Every real consumer (`MemorySmithConfigurationSetup.cs:35`, `ChatModelProfileService.cs:60`, `MemorySmithLocalDevelopmentPostConfigure.cs:15`, `AdminSettingsService.cs:28`) independently calls `MemorySmithConfigurationPaths.ResolveSettingsOverridePath(configuration["MemorySmith:SettingsOverridePath"])` against the raw `IConfiguration` indexer, not `_options.CurrentValue.SettingsOverridePath`. The bound property is a parallel, decorative path to the same config key — harmless today only because nothing currently diverges, but it means editing the doc comment or default on the options property creates zero actual effect, and a future engineer trusting the strongly-typed options object for this value (a very natural thing to do, since every other path-like setting in this same class — `EventLogPath`, `VarsPath`, `DataProtectionKeysPath` — *is* read through the bound property elsewhere) would silently get nothing.

**Why this is a "delta, not three separate bugs":** three instances of the identical failure mode — a setting exists, is documented, and does nothing or does something different from what's documented — is enough to call it a pattern rather than coincidence in a 284-property options surface. Given this project's own stated philosophy of eliminating drift and technical debt, this warrants a **dedicated sweep task**: script (the approach used for this pass is a reasonable starting point — a repo-wide "for each bound option property, find at least one real non-doc, non-test consumer" check) across the full `MemorySmithOptions` tree, rather than fixing these three in isolation and leaving the next one for a future audit to re-discover. I did not exhaustively hand-verify all 284 properties' consumers beyond the automated grep pass (which itself required manual correction for the `.razor` false positives) — recommend that sweep be someone's actual task deliverable rather than trusting my script's output as final. Confidence is 85%, not higher, specifically because of that residual manual-verification gap.

---

## Assumptions

- Re-verified branch HEAD (`e8a3065`) unchanged before starting this pass.
- F22's consumer-detection script is a heuristic (regex property-name grep across `.cs`/`.razor`/`.json`), not a compiler-verified reachability analysis — it can miss reflection-based or `IConfiguration`-string-indexed access patterns (as it initially did for `SettingsOverridePath` itself, which I had to manually re-investigate after the script's naive property-access grep missed it). Treat the two new F22 findings as strong leads confirmed by manual follow-up, not as an exhaustive list of every dead setting in the 284-property surface.
- Did not confirm whether `MaxParallelOpenAIRequests` or `MaxNestingDepth` are actually exposed as editable rows in the `/admin` Configuration tab (`AdminSettingsService.ListEditableSettings` uses an allowlist-descriptor model per the wiki memory, and neither name appeared in `AdminSettingsService.cs` directly) — this doesn't change the finding (both are editable via `appsettings.json`/override file regardless of UI exposure), but I'm not asserting UI-level visibility one way or the other.
