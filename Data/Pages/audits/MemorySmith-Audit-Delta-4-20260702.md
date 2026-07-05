# MemorySmith Code Audit — Delta Report #4 (2026-07-02, continued)

**Scope of this document:** deltas only, on top of reports #1–#3. This pass did a full line-by-line read of `MemorySmith.App/Services/MaintenanceAgentServices.cs` (2,187 lines) — the maintenance-agent proposal/review/apply pipeline. Everything below is new. Note up front: this file is generally well-built (proper path allow-listing with correct trailing-separator handling, optimistic-concurrency check before applying a proposal's file changes, real secret-redaction on transcripts, graceful LLM-output-parsing fallbacks) — several things I checked here turned out fine and are noted as ruled-out rather than padded into findings.

---

## Headline deltas

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **`DirectWrite` is a phantom setting, but in the safe direction.** The Admin UI describes it as *"Allows the maintenance agent to write directly inside its configured write roots"* — but the only two places `config.DirectWrite` is referenced are (a) forwarding its value into the LLM's prompt payload as context, and (b) recording it in run metadata. No code branches on it to skip or alter the propose→approve→apply pipeline. The agent **always** goes through human-reviewed proposals regardless of this flag's value. | 🟡 New | **90%** |
| 2 | **`RiskLevel` on maintenance proposals is an unvalidated free-form string that can come straight from LLM output**, unlike `Status` (which is only ever set by fixed internal code paths and is therefore safe despite lacking the same check). A garbage/hallucinated risk level degrades the one field a human reviewer would use to triage urgency. Confirmed nothing branches on `RiskLevel` for an actual permission/approval decision, so this is a UI/audit-trail quality issue, not a bypass. | 🟢 New | **85%** |
| 3 | **`MaintenanceProposalStatuses.All`** — an explicit allow-list `HashSet` that reads as validation infrastructure — **is defined but never referenced anywhere in the repo.** Confirmed benign: every real `Status` mutation goes through internal code (`AppendHistory(proposal, MaintenanceProposalStatuses.Approved, ...)` etc.), never raw external input, so the missing validation currently has no attack surface. This is the third instance of the "built but never wired up" dead-artifact pattern (after the `ChatServices.cs` cluster in Report #2 and `LockoutMinutes` in Report #3), just lower stakes than either. | 🟢 New (minor) | **90%** |
| 4 | **`ExtractJsonObjectPayload`'s brace extraction is naive** (`IndexOf('{')` to `LastIndexOf('}')` across the whole trimmed response) and would mis-extract if an LLM response contains more than one brace-delimited span (e.g., an inline JSON example in prose, followed by the real answer). Confirmed this is **not currently a crash risk** — the one call site with an unguarded fallback parse is itself wrapped in an outer `catch` that degrades to a logged "LLM review skipped" warning — but it would produce a confusing/wrong skip in a case that a smarter, nesting-aware extraction would have parsed correctly. | 🟢 New (low severity, hardening) | **82%** |

---

## 1. `DirectWrite`: described capability that isn't wired to anything

**Evidence:**
```csharp
// MemorySmithOptions.cs
public bool DirectWrite { get; set; }   // defaults to false

// AdminSettingsService.cs — Admin Settings UI description
EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:DirectWrite", "Allow direct agent writes", "Maintenance agent",
    settings => settings.MaintenanceAgent.DirectWrite,
    "Allows the maintenance agent to write directly inside its configured write roots. Keep false unless the task is low-risk and the write roots are intentionally constrained.");
```
Repo-wide search for `config.DirectWrite` / `DirectWrite` turns up exactly two consumers, both inert with respect to actual write behavior:
- `MaintenanceAgentServices.cs:1599` — included in a metadata dictionary attached to run output.
- `MaintenanceAgentServices.cs:1775` — included in the JSON payload sent to the LLM as part of its `config` context (i.e., the model is *told* `directWrite: true/false`, but has no tool or code path that would let it act on that information — every actual mutation still goes through `IMaintenanceProposalStore.SaveAsync` → human approve/reject → `ApplyAsync`).

I verified `ApplyAsync` (the only method that actually writes proposal content to disk) takes an already-approved `MaintenanceWriteProposal` and has no `DirectWrite` check anywhere in its body or call chain.

**Why this is worth flagging even though the direction is "safer than described," not "riskier":** an operator who deliberately wants a low-risk automation (say, a single well-scoped maintenance task they're comfortable auto-applying without review) will set this to `true`, per the description's own suggestion ("keep false unless the task is low-risk..."), and get no behavior change at all — the setting doesn't do the thing its own description tells them to expect. That's a confusing dead end for anyone reading the settings UI in good faith, and worth resolving either by implementing the described bypass-review path (with appropriate additional guardrails, since that's now a materially riskier feature than what exists today) or by removing/relabeling the setting to be honest about being informational-only for now.

**Confidence: 90%** — the reference count is exhaustive and unambiguous; the only softening factor is that I can't rule out some other code path outside `MemorySmith.App` (e.g., a bridge/agent-side component) reading this value from the JSON prompt payload and acting on it in a way I haven't traced — though that would be an unusual and fairly convoluted design if true.

---

## 2. `RiskLevel` has no membership validation on the one path where it matters

**Evidence** — `NormalizeReviewRevision` (invoked when an LLM "reviews" a proposal and optionally proposes a revision):
```csharp
var riskLevel = string.IsNullOrWhiteSpace(revision.RiskLevel) ? original.RiskLevel : revision.RiskLevel;
...
RiskLevel = riskLevel,
```
`revision` here is deserialized directly from LLM output (`ParseProposalReview` → `MaintenanceProposalReviewEnvelope.RevisedProposal`). The only guard is "empty/whitespace falls back to the original" — any non-empty string, including a hallucinated or malformed value, passes through unchanged. Contrast with `MaintenanceProposalStatuses`, which at least has a defined `All` set (even though it's currently unused — see Finding 3) — there is no equivalent `MaintenanceProposalRiskLevels.All` set at all, so there's nothing to validate against even if someone wanted to add the check.

**Blast radius, checked rather than assumed:** I searched for every place `RiskLevel` is read for a decision (not just displayed) and found none — no code gates approval, notification urgency, or write scope on `RiskLevel`'s value. So today, a bad value only pollutes the human-facing display and audit trail (`_audit.RecordAsync(..., details: new { proposal.Status, proposal.RiskLevel, ... })`) rather than causing an incorrect automated decision. That matters because it's specifically the field a time-pressured reviewer skims to decide how carefully to read the diff — a silently-wrong "low" on a proposal that should say "high" (or an unrecognized value that a UI badge doesn't know how to color/sort) degrades the safety benefit of having risk levels at all, even without being a hard security bypass.

**Recommendation:** Add a `MaintenanceProposalRiskLevels.All` set (mirroring the existing `MaintenanceProposalStatuses.All` pattern) and clamp/validate in `NormalizeReviewRevision`: if `revision.RiskLevel` isn't a recognized value, fall back to `original.RiskLevel` (same as the existing empty-string case) rather than accepting it verbatim — and consider logging a warning when that happens, since an LLM returning an unrecognized risk level is itself a mild signal that its output should be double-checked.

**Confidence: 85%** — the code path and lack of validation are directly confirmed; the "matters because reviewers use this to triage" framing is a reasonable but not empirically-measured inference about how the review UI is actually used in practice.

---

## 3. `MaintenanceProposalStatuses.All` — unused validation scaffold (minor, confirmed benign)

**Evidence:**
```csharp
public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    Open, NeedsRevision, Approved, Rejected
};
```
Zero references anywhere in the repo (`grep -rn "MaintenanceProposalStatuses.All"` → no matches outside its own declaration).

**Why this is low severity, not a repeat of Report #2's `ChatServices.cs` cluster:** I checked every place `Status` is actually assigned a value (`Proposals.razor` and `MaintenanceAgentServices.cs`, ~20 call sites) and confirmed every single one sets it via a named constant from server-side code (`AppendHistory(proposal, MaintenanceProposalStatuses.Approved, "approve", comment)` and similar) — never from raw deserialized input. So the validation this `HashSet` seems built for is structurally unnecessary today: bad `Status` values can't currently enter the system through any code path I found. This is a smaller, lower-stakes version of the earlier dead-code pattern — worth a one-line cleanup (delete `All`, or use it defensively in the one place `Status` *is* deserialized from a file on disk in `FileMaintenanceProposalStore.ReadProposal`, in case a hand-edited or corrupted proposal JSON file on disk ever contains a bogus status) rather than an urgent fix.

**Recommendation:** Either delete `All` as unused, or — slightly better — use it in `ReadProposal`'s deserialization path to reject/flag a proposal file with a `Status` outside the known set, since that's the one place an out-of-band value (a manually edited JSON file, a future format change, a corrupted write) could actually reach this field.

**Confidence: 90%**.

---

## 4. `ExtractJsonObjectPayload`'s brace-matching is naive but currently fails safe

**Evidence:**
```csharp
var firstObjectBrace = trimmed.IndexOf('{');
var lastObjectBrace = trimmed.LastIndexOf('}');
return firstObjectBrace >= 0 && lastObjectBrace >= firstObjectBrace
    ? trimmed[firstObjectBrace..(lastObjectBrace + 1)]
    : trimmed;
```
This takes everything between the *first* `{` and the *last* `}` in the whole response. If a model's response contains two distinct brace-delimited spans — e.g., it explains its reasoning with an inline example (`"...following the shape {\"example\": true}, here's my actual answer: {\"task\": ...}"`) — this concatenates both spans (plus whatever prose sits between them) into one string, which is not valid JSON.

**Traced consequence, not assumed:** in `ParseTaskOutput`, the malformed payload fails the initial `Deserialize<MaintenanceTaskOutput>` (caught, ignored) and then hits an **unguarded** `JsonDocument.Parse(payload)` fallback that would itself throw. I confirmed the one caller of `ParseTaskOutput` (`TryRunLlmReviewAsync`) wraps the whole call in a `try` with `catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException or TaskCanceledException)`, which logs a warning and returns `null` (treated as "LLM review skipped"). So the actual current behavior for this edge case is a graceful skip-with-warning, not a crash — I want to be explicit about that so this isn't mistaken for a repeat of a more serious "unhandled exception" claim.

**Why it's still worth fixing:** the failure mode today is "a response that technically contained parseable JSON gets silently discarded with a generic warning" rather than "a response that's genuinely malformed gets discarded" — the difference matters for debugging (an operator seeing repeated "LLM review skipped" warnings would reasonably suspect the model or prompt is broken, when the actual issue is an extraction heuristic mis-slicing an otherwise-fine response) and for review quality (fewer proposals get the LLM review pass than the model's actual output quality would support).

**Recommendation:** Replace the first-to-last-brace heuristic with a nesting-depth-aware scan: walk the string tracking `{`/`}` depth (respecting string literals and escape sequences) and extract the first complete, balanced top-level object. This is a self-contained, easily unit-testable change (a handful of adversarial-response test cases: multiple objects, an object nested inside prose-with-braces, an object containing a string value with a literal `}` inside it) and doesn't touch any of the calling code's error-handling contract.

**Confidence: 82%** — the code logic and the "currently fails safe via the outer catch" conclusion are both directly verified by reading both this method and its only caller; the residual uncertainty is about how often real model output actually triggers the multi-brace edge case in practice, which I have no telemetry to measure.

---

## 5. Things checked in this file and ruled out (for transparency)

- **Path allow-listing (`ValidateWritablePath`/`ValidateReadablePath`/`IsUnderPath`)** — correctly appends a trailing separator before the prefix check (avoids the classic `C:\Allowed` matching `C:\AllowedButNot` bug) and correctly operates on `Path.GetFullPath`-resolved values (so `..` traversal can't escape the allow-list). No issue found.
- **`ApplyAsync`'s optimistic concurrency check** (`current file content must still equal proposal.Before, else throw`) — correctly prevents applying a stale proposal against content that's changed since the proposal was drafted. No issue found.
- **Revision scope creep** — a revised proposal's `Changes` (potentially pointing at different file paths than the original) are not re-validated at revision-normalization time, but *are* re-validated at `ApplyAsync` time via the same allow-list check every other proposal goes through, and still require human approval before that point. Not a bypass.
- **Transcript secret redaction regex** (`\b(api[_-]?key|token|secret|password|authorization)\b\s*[:=]\s*[^\s,;]+`) — reasonably robust; correctly matches common separator variants (`api_key`, `api-key`, `apikey`, camelCase `apiKey`) via the optional `[_-]?`. Not exhaustive against every conceivable secret format, but not the kind of narrow-keyword-list brittleness flagged elsewhere in this audit.

---

## 6. Coverage note

This completes a full line-by-line read of `MaintenanceAgentServices.cs` (2,187 lines) — the second of the five ">1,200 line" files flagged as outstanding after Report #2. Remaining: `TaskDomainService.cs`, `MemoryApplicationService.cs`, `CodeSearchService.cs` (partially covered across Reports #1/#2), and the Razor component layer.
