# MemorySmith delta audit — next slice

**Report ID:** `ms-b6bd4163dce84c49`  
**Target snapshot:** `fb9f8311b72a9c20354f6eb17580d582331eeef8`  
**Focus:** chat/provider/profile/config surfaces and the adjacent admin settings path

## Executive summary

This slice turned up a tighter set of behavioral defects than the last one, mostly around chat/provider resolution and profile persistence. The biggest risk is that the app can silently mix the wrong provider with the wrong model, or reject valid-but-partially-shaped profile/config data with an exception rather than a validation error.

I also found one design-level gap: the profile service still treats “explicit profile config exists” as a reason to suppress the legacy implicit profile, even when the explicit configuration does not actually produce a usable default. That can turn a recoverable config state into a hard chat disablement.

## Findings

| ID | Severity | Confidence | Issue | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-026 | High | 92% | **Provider/model mismatch in `GetConfiguration()`** — the endpoint chooses `selectedProvider` from the query/default profile, but always uses `defaultProfile.Model` for the returned model string. | If the caller overrides provider, or the selected provider does not match the default profile, the UI receives a provider/model pair that does not belong together. That is a correctness bug, not just a cosmetic issue. | `ChatController.cs:33-55` |
| D-027 | High | 90% | **503 error path formats the wrong provider/model** — `Send()` uses the raw request provider string and `DefaultModelForProvider(request.Provider, ...)` instead of the resolved provider. | Aliases such as `Copilot`, unsupported values, or provider fallbacks can produce misleading error messages and the wrong “default model” in diagnostics. | `ChatController.cs:69-75, 127-139` |
| D-028 | High | 89% | **Null-trim crash on profile upsert / clone / view** — `TryNormalizeRequest()`, `CloneProfile()`, and `ToView()` call `.Trim()` on `Name`, `Model`, and `Description` without null guards. | Malformed JSON, legacy settings, or a partially populated request can throw `NullReferenceException` instead of returning a validation error. This is especially brittle for an admin-editable configuration path. | `ChatModelProfileService.cs:75-80, 128-137, 36-52` |
| D-029 | Medium | 84% | **Implicit default is suppressed too aggressively** — `ListProfiles()` falls back to the implicit legacy profile only when there are zero explicit profiles *and* no explicit profile config at all. If explicit config exists but produces no usable profile, the method returns an empty list. | A stale `DefaultModelProfileId`, blank profile IDs, or other partial explicit config can leave the chat surface disabled even though the legacy provider/model settings still exist. | `ChatModelProfileService.cs:68-85` |
| D-030 | Medium | 86% | **Unsupported provider silently falls back to the first registered provider** — `ResolveProvider()` returns `_providers[0]` when no match is found. | The active provider then depends on DI registration order, which is opaque and easy to change accidentally. Unknown provider names should fail closed or resolve deterministically. | `ChatController.cs:127-133` |
| D-031 | Low | 78% | **`AgentWriteApprovalModes.Normalize()` is not really canonicalization** — it only replaces `-` with `_` and otherwise returns arbitrary non-empty strings. | This makes the helper look like a validator while preserving invalid values, which increases the chance of a future caller assuming the result is guaranteed to be one of the known modes. | `MemorySmithOptions.cs:175-187` |

## Detailed notes

### D-026 — `GetConfiguration()` mixes the selected provider with the default profile model
`GetConfiguration()` resolves a provider into `selectedProvider`, then separately derives `model` from `defaultProfile?.Model`. Those two values are independent. If the provider query string changes the provider, or if the default profile was configured for a different provider, the response can advertise an endpoint for one provider and a model from another. That is a real contract mismatch. `endpoint` is already derived from `selectedProvider.Name`, so the model should be derived from the same resolved provider or from a provider-specific default profile lookup.  
**Fix:** derive the model from the resolved provider, or resolve the default profile in the context of the selected provider.  
**Priority:** high.

### D-027 — The `Send()` error path should use the resolved provider, not the raw request string
The 503 handler in `Send()` formats errors from `request.Provider` and a `DefaultModelForProvider()` helper that only checks for the literal `GitHub` string. That means aliases such as `Copilot`, a defaulted provider, or an unknown provider string can lead to misleading diagnostics. The chat path itself may still have resolved the provider correctly; the error path does not reuse that resolution.  
**Fix:** resolve provider once, reuse the resolved provider name for both dispatch and error formatting, and avoid raw string fallback in the exception path.  
**Priority:** high.

### D-028 — Profile edits can throw on null values instead of validating them
`TryNormalizeRequest()` trims `request.Name` and `request.Model` immediately. `CloneProfile()` and `ToView()` do the same to persisted profiles. Those code paths assume the strings are never null, but the DTOs and JSON-backed settings are not actually enforcing that at the boundary. A null coming from model binding or malformed persisted JSON becomes a runtime exception.  
**Fix:** guard every externally sourced string before trimming, and return an explicit validation error for null/empty names or models. For persisted profiles, either sanitize on load or treat null fields as invalid profile records and skip them with a warning.  
**Priority:** high.

### D-029 — The legacy implicit chat profile is not a true fallback once any explicit config exists
`ListProfiles()` has two states: either explicit profiles are present, or the implicit legacy default is synthesized. But the switch from one mode to the other is keyed off `HasExplicitProfileConfiguration(chat)`, not off “is there at least one usable profile.” That means a stale `DefaultModelProfileId`, blank profile IDs, or another partial explicit state can suppress the implicit fallback even when it would be the only recoverable configuration.  
**Fix:** decide fallback based on whether a usable enabled default profile exists, not merely whether explicit config keys are present.  
**Priority:** medium.

### D-030 — Provider fallback is order-dependent and can silently route to the wrong backend
`ResolveProvider()` returns the first registered provider if the requested name does not match. This is convenient during development, but it is dangerous in production because the fallback depends on DI registration order. If the app ever gains more than one provider implementation, an unknown provider string should not quietly become “whatever was registered first.”  
**Fix:** reject unknown providers with a clear error, or pick a single explicit default provider from configuration rather than `_providers[0]`.  
**Priority:** medium.

### D-031 — Approval-mode normalization should return a known mode or fail
`AgentWriteApprovalModes.Normalize()` converts `-` to `_`, but otherwise preserves the input. That means `Normalize("manual-ish")` does not normalize to `manual`; it returns `manual_ish`. The helper therefore acts more like a formatting helper than a validator, even though its name implies canonicalization.  
**Fix:** either validate and map unknown values to `manual`, or rename the helper to make it clear that it only performs punctuation normalization.  
**Priority:** low.

## Triage guidance

Start with the chat controller defects first, because they affect user-visible configuration and runtime behavior immediately. Then fix the null-guarding in `ChatModelProfileService`, because that is the most likely source of admin-facing crashes during profile edits or reloads. After that, decide whether the legacy implicit profile should remain a recovery path when explicit profile config is partial.

## Notes for the next slice

The next pass should look at the consumer side of these settings, especially anywhere provider/model selection is copied again instead of being resolved once and reused. That is the pattern most likely to produce the next correctness bug.

**Report ID for reference:** `ms-b6bd4163dce84c49`
