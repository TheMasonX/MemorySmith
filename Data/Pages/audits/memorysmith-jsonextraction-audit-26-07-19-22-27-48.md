# MemorySmith Audit — `ExtractJsonObjectPayload`: Naive Brace-Matching Against Real LLM Output Shapes
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-19
**Method:** continued the deep read of `MaintenanceAgentServices.cs`'s remaining orchestration methods — `SubmitOutputProposalsAsync` and `BuildDeterministicOutput`/`BuildDeterministicProposals` (clean; deliberate, specific exception filtering, no findings), then `TryRunLlmReviewAsync` and its JSON-parsing support methods, which is where this report's finding comes from.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F58 | `ExtractJsonObjectPayload` — the sole mechanism turning a raw LLM chat completion into parseable JSON for both the maintenance-run review (`TryRunLlmReviewAsync`) and the proposal-review (`BuildProposalReviewPrompt`/`ParseProposalReview`) features — uses a naive "first `{` to last `}`" substring extraction with no brace-depth or string-literal awareness, and its code-fence stripping only triggers if the fence is the literal first three characters of the response. Both are fragile against extremely common, well-documented LLM response shapes (a sentence of preamble before a code fence; a JSON string value that itself contains a `{`/`}` character) | 85% | Medium-High (silently degrades or discards real LLM review output whenever the model does something LLMs routinely do, despite an explicit system-prompt instruction not to — not a crash, but a quiet reliability gap in a whole feature) | **New** — no existing task covers this; confirmed this exact pattern is not duplicated elsewhere in the codebase, so this is a self-contained, single-location fix |

---

## F58 — Brace-counting JSON extraction breaks on common LLM output shapes (Medium-High, 85%)

**File:** `MemorySmith.App/Services/MaintenanceAgentServices.cs`, `ExtractJsonObjectPayload`, lines 1856-1877:
```csharp
private static string ExtractJsonObjectPayload(string content)
{
    var trimmed = (content ?? string.Empty).Trim();
    if (trimmed.StartsWith("```", StringComparison.Ordinal))
    {
        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd >= 0)
        {
            trimmed = trimmed[(firstLineEnd + 1)..].Trim();
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3].Trim();
            }
        }
    }

    var firstObjectBrace = trimmed.IndexOf('{');
    var lastObjectBrace = trimmed.LastIndexOf('}');
    return firstObjectBrace >= 0 && lastObjectBrace >= firstObjectBrace
        ? trimmed[firstObjectBrace..(lastObjectBrace + 1)]
        : trimmed;
}
```

**Two independent, compounding fragility points:**

1. **Code-fence detection only fires if the fence is the literal first three characters.** `trimmed.StartsWith("```")` is a binary, all-or-nothing check. Any leading prose at all — even a single short sentence, which is an extremely common LLM habit despite an explicit system-prompt instruction to the contrary (this file's own system prompt at line 1742 says *"Return only strict JSON using the required maintenance task envelope"* — the team clearly already knows this needs steering, which is itself evidence they expect models to sometimes not comply) — causes this entire branch to be skipped. The response then falls straight through to brace-matching against the **whole original string, prose included.**
2. **Brace-matching is "first `{` in the string, last `}` in the string," with no depth-counting and no awareness of string literals.** This is correct only when the JSON object is genuinely the first `{` and last `}` in the text being scanned. It breaks under either of two realistic conditions: (a) leading/trailing prose (per point 1) that happens to contain any brace character at all — plausible in natural language referring to "the `{config}` section" or similar, and not something a prompt instruction reliably prevents; or (b) a JSON string *value* inside the actual payload containing brace characters — e.g. a `Message` or `Comments` field describing template syntax, a code snippet, or markdown containing `{}`, all of which are exactly the kind of content a maintenance-finding description or a proposal-review comment could legitimately contain. In either case, the extracted substring is not the true JSON object, and downstream `JsonSerializer.Deserialize` throws a `JsonException`.

**Why this doesn't crash anything, but is still a real reliability gap worth fixing:** both consumers (`ParseTaskOutput`, `ParseProposalReview`) correctly catch `JsonException` and fall back to a degraded manual-property-extraction path (or, in `ParseTaskOutput`'s case, a hand-rolled `JsonDocument.Parse` fallback reading only `task`/`warnings`/`confidence`). So a malformed extraction doesn't take down the maintenance run — but it **silently discards whatever real review content the LLM actually produced**, replacing a genuinely useful review (findings, proposals, detailed comments) with a bare-bones fallback object, with only a logged warning (`_logger.LogWarning`) as any trace of what happened. From an operator's perspective, the maintenance-agent LLM review feature would appear to work most of the time and then intermittently produce oddly empty or generic-looking review output, for a root cause (a stray brace in the model's own prose, or a brace inside a legitimate field value) that's essentially invisible without reading this exact method.

**Recommendation:** replace both fragility points with more robust equivalents:
1. For code-fence handling, search for a fenced block **anywhere** in the response (a regex like ```` ```(?:json)?\s*\n([\s\S]*?)\n``` ```` applied with `Regex.Match`, not just a `StartsWith` check), and only fall through to raw-content handling if no fenced block is found at all.
2. For extracting the JSON object itself, use a proper brace-depth counter that also tracks whether the scanner is currently inside a string literal (respecting `\"` escapes) — this is a well-understood, small, self-contained state machine (track `depth`, `inString`, `escaped` flags while scanning character-by-character) and is the standard robust approach for exactly this "find the balanced JSON object embedded in arbitrary surrounding text" problem. This removes both the leading/trailing-prose fragility and the string-literal-brace fragility at once.
Given both `ParseTaskOutput` and `ParseProposalReview` call this same helper, fixing it once fixes both consumers — no duplication to worry about.
**Effort:** half a day including a focused test file covering: a response with leading prose before a fenced block; a response with no fence at all, just raw JSON; a response where a field value legitimately contains a brace character; and the current happy-path cases (fence-first, no prose) to confirm no regression. This is exactly the kind of small, self-contained utility that's cheap to get very good unit-test coverage on, and — given it's parsing untrusted-shaped (if not adversarial) LLM output feeding into a feature that creates real file-write proposals — worth that investment.
**Confidence (85%):** the code-level fragility is unambiguous and directly read. The practical-frequency claim (LLMs commonly add preamble text despite instructions, and finding/comment text can plausibly contain braces) is based on well-established, widely-documented LLM behavior patterns rather than something empirically measured against this specific project's actual model/provider configuration — I don't have visibility into which provider/model this deployment actually points `config.Provider`/`config.Model` at, or how reliably that specific model follows the "strict JSON only" instruction in practice, which is the one variable that would let this be stated with more certainty.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not verify this codebase's actual configured LLM provider/model's real-world compliance rate with "return only strict JSON" instructions — the finding's practical-frequency claim rests on general, well-documented LLM behavior rather than a measurement specific to this deployment.
- This closes out `MaintenanceAgentServices.cs`'s `TryRunLlmReviewAsync`/JSON-parsing region; `FormatProposalReviewComment`, `ReviewProposalAsync`'s calling context, and whatever remains at the very end of the file past line ~1880 were not read in this specific pass and remain open scope.
