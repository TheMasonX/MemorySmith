# MemorySmith delta audit

## What is new in this pass

This report contains only findings that were not already called out in the prior audit pass, plus corrections/extensions to existing task themes. The main delta is that the new auth safety work is still too configuration-local, and the source-link reader has two additional edge-case problems that were not yet captured. fileciteturn11file0turn22file0turn31file0turn43file0turn44file0

## New findings

| ID | Severity | Confidence | Delta finding | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-001 | High | 92% | The new admin self-lockout guard in `AdminController.SetProviderEnabled()` checks only the provider rows currently marked `IsEnabled`, but it does not verify the provider is actually usable at runtime. | A provider can be “enabled” in storage while still unusable because its client ID / scheme registration is missing. That means the guard can green-light a change that still leaves the instance with no working sign-in method. | fileciteturn11file0turn31file0 |
| D-002 | Medium | 94% | `VarResolver.ReadSelectedContentAsync()` enforces `maxBytes` using character counts, not byte counts. | UTF-8 content can exceed the intended limit, and the truncation message can under-report the real overage. This is especially brittle for non-ASCII source files and copied snippets. | fileciteturn43file0 |
| D-003 | Medium | 72% | The source-path authorization logic is prefix-based after `Path.GetFullPath()` normalization, but it does not resolve symlinks/junctions. | A symlink inside an allowed root can point outside the root while still passing the string-prefix check. That creates an escape hatch around the source-link boundary rules. | fileciteturn43file0turn44file0 |

## Corrections / extensions to existing task themes

`TSK-0300` should be extended, not duplicated: the lockout guard needs to validate “usable sign-in method” at runtime, not just “enabled provider row.” The cleanest implementation is to centralize one auth-safety policy service that can query both configuration and scheme availability, then have both settings mutation and provider toggles call it. fileciteturn11file0turn31file0turn38file0

`TSK-3090` should also be extended: source-link hardening needs byte-accurate truncation and path canonicalization that accounts for symlink escape routes. If the source-link surface is meant to be a security boundary, the current `GetFullPath` plus string-prefix model is not enough by itself. fileciteturn43file0turn44file0

## Open questions

- Should a provider count as “available” only when the database row is enabled, or only when it is both enabled and runtime-configured? The current code uses both models in different places. fileciteturn11file0turn31file0
- Should source-link snippet limits be defined in bytes, UTF-16 chars, or visible graphemes? The current API name says bytes, but the implementation uses chars. fileciteturn43file0
- Should symlinked paths be outright rejected in source-link resolution, or should they be canonicalized and re-checked against allowed roots? fileciteturn43file0turn44file0

## Priority recommendation

1. Fold runtime provider availability into the lockout guard and reuse it everywhere auth state is mutated.
2. Fix source snippet truncation to use real byte accounting.
3. Decide and implement a symlink policy for source-link roots, then add tests for escape attempts. fileciteturn11file0turn31file0turn43file0turn44file0

## Confidence notes

- D-001: 92% — the mismatch between storage-level enablement and runtime support is explicit in the code paths.
- D-002: 94% — `maxBytes` is directly enforced by char-based `StringBuilder` logic.
- D-003: 72% — the escape requires symlink/junction behavior, but the authorization model itself is clearly prefix-based. fileciteturn11file0turn31file0turn43file0turn44file0
