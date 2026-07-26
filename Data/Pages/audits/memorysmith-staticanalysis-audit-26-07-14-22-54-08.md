# MemorySmith Audit — Static Analysis Pass (semgrep) + Feasibility Notes
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` · **Commit:** `e8a3065` (unchanged)
**Report generated:** 2026-07-14

## Methodology & Feasibility

**Tool used:** [semgrep](https://semgrep.dev) 1.169.0, installed via `pip`. `semgrep.dev` (the rule registry `--config=auto` pulls from) is not reachable from this sandbox, so I wrote a **local, hand-authored ruleset** (8 rules) targeting known-risky C# patterns — empty/bare catch blocks, `async void` methods, blocking-on-async (`.Result`/`.Wait()`/`.GetAwaiter().GetResult()`), SQL built via string interpolation, hardcoded-secret-shaped assignments, `Console.WriteLine` inside service classes, and undisposed `SqliteConnection`/`SqlConnection`. Ran against all production `.cs` files (excluded `MemorySmith.Tests/`, `e2e/`): **1163 files tracked, 128 matched by extension, 23 raw findings across 3 of the 8 rules** (the other 5 rules — empty-catch, async-void, hardcoded-secret, disposable-not-disposed — matched zero instances; empty-catch and async-void's zero-hit result is itself informative, see below).

**Every one of the 23 raw findings was manually read in source** before being reported — semgrep's C# support does token/AST pattern matching without full symbol resolution, so several categories of false positive were expected and found (detailed below). Reporting raw tool output without this step would have produced a materially misleading report (e.g., "4 blocking-on-async deadlock risks" when the true count is zero).

**Dynamic analysis / fuzzing — assessed and ruled infeasible in this environment, not attempted:** running the app, or even compiling it far enough to fuzz an endpoint or harness, requires `dotnet restore`, which requires reaching `nuget.org` — not in this sandbox's allowed domain list. I confirmed this isn't a workaround-able gap: `apt-get install dotnet-sdk-8.0` was also attempted as a fallback path (in case a pre-built, non-NuGet-dependent analyzer pass was possible) and failed independently — the specific package versions referenced by this Ubuntu snapshot's `security.ubuntu.com` mirror return 404 (see raw output in this session if needed; the .deb files listed in the mirror's index aren't actually present at the expected paths, which is a mirror-sync issue unrelated to this project). Between the two blockers, no compiled, running, or even fully syntax-resolved build of this codebase is achievable here — so no Roslyn analyzers, no nullable-reference-type warnings, no coverage-guided fuzzing (e.g. against `VarResolver`'s path logic or `CodeSearchService`'s SQL builders, which would otherwise be good fuzz targets), and no dynamic/runtime analysis of any kind. This report is therefore static/source-level only, same as all prior reports in this engagement — semgrep added a second, independent detection method on top of manual reading, not a fundamentally different analysis class.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F34 | `SqliteMemorySmithDatabase.QueryRowsAsync<T>` — a shared generic paged-query helper used by 3 of the god-class's 9 store interfaces — takes `table` and `orderBy` as raw, unvalidated `string` parameters interpolated directly into SQL text; no live injection today (all 3 call sites use hardcoded literals, verified), but the method itself provides no guard against a future caller passing a dynamic value | 90% | Medium (latent SQL-injection primitive, currently safe only by caller discipline) | **New** — same category as F27 (`MergeShardAsync`'s missing service-level path guard) but for SQL identifiers instead of file paths |
| — | 3 of 5 semgrep SQL-concatenation flags, all 4 blocking-on-async flags, and most `Console.WriteLine` flags were verified as false positives — documented below for transparency, not reported as findings | 100% | N/A | Demonstrates the manual-verification step was load-bearing, not decorative |

**Notable negative result:** zero hits for the empty-catch-block and bare-catch-with-no-variable rules in production code outside the specific instances already catalogued in Delta Report 1 (F6, `ChatServices.cs`). This is worth stating plainly: it suggests F6's finding was close to a complete inventory of that pattern in this codebase, not the tip of a larger iceberg — useful confirmation for whoever picks up TSK-0346/0371.

---

## F34 — `QueryRowsAsync<T>`: unvalidated SQL identifiers in a shared repository helper (Medium, 90%)

**File:** `MemorySmith.Storage/SqliteMemorySmithDatabase.cs`, lines 847-865:
```csharp
private async Task<PagedResult<T>> QueryRowsAsync<T>(
    string table, SqlWhereClause where, int page, int pageSize,
    Func<SqliteDataReader, T> read, CancellationToken cancellationToken, string orderBy = "rowid DESC")
{
    ...
    var total = await ExecuteScalarLongAsync(connection, $"SELECT COUNT(*) FROM {table} {where.Sql};", where.Apply, cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT * FROM {table} {where.Sql} ORDER BY {orderBy} LIMIT @limit OFFSET @offset;";
    ...
}
```
`where.Sql`/`where.Apply` correctly use parameterized values throughout (verified — `SqlWhereClause` binds actual filter values via `@`-prefixed parameters, only the *shape* of the WHERE clause is string-built from internally-controlled fragments). `table` and `orderBy`, however, are spliced directly into the SQL text with no escaping, no allowlist check, and no `[ValidatedIdentifier]`-style type wrapper — this is unavoidable for table/column names in ADO.NET (you cannot parameterize a SQL identifier the way you parameterize a value), which means the *only* available defense is validating the string against an allowlist before use, and that validation doesn't exist here.

**Verified there is no live exploit today:** grepped every call site —
```
line 426: QueryRowsAsync("LoginHistory", BuildLoginWhere(query), ..., ReadLoginHistory, cancellationToken)
line 451: QueryRowsAsync("AuditMetadata", where, ..., ReadAudit, cancellationToken, "Sequence DESC")
line 719: QueryRowsAsync("ApiTokens", BuildApiTokenWhere(query), ..., ReadApiToken, cancellationToken, "CreatedAtUtc DESC")
```
All three `table` values and both explicit `orderBy` overrides are compile-time string literals. No caller currently derives either parameter from user input, config, or any other non-literal source.

**Why flag it anyway:** this is a shared private helper inside the same god-class already flagged for decomposition (F12/TSK-3081). Once that decomposition happens and this method's 9 call sites potentially grow or get touched by different engineers working on different store interfaces in parallel, the guarantee "nobody will ever pass a dynamic value here" gets weaker, not stronger — and the method's own signature gives no signal that `table`/`orderBy` are more dangerous than an ordinary string parameter. This is the same shape of issue as F27 (`MergeShardAsync` trusting callers to have pre-validated a path) and F32's underlying lesson (shared low-level helpers are exactly where a validation gap should live centrally, not be re-derived per caller) — a recurring theme across this engagement's findings, not a one-off.

**Recommendation:**
```csharp
private static readonly HashSet<string> KnownTables = new(StringComparer.Ordinal) { "LoginHistory", "AuditMetadata", "ApiTokens", /* ...rest of the 9 stores' tables... */ };
private static readonly Regex SafeOrderByPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\s+(ASC|DESC))?$", RegexOptions.Compiled);

private async Task<PagedResult<T>> QueryRowsAsync<T>(string table, ..., string orderBy = "rowid DESC")
{
    if (!KnownTables.Contains(table)) throw new ArgumentException($"Unrecognized table '{table}'.", nameof(table));
    if (!SafeOrderByPattern.IsMatch(orderBy)) throw new ArgumentException($"Unsafe orderBy clause '{orderBy}'.", nameof(orderBy));
    ...
}
```
Cheap, additive, zero behavior change for the three existing (already-safe) call sites — it only starts mattering if a future call site tries to pass something dynamic, which is exactly when you want it to fail loudly instead of silently working until someone finds the gap the hard way. **Effort:** 1-2 hours including a test asserting an out-of-allowlist `table`/`orderBy` throws.

---

## Verified False Positives (documented for transparency)

**SQL string-concatenation (3 of 5 flags):**
- `CodeSearchService.cs:1674` and `:2411` — both interpolate internally-generated **parameter names** (e.g. `@target0`, `@target1`) into clause text, with actual values bound separately via `AddWithValue`. Already verified safe in the CodeSearchService deep-dive report; semgrep's pattern can't distinguish "interpolating a parameter name" from "interpolating a value," which is the expected limitation of a rule written without full data-flow analysis.
- `SqliteMemorySmithDatabase.cs:156` (`ListAsync` for users) — the interpolated `where` variable is one of exactly two hardcoded literal strings (`string.Empty` or a fixed clause with a `@search` placeholder); the actual search text goes through `Add(command, "@search", ...)`. Verified safe.
- (The remaining 2 of 5 are F34 and the `EnsureColumnAsync` `ALTER TABLE` interpolation already discussed in the CodeSearchService report's F-series — not re-counted here to avoid double-reporting.)

**Blocking-on-async (4 of 4 flags — all false positives):** every one of these matched a plain C# property named `.Result` or `.Wait()`-*shaped* code that isn't actually `Task<T>.Result`/`Task.Wait()`. Specifically: `context.Result` (`AutoValidateAntiforgeryTokenFilter.cs`, an ASP.NET Core `FilterContext.Result` property, unrelated to `Task`), and `loopEvent.Result`/`evt.Result` (`ChatServices.cs`, `MemoryChatAgent.ToolLoop.cs` — a custom `ToolLoopResult?` property on an application-defined event type). This is the expected failure mode of a regex/AST-only rule with no symbol table — a real Roslyn analyzer (unavailable here per the feasibility note above) would resolve these correctly by type. No actual blocking-on-async calls were found in production code in this pass.

**`Console.WriteLine` in service code (12 of 14 flags):** all 12 non-`WindowsServiceCommands.cs` hits are in `MemorySmith.Benchmarks/` (a console benchmarking tool, where console output is the entire point). The 2 `WindowsServiceCommands.cs` hits are inside its `install`/`uninstall`/`--help` CLI command handlers (verified via surrounding code — explicitly gated behind `OperatingSystem.IsWindows()` checks and invoked only from an interactive elevated terminal per the tool's own help text, never from the running web service). All 14 are legitimate.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- The local semgrep ruleset (8 rules) is a reasonable but non-exhaustive substitute for `--config=auto`'s much larger community ruleset (which typically includes hundreds of C#/.NET-specific checks); treat this pass as "one more independent detection method applied," not "equivalent coverage to a fully-licensed SAST tool run." If network access to `semgrep.dev` or an offline copy of the community C# ruleset becomes available, a follow-up run would likely surface additional findings this pass couldn't.
- The dotnet-sdk apt install failure appears to be a transient mirror-sync issue (specific `.deb` versions absent from `security.ubuntu.com`'s current index) rather than a fundamental unavailability — worth a retry outside this sandbox's constraints if compiler-level analysis (nullable warnings, full Roslyn analyzers, actual fuzzing) is wanted later; even then, `dotnet restore` would still need `nuget.org` access this sandbox doesn't have, so a working build still requires an environment with broader network access than this one.
