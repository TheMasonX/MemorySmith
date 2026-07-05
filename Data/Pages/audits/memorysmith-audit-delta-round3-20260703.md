# MemorySmith Audit — Delta Report (Round 3)

**Same repo/commit:** `TheMasonX/MemorySmith` @ `d250ffe8` (master, 2026-06-27).
**Delta-only**, continuing into the files Round 2 flagged as still unread: `MemoryGovernanceServices.cs`, `PageService.cs`, `AdminSettingsService.cs`, and cross-referencing back into `ChatToolCatalog.cs`/`ChatServices.cs` for consumers of what was found there.

**Headline of this round:** D7 below isn't just a new finding — it's the mechanism behind Round 1's Finding F1. It explains *why* the leaked secrets exist in two actively-diverging copies and *why* rotating them alone won't fix the underlying leak. Read that one first.

---

## New Finding D7 (escalation of Round 1 / F1) — The settings UI writes live secrets to a path the code itself treats as a valid, non-gitignored config location

**Where:** `MemorySmith.App/Services/MemorySmithConfigurationPaths.cs` (`ResolveSettingsOverridePath`), consumed by `AdminSettingsService.cs:28` and `MemorySmithConfigurationSetup.cs:26`.

**Evidence:**
```csharp
public static string ResolveSettingsOverridePath(string? configuredPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath)) return Path.GetFullPath(configuredPath);

    var defaultPath = Path.Combine(AppContext.BaseDirectory, DefaultSettingsOverrideFileName);
    var discoveryCandidates = new[]
    {
        defaultPath,
        DiscoverFromAncestors(Path.Combine("MemorySmith.App", DefaultSettingsOverrideFileName)),
        DiscoverFromAncestors(Path.Combine("artifacts", "MemorySmith.App", DefaultSettingsOverrideFileName))
    };
    foreach (var candidate in discoveryCandidates)
        if (File.Exists(candidate)) return candidate;
    return defaultPath;
}
```
- `artifacts/MemorySmith.App/appsettings.LocalOverrides.json` is not a stray build artifact that got committed by accident — it's a **first-class, code-recognized discovery candidate**. The application is designed to find and use this exact path.
- `AdminSettingsService.UpdateAsync` (the backend for the in-app Settings page) writes every settings change straight back to whichever path this function resolves at startup (`SetJsonValue(root, ...)` then persists to `_settingsPath`).
- Critically, `defaultPath = Path.Combine(AppContext.BaseDirectory, ...)`. For a `dotnet publish`-style deployment, `AppContext.BaseDirectory` **is** the `artifacts/MemorySmith.App/` output folder — meaning in that deployment shape, `defaultPath` and the `artifacts/...` discovery candidate are the same physical location, and it's checked **first** in the candidate list.
- `.gitignore` (confirmed in Round 1) only covers `MemorySmith.App/appsettings.LocalOverrides.json` — the source-tree path. It has no rule for `artifacts/`.

**What this actually explains:** Round 1 found two divergent copies of this file, with the `artifacts/` copy being the newer/more-complete one (more config sections, matching the currently-live secret values). This isn't two accidents — it's one designed behavior: whenever this app runs from a published build and an admin touches the Settings page, it writes the **current, live** API key and OAuth client secret to a path with zero `.gitignore` coverage, in a **public** repository. Rotating the secret (Round 1's #1 recommendation) does not fix this — the next settings change from the admin UI will happily write the *new* secret right back into the same ungitignored location, and a routine `git add -A` / `git status` glance would show it as an untracked-but-present file, not obviously as "the secrets file."

**Recommendation, in addition to Round 1's rotation/history-purge steps:**
1. Add `artifacts/` (or at minimum `artifacts/**/appsettings.LocalOverrides.json`) to `.gitignore` immediately — this is the actual missing rule, more urgent than the history purge itself, since it's the one still actively regenerating.
2. Reconsider `AppContext.BaseDirectory` (the publish output folder) as a place this file is ever written to at all — a runtime settings-override file living inside the publish/build output directory is itself an unusual choice; a path under a dedicated, always-gitignored `data/` or `config/` root (outside the build output tree entirely) would remove this entire class of "build artifact directory accidentally becomes a secrets store" risk structurally, rather than relying on a `.gitignore` rule to keep catching it.
3. Add a startup-time check that logs a loud warning (not just silently proceeding) if the resolved settings-override path lives inside a directory that looks like a build/publish output or a git-tracked source tree — cheap, and would have caught this on day one.

**Confidence:** 90% on the mechanism (traced the exact function, its call sites, and its interaction with `AppContext.BaseDirectory` in a publish layout); 65% on the specific claim that this exact function is *why* the artifacts copy is newer/more-complete — I inferred that from the timeline and content-completeness observed in Round 1 rather than directly observing a deployment in progress, so treat the causal claim as a well-supported hypothesis rather than a confirmed reproduction.

---

## New Finding D5 — Tag-policy loading has two independent fallback layers; the outer one is a silent, unlogged, up-to-8-ancestor-directory filesystem search

**Where:** `MemorySmith.App/Services/MemoryGovernanceServices.cs` — `TagPolicyService.LoadPolicy` (well-behaved) vs. `TagPolicy.CreateDefault()` → `TryLoadFileBackedDefault()` (the problem).

**Evidence:**
- `TagPolicyService.LoadPolicy(path)` is the primary, well-designed path: on missing file or parse failure, it returns a `TagPolicyLoadStatus` with a `Reason`/`Message`/`ErrorType`, which `MemoryDiagnosticsService.AnalyzePolicy` surfaces as an actual diagnostic (`tag.policy_missing` / `tag.policy_load_failed`). Good, deliberate design — confirmed by reading both sides of the contract.
- But the fallback value it returns in both failure cases is `TagPolicy.CreateDefault()`, which is **not** a static/hardcoded object. It calls `TryLoadFileBackedDefault()`, which:
  - Optionally honors a *second*, entirely separate environment variable (`MEMORYSMITH_DEFAULT_TAG_POLICY_PATH`) that nothing in the admin-facing status reporting knows about.
  - Walks up to 8 parent directories from `AppContext.BaseDirectory` looking for `Data/Policies/tag-policy.json` at each level.
  - Wraps the read+deserialize in a bare `catch { }` (empty, no logging, no status object) — if a file is found at some ancestor level but fails to parse, this is swallowed completely silently and the loop just tries the next candidate.
- The `TagPolicyLoadStatus` that `AnalyzePolicy` surfaces to admins only describes the *primary configured path* (`_options.Governance.TagPolicyPath`). It has no way to report "actually, a different tag-policy.json was found 4 directories up and silently loaded instead" versus "the truly hardcoded 9-namespace default is in effect" — both cases produce identical diagnostic output.

**Impact:** If `Governance:TagPolicyPath` is ever misconfigured, missing, or corrupted, the admin-facing diagnostic message says "using built-in defaults" — but what's actually running could be any `tag-policy.json` sitting up to 8 directories above the app's base directory (plausible in this specific repo, since the wiki is deliberately shared with the sibling `MemorySmith.Agent` checkout — see Round 1's cross-repo note — which increases the odds of an unrelated but similarly-named file existing at a nearby path), and there would be zero indication that this happened versus the true hardcoded fallback.

**Recommendation:** Have `TryLoadFileBackedDefault()` return its own status (found-and-loaded vs. found-but-failed-to-parse vs. not-found-anywhere-hardcoded-used) and plumb that into `TagPolicyLoadStatus` so `AnalyzePolicy`'s diagnostic is accurate about which of the three outcomes actually happened. At minimum, replace the empty `catch { }` with a debug/warning log including the path that failed to parse.

**Confidence:** 88%. Read both methods directly and confirmed the status-reporting gap by tracing `AnalyzePolicy`'s message construction; did not attempt to actually trigger this path against a real ancestor-directory layout to observe it firing.

---

## New Finding D6 — `PageAccessLevels.ResolveStoredMinimumRole` fails open to `Anonymous` on malformed input; both current callers happen to pre-validate around it, but the function itself doesn't enforce that

**Where:** `MemorySmith.App/Services/PageService.cs:140-153`, consumed by `PagesController.cs:147` and `ChatToolCatalog.cs:1002` (the `memorysmith_page_save` MCP/chat tool).

**Evidence:**
```csharp
public static string ResolveStoredMinimumRole(string? requestedMinimumRole, string? existingMinimumRole, string configuredDefaultMinimumRole)
{
    if (!string.IsNullOrWhiteSpace(requestedMinimumRole))
        return Normalize(requestedMinimumRole);   // <-- single-arg call
    ...
}
```
`Normalize(string? value, string fallback = Anonymous)` only falls back to the *given* fallback if `value` is null/empty; if `value` is non-empty but doesn't match any recognized role string, `Normalize` returns `Anonymous` directly (its own default parameter), **not** `existingMinimumRole` and **not** `configuredDefaultMinimumRole`. So a non-empty-but-unparseable `requestedMinimumRole` passed into `ResolveStoredMinimumRole` silently resolves to `Anonymous` — a privilege downgrade — regardless of what the page's current restriction is.

**Why this isn't an active bug today (verified, not assumed):**
- `PagesController.TryResolveMinimumRole` (line 141) calls `PageAccessLevels.TryNormalize(request.MinimumRole, ...)` **first** and returns `400 BadRequest` before ever reaching `ResolveStoredMinimumRole` — so a malformed value never reaches the vulnerable function from the HTTP API.
- `ChatToolCatalog.cs`'s `memorysmith_page_save` tool handler (line 986) does the identical pre-check — `TryNormalize` first, tool-error response if it fails — before calling `ResolveStoredMinimumRole`.
- I checked both call sites line-by-line; both are currently safe.

**Why it's still worth fixing:** the safety is duplicated, independently, in two call sites — it is not enforced by the function that actually has the unsafe fallback behavior. Anyone adding a third caller (a new admin API, a bulk-import script, a future MCP tool for page templating, etc.) who reasonably assumes "a function named `ResolveStoredMinimumRole` that takes an existing role and a configured default will fail safe toward one of those, not toward the most permissive option" would introduce this exact privilege-downgrade bug without realizing it — especially risky given one of the two current callers is an LLM-driven tool where "the model formatted an argument slightly wrong" is a realistic, not hypothetical, failure mode.

**Recommendation:** Move the `TryNormalize`-and-reject-on-failure check *inside* `ResolveStoredMinimumRole` itself (or have it throw/return a result type on unparseable input) so the safety isn't something every future caller has to remember to replicate. This is a "make the interface hard to misuse" fix, not a "there's a live vulnerability" fix.

**Confidence:** 92% on the mechanism and on both current callers being safe (read both in full); 70% on the practical risk of a future third caller, which is necessarily speculative.

---

## Investigated and ruled out this round (no action needed, noting so the ground isn't re-covered)

- **`PageAccessLevels.Normalize`'s two-argument fallback chain** (used by `FilePageService.SaveAsync` and `ReadMinimumRole`, as opposed to the single-argument path in D6): traced fully — `_defaultMinimumRole` is itself always pre-normalized at construction, so this path cannot actually fail open to a surprising value. Only the single-argument call inside `ResolveStoredMinimumRole` (D6) has the sharp edge.
- **`ChatToolDescriptor.AvailableInAgent`**: initially looked like a dead/unused flag (the sub-agent session tool filter in `AgentSessionService.InvokeCoreAsync` doesn't reference it). Traced further and found it *is* consumed, in `ChatServices.cs:2533` — but for a **different** concept: `MemoryChatMode.Agent`, a mode within the regular interactive chat UI, not the autonomous `AgentSessionService` sub-agent sessions. Both subsystems use the word "Agent" for genuinely different things, which is a naming-clarity nitpick worth a code comment, but not a functional bug — retracting my initial suspicion here explicitly rather than reporting it as a finding.
- **`PageSlugPolicy.TryNormalize`**: re-checked for path-traversal bypass tricks (backslash normalization order, encoded traversal sequences via `Uri.UnescapeDataString` before segment validation). Confirmed safe — unescaping happens before segment splitting and validation, so an encoded `..` can't slip through.
- **`memorysmith_code_search_merge_shard` tool**: re-confirmed (this round, independently) the extension allowlist + absolute-path + root-containment checks are all present and correctly ordered; still not exploitable, still opt-in-only (`EnabledByDefaultInMcp: false`).

---

## Still-Unread Areas (updated)

After this round: `ChatToolCatalog.cs` has now been read closely around ~15 of its 22 tool handlers (the highest-risk ones: file/shard access, page save, memory/task write tools); the remaining ~7 (mostly read-only search/status tools) are lower priority. Genuinely untouched at any depth: `Chat.razor`/`Admin.razor`/`Tasks.razor` component code-behind (beyond targeted greps in Round 2), `MemorySmith.Training` Python code, `Scripts/*.ps1` beyond the two scripts checked in Round 2, and `MemorySmith.Benchmarks`. If continuing, I'd prioritize the Python training harness next, since Round 1 flagged it as a dependency of two in-progress tasks (TSK-0201/0203) and it's the one major language/runtime boundary that's had zero review so far.
