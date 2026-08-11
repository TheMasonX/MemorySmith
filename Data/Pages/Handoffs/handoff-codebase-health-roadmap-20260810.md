# Codebase Health Roadmap Handoff - 2026-08-10

## Purpose

Continue the implementation of the roadmap in [the codebase maturity health report](../audits/codebase-maturity-health-portability-20260810.md). This handoff covers the first feedback-restoration slice in the `MemorySmith` repository.

## Current State

- The test compilation blocker in `MemorySmith.Tests/McpAndSemanticSearchTests.cs` has been repaired locally.
- The repair restored malformed MCP test methods, the admin-client and response helpers, and the telemetry listener helper. It also restored the required controller/MVC imports.
- The test factory now uses an explicit deterministic bootstrap token hash and supplies the token to first-admin setup. This is required because `TestServer` exposes a null remote IP and the bootstrap gate does not treat null as loopback.
- The test factory no longer forces the `LocalDevelopment` environment. That override enabled remote API mode and caused ordinary test clients to receive `503` responses when they did not carry an API key.
- The working tree is intentionally dirty. Preserve unrelated user and generated changes.

## Evidence

| Check | Result |
|---|---|
| `dotnet build MemorySmith.slnx --configuration Debug` | **PASS**; Core, Storage, Bridge, App, Benchmarks, and Tests all built. |
| Focused test filter `FullyQualifiedName~McpAndSemanticSearchTests` | **24 discovered; 1 passed, 23 failed**. |
| Focused failure boundary | TestServer requests reach the app, then most API/MCP calls receive `403 Forbidden` because `MemorySmithRequestGuardMiddleware.IsLoopback(null)` is false. |
| `git diff --check -- MemorySmith.Tests/McpAndSemanticSearchTests.cs` | **PASS**. |
| Full `Scripts/Validate-Repo.ps1` | **Not run to completion**; wait until the focused test-host contract is repaired. |

The failed tests are no longer compile failures. The remaining failure is a test-host/request-boundary contract and should be fixed without weakening production security semantics.

## Immediate Next Action

Repair the TestServer API/MCP access contract in the narrowest way that preserves the production guard:

1. Inspect how existing integration tests establish API access and whether a custom `WebApplicationFactory<Program>` can add `X-Api-Key` to every generated client.
2. Prefer a test-only factory/client configuration or an explicit supported test-host option. Avoid changing `MemorySmithRequestGuardMiddleware.IsLoopback` to treat null as loopback unless the application deliberately defines that behavior and adds security tests for it.
3. Rerun:

   ```powershell
   dotnet test MemorySmith.Tests/MemorySmith.Tests.csproj --configuration Debug --filter 'FullyQualifiedName~McpAndSemanticSearchTests'
   ```

4. Capture the new totals and categorize any remaining failures by behavior rather than repairing broad cascades.

## Next Roadmap Order After Green Focused Tests

1. Run `pwsh ./Scripts/Validate-Repo.ps1` from `D:\@Repos\MemorySmith`.
2. Repair or re-enable browser navigation-freeze coverage under `TSK-0470`.
3. Separate MCP positive and denial-path tests under `TSK-0463`.
4. Make semantic benchmark tests prove real embedding-provider execution versus token fallback under `TSK-0466`.
5. Hold the focused council verification of the 11 High findings, then reconcile task status against current source and acceptance evidence.
6. Address state/write-boundary invariants (`TSK-0453`, `TSK-0456`) only after feedback is trustworthy.
7. Decide storage concurrency policy before atomic persistence changes (`TSK-0471` dependency).
8. Defer broad maintainability extraction until correctness, security, persistence, and validation gates are green.

## Files and Commands

- Source report: [codebase-maturity-health-portability-20260810.md](../audits/codebase-maturity-health-portability-20260810.md)
- Active test file: [McpAndSemanticSearchTests.cs](../../../MemorySmith.Tests/McpAndSemanticSearchTests.cs)
- Validation entrypoint: [Validate-Repo.ps1](../../../Scripts/Validate-Repo.ps1)
- Build: `dotnet build MemorySmith.slnx --configuration Debug`
- Focused test: `dotnet test MemorySmith.Tests/MemorySmith.Tests.csproj --configuration Debug --filter 'FullyQualifiedName~McpAndSemanticSearchTests'`

## Handoff Constraints

- Do not revert unrelated working-tree changes.
- Do not commit or create a branch unless explicitly requested.
- Keep `TreatWarningsAsErrors` behavior intact.
- Use NUnit conventions already present in the repository.
- After each substantive edit, run the narrowest executable validation before expanding scope.

## Confidence and Open Questions

- **95% confidence:** the original test compilation blocker is repaired; the full solution build is green.
- **90% confidence:** the remaining focused failures are caused by TestServer remote-IP/API-guard behavior, based on the observed `403` responses and middleware logs.
- **Open question:** should the test host configure a default API key on all generated clients, or should the application expose a documented test-only local transport mode? Resolve this before changing shared middleware behavior.