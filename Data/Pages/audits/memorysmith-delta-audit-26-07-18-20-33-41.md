# MemorySmith delta audit — next slice

## Executive summary

This slice found four more maintainability and correctness issues that sit below the previously reported hot spots: repeated silent normalization of invalid config values, a settings override loader that collapses non-object JSON into an empty object, an admin settings update path that is not robust to file I/O failures, and ambiguous ambient discovery of the override file from ancestor directories. I also found one smaller doc/code mismatch on `SourceLink` line-window defaults. fileciteturn157file0turn152file0turn154file0turn155file0turn138file0turn139file0

The common thread is hidden fallback. The code repeatedly prefers “pick a default and keep going” over surfacing invalid input or configuration drift, which makes the system harder to reason about and harder to operate safely.  

## Findings

| ID | Severity | Confidence | Smell / issue | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-021 | Medium | 88% | **Duplicated Code / Primitive Obsession / Speculative Generality** — multiple config normalizers silently coerce invalid strings to built-in defaults: `SecurityProfiles.Normalize()` falls back to `SecureLocal`, `MermaidRestrictionModes.Normalize()` falls back to `Restricted`, and `NormalizeAuthenticatedDefaultRole()` falls back to `Viewer`. | Typos or unexpected values do not fail fast; they quietly change behavior. This is a repeated pattern across the config surface and is easy to miss in review. | fileciteturn157file0turn146file0 |
| D-022 | High | 94% | **Silent data-loss / hidden contract** — `AdminSettingsService.LoadSettingsRootAsync()` treats any parsed JSON node that is not a `JsonObject` as `new JsonObject()`. | A valid JSON array, scalar, or other shape is silently discarded and looks identical to “empty settings,” which can mask operator mistakes and file corruption. | fileciteturn152file0 |
| D-023 | Medium | 86% | **Ambient filesystem discovery / brittle assumptions** — `MemorySmithConfigurationPaths.ResolveSettingsOverridePath()` walks ancestor directories and returns the first matching override file from the app base path, an ancestor `MemorySmith.App` path, or an `artifacts/MemorySmith.App` path. | The active settings file depends on where the app is launched from and what sibling trees happen to exist. That makes configuration resolution non-local and easy to misread. | fileciteturn154file0turn155file0 |
| D-024 | Medium | 84% | **Uncaught I/O / temp-file cleanup gap** — `AdminSettingsService.UpdateAsync()` only catches `JsonException` while reading the override file; write and move operations are also unguarded, and there is no temp-file cleanup if the write/move fails. | The admin UI can surface a 500 for ordinary I/O failures, and a failed update can leave stale `.tmp` files behind. That is fragile for an admin-facing settings editor. | fileciteturn152file0 |
| D-025 | Low | 79% | **Code/comment mismatch** — `SourceLink` documents “default to StartLine + 49 (50 lines)” when `EndLine` is omitted, but the reader actually uses the configured `ReadContextLinesAfter` value. | The contract is described one way in the model comment but implemented another way in the reader, which makes the feature harder to trust and debug. | fileciteturn138file0turn139file0 |

## Detailed findings

### D-021 — Repeated silent normalizers across the config model
The config model contains multiple string normalizers that map invalid or unknown values to a default rather than reporting an error. `MemorySmithSecurityProfiles.Normalize()` returns `SecureLocal` when a profile string is not recognized; `MermaidRestrictionModes.Normalize()` returns `Restricted`; and `NormalizeAuthenticatedDefaultRole()` maps every value except `Editor` to `Viewer`. These are all the same idea expressed in different places. fileciteturn157file0turn146file0

**Why this is a smell:** it is a family of repeated fallback logic that makes invalid configuration indistinguishable from intentional configuration. In a system that is trying to reduce ambiguity, this is a hidden contract.  
**Fix:** validate config inputs explicitly and emit a startup/admin warning for unknown values. If defaults are intended, make them explicit in the config schema and diagnostics instead of relying on silent coercion.  
**Confidence:** 88%

### D-022 — Non-object JSON collapses to empty settings
`LoadSettingsRootAsync()` returns `new JsonObject()` whenever `JsonNode.ParseAsync` yields anything other than a `JsonObject`. That means a syntactically valid JSON array or scalar is silently treated as “no settings,” even though it is clearly not a valid settings object for this feature. fileciteturn152file0

**Why this matters:** this is a form of silent data loss. It also makes troubleshooting much harder because malformed-but-valid JSON and genuinely absent settings both produce the same state.  
**Fix:** distinguish “missing file,” “invalid JSON,” and “wrong JSON shape.” Reject non-object JSON explicitly and return a clear admin-facing validation error.  
**Task fit:** this should be folded into `TSK-0181` as an extension, because that task already exists to surface or block malformed settings-override fallback behavior. fileciteturn179file0turn152file0

### D-023 — Override file discovery is ambient and ambiguous
`ResolveSettingsOverridePath()` searches the app base directory, then ancestor `MemorySmith.App` folders, then ancestor `artifacts/MemorySmith.App` folders, and returns the first existing file. That is convenient for local development, but it also makes configuration resolution dependent on the current working tree layout and the presence of unrelated sibling folders. fileciteturn154file0turn155file0

**Why this matters:** the active settings file is not obviously the one the operator thinks they are editing. That is a brittle operational assumption and a source of “it works on my machine” configuration drift.  
**Fix:** make the override path explicit by default, and keep discovery as an opt-in dev convenience with a visible warning when a discovered path is used.  
**Task fit:** this is also an extension of `TSK-0181`, but the emphasis here is the discovery mechanism, not just malformed JSON. fileciteturn179file0turn154file0turn155file0

### D-024 — Admin settings update is not resilient to ordinary I/O faults
`UpdateAsync()` catches `JsonException`, but not `IOException` or `UnauthorizedAccessException` from `File.OpenRead`, `File.WriteAllTextAsync`, or `File.Move`. The method also does not clean up the temp file if the write/move fails after the temp file has been created. fileciteturn152file0

**Why this matters:** the admin UI can fail with an unhelpful 500 in normal operational cases such as a locked file, missing directory permissions, or transient filesystem issues. That is especially brittle for the feature that operators will use to repair configuration.  
**Fix:** catch and report I/O failures as explicit admin errors, and wrap the write/move sequence in a `try/finally` that removes the temp file on failure.  
**Confidence:** 84%

### D-025 — SourceLink docs and implementation disagree on default windowing
`SourceLink` documents that if `StartLine` is set and `EndLine` is null, the default is `StartLine + 49` (50 lines total). The reader implementation instead uses the configured `ReadContextLinesAfter` option when `EndLine` is absent. fileciteturn138file0turn139file0

**Why this matters:** it is a small contract mismatch, but it makes the feature harder to reason about and undermines the value of the model comment as a source of truth.  
**Fix:** align the comment with the implementation or make the fallback explicit in the model/API contract.  
**Confidence:** 79%

## Task mapping and backlog fit

`TSK-0181` is the clearest ancestor for the override-file issues. The new additions here are not separate bug classes so much as tighter corrections: non-object JSON should fail explicitly, and ancestor discovery should be made opt-in or at least loudly visible. fileciteturn179file0turn154file0turn155file0turn152file0

`TSK-0024` remains related to the security-profile preset system, but this report extends it with a more specific concern: unknown profile strings and related config strings are normalized silently instead of being validated. That should be treated as an extension to the preset system, not a new duplicate task. fileciteturn161file0turn157file0turn146file0

## Implementation guidance

1. Replace silent normalizers with explicit validation and warning paths.
2. Make settings-override loading reject wrong-shaped JSON instead of collapsing it to empty state.
3. Tighten admin settings I/O error handling and temp-file cleanup.
4. Reduce ambient override-file discovery or make it visibly opt-in.
5. Align `SourceLink` documentation with the actual read-window behavior. fileciteturn157file0turn152file0turn154file0turn155file0turn138file0turn139file0

## Assumptions and open questions

- Assumption: silent coercion of config strings is not intended as a permanent policy, only a convenience during early bring-up. If it is intentional, the behavior still needs clearer diagnostics. fileciteturn157file0turn146file0
- Assumption: the settings override file is meant to be operated by humans outside the app process, which makes opaque I/O failures especially undesirable. fileciteturn152file0turn154file0turn155file0
- Open question: should discovered settings files be allowed at all in production-like environments, or only when an explicit override path is configured? fileciteturn154file0turn155file0
- Open question: should invalid enum-like settings block startup/config reload, or should they be applied with a warning and a known-safe fallback? fileciteturn157file0turn146file0

## Confidence notes

- D-021: 88% — three separate normalizer sites show the same pattern.
- D-022: 94% — the empty-object fallback is explicit.
- D-023: 86% — the discovery algorithm is explicit and depends on ambient layout.
- D-024: 84% — I/O exceptions are not handled in the write path.
- D-025: 79% — the doc/implementation mismatch is clear, though low severity. fileciteturn157file0turn146file0turn152file0turn154file0turn155file0turn138file0turn139file0
