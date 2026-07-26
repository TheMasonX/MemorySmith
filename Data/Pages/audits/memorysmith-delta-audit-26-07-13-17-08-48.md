# MemorySmith delta audit

## Delta scope

This pass adds only new findings and corrections that were not already captured in the prior audit notes. It focuses on hidden fallback paths, audit continuity, and file-backed configuration behavior that can quietly mask corruption or misconfiguration. fileciteturn92file0turn96file0turn76file0turn98file0

## New findings

| ID | Severity | Confidence | Delta finding | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-004 | High | 89% | Local-development settings loading is still fail-open: `ResolveSettingsOverridePath()` searches ancestor and artifacts trees, and `LoadOverrideKeys()` swallows JSON/IO/permission failures by returning an empty override set. | A malformed or inaccessible override file can silently re-enable profile defaults and local-dev relaxations instead of forcing an operator to fix the config. That is a hidden legacy fallback in a greenfield system. | fileciteturn91file0turn92file0turn98file0 |
| D-005 | High | 96% | `RequestMetadata.LoadOrCreateHmacKey()` catches all exceptions and generates a fresh random key when the stored HMAC key cannot be read. | Hashes for IP/user-agent/request metadata stop being stable across restarts or corruption events, which weakens audit continuity and makes correlation comparisons unreliable. | fileciteturn96file0 |
| D-006 | Medium | 88% | `FileVarStore.Load()` returns an empty dictionary on any parse/read failure, and `VarResolver` reloads that file on every resolution call. | A corrupt `vars.json` does not fail fast; it quietly degrades all `%Variable%` resolution into unresolved tokens and can make source-link behavior look “mostly working” while actually broken. | fileciteturn76file0turn43file0 |

## Corrections / task extensions

`TSK-0023` should be extended, not duplicated: the remaining issue is not remote access gating itself, but the fact that malformed local override/config discovery can still reintroduce permissive defaults under the same security profile machinery. The fix belongs in the config-loading path, not in another controller guard. fileciteturn80file0turn92file0turn98file0

`TSK-3090` should be extended with variable-store failure handling: source-link security is not just path boundaries, it also depends on deterministic `%Variable%` expansion. Today a corrupted vars file causes silent fallback rather than a clear operational fault. fileciteturn87file0turn76file0turn43file0

`TSK-0157` is orthogonal and already covers the SQLite adapter decomposition problem, so it should not be expanded with these file-fallback issues. The new work is about fail-closed behavior and abstraction around file-backed config/metadata paths, not more SQLite slicing. fileciteturn79file0turn77file0

## Open questions

- Should malformed local override files fail closed with a hard startup/configuration error, or should they be surfaced in diagnostics and still allow the app to start? The current behavior is silent fallback. fileciteturn92file0turn98file0
- Should request-metadata HMAC key corruption be fatal so audits remain comparable, or should a new key be rotated only with an explicit operator action? The current code does the latter implicitly. fileciteturn96file0
- Should `vars.json` corruption block source-link reads entirely instead of returning unresolved tokens? At the moment the error is recorded but the caller gets a soft failure path. fileciteturn76file0turn43file0turn95file0

## Confidence notes

- D-004: 89% — both the ancestor search and swallow-to-empty behavior are explicit.
- D-005: 96% — the catch-all + random-key fallback is direct in code.
- D-006: 88% — the storage corruption fallback and per-call reload path are directly visible. fileciteturn91file0turn92file0turn96file0turn76file0turn43file0
