# MemorySmith delta audit — chat UI, storage restore, and tool-parse slice

**Report ID:** `ms-b0d81585467638c8`  
**Snapshot:** `fb9f8311b72a9c20354f6eb17580d582331eeef8`

## Executive summary

This slice found three new maintenance hazards in the chat surface. First, the UI stores and restores legacy provider/model preferences with its own provider-matching logic, duplicating the same equivalence rules already present in the controller and chat services. Second, the browser-storage load path treats parse failures as empty/default state, which makes corrupted or incompatible local state hard to distinguish from a clean first run. Third, the tool-argument parser is deliberately permissive and turns malformed inputs into best-effort fallback structures, which is convenient but weakens the contract boundary for tool calls. citeturn222file0turn225file0turn217file0

I did not repeat the earlier provider-contract, persistence, or profile-service findings except where they directly connect to these new behaviors.

## Findings

| ID | Severity | Confidence | Issue | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-039 | Medium | 88% | **Semantic duplication / repeated switch** — `Chat.razor` restores legacy preferences by matching `preferences.Provider` and `preferences.Model` against `_modelProfiles` with the same provider equivalence rules already used in the controller/service layer. | The UI is now another place where provider equivalence rules live. That increases drift risk, especially if aliases or provider normalization change again. | citeturn222file0turn193file0turn184file0turn185file0 |
| D-040 | Medium | 85% | **Silent state fallback** — `LoadStorageJsonAsync<T>()` and `LoadStorageTextAsync()` treat exceptions as default/empty state after optionally warning, so corrupted or incompatible browser storage is indistinguishable from a fresh session. | This hides genuine storage corruption behind a soft reset. Users can lose context without a clear recovery path or explicit migration failure. | citeturn222file0 |
| D-041 | Low | 82% | **Speculative generality / permissive parsing** — `ReadArguments()` accepts arbitrary JSON text and falls back to `{ "query": text }` or an empty object when parsing fails. | This makes the boundary between structured tool arguments and plain text fuzzy. It improves resilience, but it also hides malformed tool payloads and can mask upstream contract drift. | citeturn217file0 |
| D-042 | Low | 79% | **Near-duplicate formatting** — `FormatToolResults()` and `FormatInterceptResults()` use nearly identical join-and-preamble assembly with only the preamble changing. | Small duplication, but it is another place where presentation rules can diverge over time. A shared formatter would reduce churn and make the trust-boundary text consistent. | citeturn219file0 |

## Detailed notes

### D-039 — Chat UI duplicates provider equivalence rules
The `Chat.razor` restore path accepts a legacy preference if it finds a profile whose provider and model match the saved pair, using `ProviderMatches(profile.Provider, preferences.Provider)` plus an exact model comparison. That means the UI itself now knows about provider aliases/compatibility and has its own legacy restore policy. The same provider-name equivalence already exists in the controller and services, so this is a third copy of the same domain rule. citeturn222file0turn193file0turn184file0turn185file0

**Fix:** move legacy preference resolution into the profile/provider service or a dedicated migration helper so the UI only asks for the resolved profile.  
**Confidence:** 88%

### D-040 — Browser-storage corruption gets treated like a clean reset
`LoadStorageTextAsync()` and `LoadStorageJsonAsync<T>()` convert exceptions into `default` after marking a load failure, and `LoadSessionsAsync()` responds by clearing sessions and starting a new chat. That is reasonable for resilience, but it also collapses a corrupted or incompatible storage format into the same behavior as “no prior local data.” citeturn222file0

**Fix:** distinguish “empty storage,” “parse failure,” and “migration needed” in the UI state model. A visible recovery warning or explicit reset affordance is better than silently replacing state with a fresh session.  
**Priority:** medium.

### D-041 — Tool-argument parsing is intentionally loose
`ReadArguments()` accepts either a JSON object or a string containing JSON text, and if parsing fails it wraps the raw text into `{ "query": text }`. That makes the chat system resilient to a lot of malformed input, but it also means malformed structured tool arguments can silently morph into a different shape rather than failing fast. citeturn217file0

**Fix:** keep the fallback if you want interactive resilience, but emit a warning or explicit parse-status marker so downstream tooling can tell whether the arguments were actually valid JSON.  
**Priority:** low.

### D-042 — Tool result presentation is duplicated
`FormatToolResults()` and `FormatInterceptResults()` share the same “preamble + untrusted data blocks + join results” structure. The only meaningful difference is the opening sentence. That is small Type-4 duplication, but it is still a consolidation candidate in a file that already handles a lot of chat orchestration. citeturn219file0

**Fix:** extract a shared formatter that takes the header sentence and result list.  
**Priority:** low.

## Task mapping and backlog fit

`TSK-0283` remains the right place for provider-contract honesty, but D-039 shows that the chat UI itself still contains provider equivalence logic and should be folded into the same seam cleanup. citeturn213file0turn222file0turn193file0turn184file0turn185file0

`TSK-0042` is still the broad chat-service decomposition bucket, but D-041 and D-042 are better framed as follow-on cleanups inside that work than as separate backlog items. citeturn209file37turn217file0turn219file0

## Implementation guidance

1. Move legacy provider/model preference recovery out of the chat page and into the profile/provider service layer.
2. Distinguish empty browser storage from parse failure or migration failure.
3. Make tool-argument parsing visibly recoverable, not silently coercive.
4. Collapse the two tool-result formatter branches into one shared helper. citeturn222file0turn217file0turn219file0

## Assumptions and open questions

- Assumption: legacy provider/model preferences still need to be restored for compatibility with older browser state. citeturn222file0
- Assumption: permissive tool-argument parsing is intended to improve chat robustness against model output variance. citeturn217file0
- Open question: should corrupted local browser storage start a fresh chat automatically, or should the UI force an explicit recovery choice? citeturn222file0
- Open question: should malformed tool arguments be accepted as best-effort text, or rejected with a typed parse failure so downstream logic can react? citeturn217file0

## Confidence notes

- D-039: 88%
- D-040: 85%
- D-041: 82%
- D-042: 79% citeturn222file0turn217file0turn219file0turn193file0turn184file0turn185file0

**Report ID for follow-up references:** `ms-b0d81585467638c8`
