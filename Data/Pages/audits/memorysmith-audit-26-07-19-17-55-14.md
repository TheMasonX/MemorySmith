# MemorySmith delta audit — next slice

**Report ID:** `ms-801440df8e8ca07c`  
**Snapshot:** `fb9f8311b72a9c20354f6eb17580d582331eeef8`  
**Focus:** chat/provider/profile/config surfaces, duplication, brittle assumptions, and maintainability

## Executive summary

This slice found a deeper provider-contract fracture and several maintainability issues in the chat/config path. The main problems are that the chat configuration endpoint can pair the wrong provider with the wrong model, send-error formatting uses raw request/provider strings instead of the resolved provider, profile persistence can throw on null strings instead of validating them, and the profile service hard-codes a two-provider world even though the rest of the app exposes provider discovery more generally. citeturn193file0turn184file0turn185file0

The recurring pattern is the same as before: several helpers look like resolvers or normalizers, but they either preserve invalid values, fall back to arbitrary defaults, or encode policy in more than one place. That makes the system harder to extend safely and easier to misconfigure without noticing.  

## Findings

| ID | Severity | Confidence | Issue | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-026 | High | 92% | **Provider/model mismatch in `GetConfiguration()`** — the endpoint resolves `selectedProvider`, but always returns `defaultProfile?.Model` as the active model string. | The UI can receive a provider and model pair that do not belong together when provider selection diverges from the default profile. That is a correctness bug in the runtime contract. | citeturn193file0turn184file0 |
| D-027 | High | 90% | **503 error path uses raw provider strings** — `Send()` formats the fallback response using `request.Provider` and `DefaultModelForProvider(request.Provider, ...)`, not the resolved provider. | Aliases, fallbacks, and unknown values can generate misleading diagnostics and the wrong default model in the error payload. | citeturn193file0 |
| D-028 | High | 89% | **Null-trim crash in profile normalization** — `TryNormalizeRequest()`, `CloneProfile()`, and `ToView()` call `.Trim()` on request/persisted strings without guarding against null. | A null value from model binding or malformed JSON becomes a runtime exception instead of a validation failure. That is brittle for an admin-editable config surface. | citeturn184file0turn185file0 |
| D-029 | Medium | 84% | **Legacy implicit profile is suppressed too aggressively** — `ListProfiles()` only synthesizes the implicit legacy profile when there are no explicit profiles *and* no explicit profile configuration. | Partial explicit config can leave chat disabled even though a usable legacy provider/model still exists. This turns a recoverable state into an outage. | citeturn184file0turn185file0 |
| D-030 | Medium | 86% | **Provider fallback is order-dependent** — `ResolveProvider()` returns `_providers[0]` when no provider matches. | Unknown provider names silently route to whichever implementation happened to be registered first. That is brittle and hard to reason about. | citeturn193file0 |
| D-031 | Low | 78% | **`AgentWriteApprovalModes.Normalize()` is not true normalization** — it only swaps `-` for `_` and otherwise preserves arbitrary non-empty strings. | The helper looks canonicalizing, but it does not actually validate. Future callers can easily assume the returned value is one of the known modes when it is not. | citeturn190file0 |
| D-032 | Medium | 85% | **Hard-coded provider whitelist in the profile service** — `ChatModelProfileService` exposes only `["Ollama", "GitHub"]` and maps everything else to Ollama. | This bakes a two-provider world into the profile editor and makes provider extension a hidden breaking change. It duplicates provider policy instead of deriving it from actual registered providers. | citeturn184file0turn185file0turn193file0 |

## Detailed notes

### D-026 — `GetConfiguration()` mixes provider selection with a model from a different source
`GetConfiguration()` resolves a `selectedProvider` and an `endpoint`, but the `model` value still comes from `defaultProfile?.Model` instead of being resolved in the same provider context. If provider selection changes, the endpoint/provider and the model can drift apart. That breaks the mental model of a single chat runtime configuration object. `endpoint` is already derived from `selectedProvider.Name`, so the model should be derived from the same resolved provider or from a provider-specific default profile lookup.  
**Fix:** derive the model from the resolved provider, or resolve the default profile in the context of the selected provider.  
**Priority:** high.

### D-027 — Error handling should reuse the resolved provider
The 503 fallback path in `Send()` formats the error response using the raw request provider string and a literal `DefaultModelForProvider()` branch. That is the same kind of duplicated provider policy that already exists elsewhere in the codebase, but here it affects the user-visible error payload. If the request contained an alias or an invalid provider, the diagnostic string will not match the provider that the app actually attempted to use.  
**Fix:** resolve provider once and carry the resolved provider through both the happy path and the error path.  
**Priority:** high.

### D-028 — Profile data needs null-safe normalization
`TryNormalizeRequest()`, `CloneProfile()`, and `ToView()` all trim values without first checking for null. That is fine only if the entire persistence and model-binding path already guarantees non-null strings, which is not actually enforced at the boundary here. Admin-editable settings deserve explicit validation, not an implicit “we assume this is always populated” contract.  
**Fix:** treat null as invalid input for required fields and convert bad persisted records into explicit warnings or validation errors.  
**Priority:** high.

### D-029 — Partial explicit config should not kill the implicit fallback automatically
`HasExplicitProfileConfiguration(chat)` only checks whether any explicit profile list exists or whether the default profile ID is non-empty. That means a config with blank or unusable explicit profiles will suppress the legacy implicit profile even if it is the only viable fallback. The result is an avoidable chat-disablement failure mode.  
**Fix:** decide fallback based on “usable enabled default exists,” not on “some explicit config key was touched.”  
**Priority:** medium.

### D-030 — Unknown provider names should not become “first provider wins”
Returning `_providers[0]` makes provider behavior depend on registration order. That is a brittle implicit contract, especially in a system that already has both provider discovery and provider aliasing. Order-dependent fallback is easy to break during refactors and hard to observe in logs.  
**Fix:** fail closed with a clear error or pick a single explicit default provider from configuration.  
**Priority:** medium.

### D-031 — Approval-mode normalization should either validate or be renamed
`AgentWriteApprovalModes.Normalize()` swaps punctuation but does not actually canonicalize to a known allowed value. The helper’s name suggests a stronger guarantee than the implementation provides. That is a minor smell, but it is the kind of thing that later becomes a mistaken assumption in a security or workflow path.  
**Fix:** either validate against the known set or rename the helper to make it clear that it only performs punctuation normalization.  
**Priority:** low.

### D-032 — The profile editor is hard-coded to two providers
`ChatModelProfileService` exposes only `Ollama` and `GitHub` as supported providers, and `NormalizeProvider()` collapses everything else to `Ollama`. That is a very strong hidden policy. It means provider extension is not just a new provider implementation; it is also a profile-service rewrite. This duplicates provider policy in a second place instead of deriving it from the actual provider set the app has already discovered in `ChatController`.  
**Fix:** treat provider options as data from the provider registry/capability layer, not a static list in the profile service. If only two providers are truly intended, make that constraint explicit in the API and tests so it does not become accidental lock-in.  
**Priority:** medium.

## Task mapping and backlog fit

`TSK-0283` remains the right home for the provider-contract work, but it should be extended to include the chat controller’s raw-provider error formatting and the profile-service provider whitelist. Those are the same seam-honesty problem expressed in three places. citeturn210file0turn193file0turn184file0turn185file0

`TSK-0042` is still the correct backlog item for broad `ChatServices` decomposition, but these new findings are more specific: they are contract mismatches, order-dependent fallbacks, and hard-coded provider policy. That makes them extensions inside the chat seam work, not separate duplication tasks. citeturn209file37turn195file0turn193file0

## Implementation guidance

1. Make provider selection a single resolved decision and reuse it for endpoint, model, and diagnostics.
2. Replace provider order fallback with an explicit default or an error.
3. Harden profile normalization against null strings and malformed persisted profiles.
4. Move the provider whitelist out of `ChatModelProfileService` and into a shared provider registry/capability source. citeturn193file0turn184file0turn185file0turn190file0

## Assumptions and open questions

- Assumption: provider aliases such as `Copilot` are temporary compatibility bridges, not permanent domain concepts. citeturn193file0turn184file0
- Assumption: profile data may be edited through JSON or persisted from previous versions, so null-safe normalization is necessary. citeturn184file0turn185file0
- Open question: should an unknown provider be a hard error, or should the app choose a documented default provider from configuration? citeturn193file0
- Open question: should the profile editor continue to suppress the implicit fallback once any explicit profile configuration exists, even if none are usable? citeturn184file0turn185file0

## Confidence notes

- D-026: 92%
- D-027: 90%
- D-028: 89%
- D-029: 84%
- D-030: 86%
- D-031: 78%
- D-032: 85% citeturn193file0turn184file0turn185file0turn190file0turn210file0turn209file37

**Report ID for follow-up references:** `ms-801440df8e8ca07c`
