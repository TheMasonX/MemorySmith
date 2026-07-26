# MemorySmith delta audit — deeper maintainability pass

## Executive summary

This slice found several new maintainability and correctness issues beyond the earlier auth, source-link, dependency, and state-machine notes. The main themes are: settings file handling still fails open on schema-valid-but-wrong JSON; source-link resolution reloads the variable store multiple times per request and can leak sensitive paths into audit logs; URL source content is reported as existing without being fetched; and the configuration model still normalizes unknown string presets to defaults, which can hide typos and change posture silently. fileciteturn152file0turn139file0turn137file0turn138file0turn157file0turn155file0

There is also a new supply-chain/documentation issue: the user-facing About page mixes runtime packages and test-only packages into one manually curated inventory, so it is no longer a trustworthy explanation of the app’s live dependency surface. fileciteturn126file0turn117file0

## Findings

| ID | Severity | Confidence | Smell / issue | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-021 | High | 93% | **Silent schema fallback / data loss risk** — `AdminSettingsService.LoadSettingsRootAsync()` returns a new empty object when the JSON file parses but the root is not an object. | A syntactically valid but schema-invalid settings file is treated as “empty settings” and will be overwritten on save, which can silently discard operator intent. | fileciteturn152file0 |
| D-022 | Medium | 91% | **Duplicated I/O / non-atomic authorization snapshot** — `VarResolver.ReadSourceAsync()` resolves the URI and then reloads `vars.json` again inside both `GetAllowedSourceRoots()` and `GetDeniedSourceRoots()`. | One source-link operation can observe multiple variable snapshots, and the extra loads make authorization and resolution depend on timing and repeated disk reads. | fileciteturn139file0turn140file0 |
| D-023 | Medium | 89% | **Logging sensitive data** — `SourceLinksController.Open()` records both the raw `link.Uri` and the resolved URI into audit logs. | Source links can contain local filesystem paths, query strings, and expanded `%Variable%` values, so the audit trail may capture information that should be redacted or hashed. | fileciteturn137file0turn138file0 |
| D-024 | Low | 88% | **Semantic mismatch** — `ReadSourceAsync()` marks HTTP/HTTPS links as `Exists: true` even though it does not fetch or validate them. | Callers may treat an unreachable or malformed URL as available. The `Exists` field is lying about more than it knows. | fileciteturn139file0 |
| D-025 | Medium | 87% | **Primitive obsession / hidden defaults** — `MemorySmithSecurityProfiles.Normalize()` and `MermaidRestrictionModes.Normalize()` silently coerce unknown strings to fallback values. | Typos or unexpected config values do not fail fast; they silently change the security posture or rendering policy, which makes configuration drift hard to detect. | fileciteturn157file0 |
| D-026 | Medium | 90% | **Documentation / supply-chain drift** — the About page’s dependency table mixes runtime packages with test-only packages and is manually maintained. | It is no longer a reliable dependency inventory, and it inflates the perceived runtime package surface by blending app and test dependencies. | fileciteturn126file0turn117file0 |
| D-027 | Low | 84% | **Shotgun surgery in configuration policy** — `MemorySmithLocalDevelopmentPostConfigure` repeats the same posture keys and safe defaults across security-profile branches and environment-specific overrides. | Every new posture-related setting must be updated in multiple branches, which is a classic maintenance trap and increases drift risk. | fileciteturn155file0turn157file0 |

## Detailed findings

### D-021 — Non-object JSON silently becomes empty settings
`AdminSettingsService.LoadSettingsRootAsync()` returns a new `JsonObject` both when the file is missing and when the parsed JSON root is not a `JsonObject`. That means a file that is valid JSON but has the wrong top-level shape is treated the same as “no settings file at all.” fileciteturn152file0

**Why this is a smell:** it is a silent schema fallback in a write path. If a human or tool accidentally writes a JSON array/string/null at the override path, the next admin edit can obliterate the original file contents without any diagnostic signal.  
**Fix:** reject non-object roots with a hard error, or at least surface a specific validation result that blocks writes until the file is repaired.  
**Confidence:** 93%

### D-022 — Variable store is reloaded multiple times inside one source-link operation
`ReadSourceAsync()` calls `Resolve(link.Uri)` up front, and `Resolve()` itself loads the variable store. Later, `TryAuthorizeSourcePath()` calls `GetAllowedSourceRoots()` / `GetDeniedSourceRoots()`, and each of those loads the variable store again. That means one request can resolve, allow, and deny against different snapshots if `vars.json` changes mid-flight. fileciteturn139file0turn140file0

**Why this is a smell:** it is duplicated I/O with a hidden consistency contract. The operation should probably use one variable snapshot, not three.  
**Fix:** load the vars once per request, pass the snapshot through resolution and authorization, and make the operation explicitly atomic with respect to the variable set.  
**Confidence:** 91%

### D-023 — Audit logging captures raw and expanded source-link values
`SourceLinksController.Open()` records `link.Uri` as the target ID and also logs `resolvedUri` in the details object. The `SourceLink` model explicitly allows `%Variable%` expansion and local file paths, so the audit log can contain both the pre-expansion and post-expansion forms of potentially sensitive paths. fileciteturn137file0turn138file0

**Why this matters:** audit logs often get broader access than the original feature surface. Logging file layout, repo paths, or embedded tokens makes the audit trail itself a disclosure channel.  
**Fix:** log a redacted path, a stable hash, or a normalized identifier instead of the raw URI and resolved filesystem path. Preserve enough context for triage without storing the whole secret-bearing string.  
**Confidence:** 89%

### D-024 — URL source existence is overstated
In `ReadSourceAsync()`, any `http://` or `https://` source returns `Exists: true` even though the resolver does not fetch it and cannot confirm reachability. The result says the source exists when the code only knows that it looks like a URL. fileciteturn139file0

**Why this matters:** callers may treat a dead URL the same as a live one. That is a misleading contract for any UI or downstream consumer that relies on `Exists`.  
**Fix:** separate “is a syntactically valid URL” from “was fetched and verified,” or mark URL sources as unknown/unverified instead of existing.  
**Confidence:** 88%

### D-025 — Config model still hides typos by defaulting unknown strings
`MemorySmithSecurityProfiles.Normalize()` maps any unrecognized profile to `SecureLocal`, and `MermaidRestrictionModes.Normalize()` maps any unrecognized mode to `Restricted`. That is a classic hidden-default pattern for stringly-typed configuration. fileciteturn157file0

**Why this matters:** a typo in config silently changes behavior instead of failing loudly. In security-sensitive settings, that is especially dangerous because the app will still start and appear healthy.  
**Fix:** replace these with validated options binding that rejects unknown values, or at least emit startup diagnostics that call out the fallback explicitly.  
**Task fit:** this is an extension of the older security-profile preset work (`TSK-0024`) and the security regression matrix (`TSK-0040`), not a new unrelated task. fileciteturn161file0turn163file0  
**Confidence:** 87%

### D-026 — About page mixes runtime and test dependencies
The About page’s package table lists runtime packages together with `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.NET.Test.Sdk`, `NUnit`, `NUnit.Analyzers`, and `NUnit3TestAdapter`, while the app project file itself does not declare those as runtime dependencies. That makes the page a muddled mix of runtime, test, and documentation concerns. fileciteturn126file0turn117file0

**Why this matters:** the page is meant to explain the supply chain, but it now overstates the application’s runtime dependency surface by blending in test-only packages.  
**Fix:** move test-only packages out of the user-facing inventory or split the page into runtime dependencies and test/dev tooling with clearly labeled sections fed from build artifacts.  
**Task fit:** this is a follow-on to dependency hygiene work (`TSK-0046`), but it is more specific: the goal is accurate dependency explanation, not just pruning stale references. fileciteturn119file0turn126file0turn117file0  
**Confidence:** 90%

### D-027 — Post-configure posture has repeated branches and duplicate setting keys
`MemorySmithLocalDevelopmentPostConfigure` applies the same posture keys across multiple branches, while also layering local-development overrides after security-profile application. That is workable, but each new posture-related setting requires edits in several branches, and the policy is spread across profile normalization, branch logic, and environment-specific overrides. fileciteturn155file0turn157file0

**Why this matters:** it is a small but real shotgun-surgery surface. The code is safe by convention today, but it is easy for one branch to drift or for a new setting to be missed in a single profile.  
**Fix:** consolidate posture defaults into a single profile table or builder, and keep environment overrides as a separate overlay so the merge order is explicit and testable.  
**Confidence:** 84%

## Task mapping and backlog fit

`TSK-0024` is already done, so the new config-validation concern should be treated as a follow-on correction to the preset system, not a duplicate of the original profile introduction task. fileciteturn161file0

`TSK-0040` is the right parent for validation and regression coverage across remote profile, proxy, and auth combinations. The new config-typo and posture-default problems belong there as matrix cases or acceptance criteria. fileciteturn163file0

`TSK-0046` remains the right ancestor for dependency-surface hygiene, but the About-page issue is not just “remove a package”; it is “stop mixing runtime and test dependencies in a user-facing inventory.” fileciteturn119file0turn126file0turn117file0

## Implementation guidance

1. Make settings-file root validation fail closed on non-object JSON.
2. Load variable data once per source-link request and pass a snapshot through resolution and authorization.
3. Redact source-link audit fields.
4. Split URL validity from URL reachability in `SourceContent`.
5. Replace string fallback normalizers with validated config binding or explicit startup diagnostics.
6. Rebuild the dependency inventory from the build graph and separate runtime vs test packages. fileciteturn152file0turn139file0turn137file0turn139file0turn157file0turn126file0turn117file0

## Assumptions and open questions

- Assumption: the admin settings file is intended to be operator-editable, so schema validation matters more than silent recovery. fileciteturn152file0
- Assumption: source-link audit logs are expected to be retained and viewed by more people than the raw source-link request surface. fileciteturn137file0turn138file0
- Open question: should unknown security-profile and Mermaid restriction values fail startup, or should they remain safe defaults but emit an explicit warning? The current code silently falls back. fileciteturn157file0
- Open question: should `Exists` mean “syntactically valid and reachable,” or just “looks like a URL”? The current contract uses the latter but the name suggests the former. fileciteturn139file0

## Confidence notes

- D-021: 93%
- D-022: 91%
- D-023: 89%
- D-024: 88%
- D-025: 87%
- D-026: 90%
- D-027: 84% fileciteturn152file0turn139file0turn137file0turn138file0turn157file0turn126file0turn117file0turn155file0turn161file0turn163file0
