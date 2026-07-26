# MemorySmith Audit — `ChatModelProfileService`: OpenAI Provider Silently Corrupted to Ollama
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-17
**Method:** full read of `MemorySmith.App/Services/ChatModelProfileService.cs` (440 lines, never previously examined). Also ran two targeted custom-Python scans for "AI-generated-code" tells (exact-duplicate comments across files, and comment-restates-next-line patterns) per the standing request to look for that signal — results reported honestly below, including the negative ones, rather than only reporting the file-read finding.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F47 | `ChatModelProfileService.NormalizeProvider` only recognizes `"GitHub"` and falls back to `"Ollama"` for anything else — including the literal string `"OpenAI"`, which is one of exactly three entries in the same class's own `SupportedProviders` list. Creating or editing a chat-model profile with Provider = "OpenAI" is accepted by validation and then **silently persisted with Provider = "Ollama"** | 95% | **High** (data corruption on a user-facing admin feature — the saved profile silently becomes a different provider than what was configured, with no error) | **New** |

**AI-code-smell scan results (requested angle, reported for completeness):** re-ran the custom duplicate-comment and comment-restates-code scripts from the prior report against files not yet covered — no new exact-duplicate comments (3+ occurrences) found, and only 2 new low-severity "comment closely restates the next line" candidates, both benign (a comment describing an antiforgery-attribute check, and one describing a finish-reason read — neither egregious). Continuing to report this honestly: **this codebase does not show a strong, systematic "over-commenting" or "restates-the-obvious" AI-generation tell at scale**, on either pass. That's a real, useful negative result for anyone specifically worried about this codebase being AI-authored slop — the comment discipline looks like normal human (or at minimum, well-edited) authorship throughout everything read across this engagement so far.

---

## F47 — OpenAI provider selection silently saves as Ollama (High, 95%)

**File:** `MemorySmith.App/Services/ChatModelProfileService.cs`, lines 47-48 and 396-402:
```csharp
private static readonly IReadOnlyList<string> SupportedProviders = ["Ollama", "GitHub", "OpenAI"];
...
private static string NormalizeProvider(string provider) =>
    ProviderMatches(provider, "GitHub") ? "GitHub" : "Ollama";

private static bool ProviderMatches(string left, string right) =>
    string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
    (string.Equals(left, "GitHub", StringComparison.OrdinalIgnoreCase) && string.Equals(right, "Copilot", StringComparison.OrdinalIgnoreCase)) ||
    (string.Equals(left, "GitHubCopilot", StringComparison.OrdinalIgnoreCase) && string.Equals(right, "GitHub", StringComparison.OrdinalIgnoreCase));
```
`NormalizeProvider` is a strict binary function — it returns `"GitHub"` if the input matches (via `ProviderMatches`'s equality plus two legacy-alias checks for `"Copilot"`/`"GitHubCopilot"`), and **`"Ollama"` for literally anything else**, including `"OpenAI"`.

**Traced the actual save path, `TryNormalizeRequest` (lines 331-382):**
```csharp
var provider = NormalizeProvider(request.Provider);   // "OpenAI" → "Ollama", silently, right here
...
if (!SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))   // "Ollama" passes — always will
{
    error = "Choose a supported provider.";
    return false;
}
...
profile = new ChatModelProfileOptions { ..., Provider = provider, ... };   // saved as "Ollama"
```
The validation check at line 344 runs *after* normalization has already happened, so it can never catch this — by the time it runs, `provider` is already `"Ollama"` and trivially passes the "is this supported" check. A user (via whatever admin UI binds to `ProviderOptions`, which includes `"OpenAI"` per line 63/47) selects "OpenAI", fills in a model name and settings, saves — and the profile is written to disk, audit-logged, and returned to the caller with `Provider = "Ollama"`, with **no error, no warning, and no visible sign anything went wrong** until the profile is actually used for a chat request and either fails against Ollama (if no local Ollama server matches the OpenAI model name) or, worse, silently attempts to route what the user thinks is an OpenAI-compatible request through the Ollama provider path instead.

**Why this is a High, not just Medium, severity finding:** this isn't a cosmetic label mismatch — `Provider` is very likely used elsewhere to select *which chat provider class actually handles the request* (this engagement's earlier reports examined `OpenAICompatibleChatProvider.cs` as a distinct, newly-added provider implementation, separate from the Ollama path in `ChatServices.cs`). A profile silently mislabeled as `"Ollama"` when the user configured it as `"OpenAI"` would very plausibly route real chat traffic to the wrong provider entirely, using a model name/endpoint combination that doesn't match — a functional break, not just a display issue, and one that fails *silently* rather than with a clear error at save time.

**Root cause, evidenced from the code's own history:** `ProviderMatches`'s legacy-alias handling for `"Copilot"`/`"GitHubCopilot"` shows this function has been patched at least once before as provider naming evolved, and each time, only the two-way GitHub/Ollama distinction was maintained. `SupportedProviders` was updated to include `"OpenAI"` as a third option (presumably alongside the same sprint's `OpenAICompatibleChatProvider.cs` addition, confirmed new-this-sprint in an earlier report) but `NormalizeProvider`'s binary logic was never extended to match — the exact same "fix/feature landed in one place, a parallel piece of logic elsewhere wasn't updated to match" pattern this engagement has now found multiple times (F32's triplicated path-resolution logic, F39's un-propagated `SplitThinking` fix).

**Recommendation:**
```csharp
private static string NormalizeProvider(string provider)
{
    if (ProviderMatches(provider, "GitHub")) return "GitHub";
    if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase)) return "OpenAI";
    return "Ollama";
}
```
or, more robustly against this recurring, given there are now three supported providers and likely more in the future: normalize against `SupportedProviders` directly (`SupportedProviders.FirstOrDefault(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase)) ?? "Ollama"`, with the GitHub-alias handling layered on top only for the two known legacy names) so that adding a fourth provider to `SupportedProviders` in the future doesn't require remembering to also touch this separate function — closing off the exact failure mode that caused this bug. **Add a test** creating a profile with `Provider = "OpenAI"` and asserting the saved/returned profile's `Provider` is `"OpenAI"`, not `"Ollama"` — this is precisely the kind of one-example test that would have caught this the moment `"OpenAI"` was added to `SupportedProviders`, and its absence is presumably why this shipped.
**Effort:** 1 hour for the fix + test. **Priority: should be treated as urgent** relative to most other findings in this engagement — unlike many of the latent/theoretical risks reported elsewhere, this one actively corrupts data the moment a real admin tries to use a real, already-shipped feature (selecting OpenAI as a provider), with no error to alert them.
**Confidence (95%):** the code-level bug is unambiguous and directly traced end-to-end (normalize → validate → persist), not inferred. The 5% held back is only for not having empirically confirmed (can't run the app in this sandbox) that `Provider` is actually consulted downstream to select the provider implementation as I described — I'm confident this is how it's used based on this engagement's prior reads of the provider-selection code, but didn't re-trace that specific downstream consumption in this pass.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- The AI-code-smell scan methodology (exact-duplicate-comment counting, comment-restates-next-line heuristic) is necessarily a coarse proxy — it can only catch the more mechanical/obvious version of "AI-generated feel" and would miss subtler tells (uniform paragraph rhythm, consistent-but-generic variable naming, etc.) that would require a different kind of analysis (e.g., stylometric comparison against a human-authored baseline) not attempted here. Treat the negative result as "no strong mechanical tell found," not "confirmed human-authored."
- F47's downstream-consumption claim (that `Provider` selects the actual chat-provider implementation) is based on this engagement's earlier examination of `OpenAICompatibleChatProvider.cs` and `ChatServices.cs`'s Ollama-handling code, not a fresh trace in this specific pass — worth a quick confirmation read of wherever `ChatModelProfileView.Provider`/`ChatModelProfileOptions.Provider` is consumed at chat-request time before treating the "silently routes to wrong provider" consequence as fully proven, though the core "OpenAI silently becomes Ollama" bug stands regardless of exactly how severe its downstream consequence turns out to be.
