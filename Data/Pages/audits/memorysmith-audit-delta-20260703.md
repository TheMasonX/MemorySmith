# MemorySmith Audit — Delta Report (Round 2)

**Same repo/commit as the first report:** `TheMasonX/MemorySmith` @ `d250ffe8` (master, 2026-06-27).
This is **delta-only** — new findings and corrections from continuing the deep dive into the areas the first report explicitly flagged as sampled-not-read: `CodeSearchService.cs` (3,115 lines), `MaintenanceAgentServices.cs` (2,187 lines), `AgentSessions/*`, the GPU slot scheduler, `MemorySmith.Bridge`, and the PowerShell deploy scripts. Nothing from the first report is repeated here.

---

## New Finding D1 — HTTPS certificate password: typed as `SecureString`, then decrypted and handed to the process as a plaintext command-line argument

**Where:** `Scripts/Redeploy-MemorySmithService.ps1`

**Evidence:**
- The script accepts the cert password two ways: `[SecureString]$HttpsCertificatePassword` (parameter block, ~line 27) or `$HttpsCertificatePasswordFile` (a path to a file containing it, ~line 28).
- Both paths converge on a single plaintext variable, `$resolvedHttpsCertificatePassword`:
  - The file path just reads the file as plaintext (`(Get-Content -Path ... -Raw).Trim()`, ~line 544).
  - The `SecureString` path immediately decrypts it: `$credential.GetNetworkCredential().Password` (~line 551) — this is the one line that defeats the entire point of accepting it as a `SecureString` in the first place.
- Either way, the plaintext value is then appended to the child process's argument list: `$additionalRuntimeArgs += @('--Kestrel:Certificates:Default:Password', $resolvedHttpsCertificatePassword)` (~line 561), which gets passed to the `dotnet`/service process as a literal command-line argument.

**Impact:** Process command-line arguments are visible for the life of the process to any other local process/user with process-enumeration rights (`Get-CimInstance Win32_Process | select CommandLine`, Task Manager's command-line column, Process Explorer, `/proc/<pid>/cmdline` if ever ported to Linux) and are commonly captured by process-creation auditing (Windows Event ID 4688 with command-line logging enabled). Typing the parameter as `SecureString` signals the author knew this value needed protecting in memory — but the very next thing the script does is defeat that protection by handing it to the OS as an argv string, which was never a secure channel to begin with.

**Recommendation:** Pass the certificate password via an environment variable scoped to the child process (`$env:...` set only in the process's own environment block, not the parent shell) or via .NET's user-secrets/`dotnet run --launch-profile` style config, not argv. If it must go through config, prefer `ASPNETCORE_Kestrel__Certificates__Default__Password` as an environment variable set on the `Start-Process`/service-config call rather than `--Kestrel:...` as an argument — environment blocks of a process are still readable by admins but are not shown in default process listings the way argv is.

**Confidence:** 85%. I read the script's logic directly and confirmed the decrypt-then-argv-append flow; I did not execute the deploy script end-to-end to observe the resulting process's actual argv at runtime, so there's a small chance an intermediate step (e.g. the service manager) further wraps or protects this argument in a way not visible from the script alone.

---

## New Finding D2 — `OllamaGpuSlotScheduler` takes `IOptionsMonitor` but only ever reads the value once (singleton, constructor-only read) — live config reload is silently a no-op

**Where:** `MemorySmith.App/Services/OllamaGpuSlotScheduler.cs`, registered in `MemorySmith.App/Hosting/MemorySmithChatSetup.cs:39`

**Evidence:**
```csharp
public OllamaGpuSlotScheduler(IOptionsMonitor<MemorySmithOptions> options)
{
    var maxParallel = Math.Max(1, options.CurrentValue.Chat.MaxParallelOllamaRequests);
    _semaphore = new SemaphoreSlim(maxParallel, maxParallel);
}
```
- Registered as `AddSingleton<IGpuSlotScheduler, OllamaGpuSlotScheduler>()` — constructed exactly once for the app's lifetime.
- `SemaphoreSlim`'s max count is fixed at construction and cannot be changed afterward.
- The constructor takes `IOptionsMonitor<T>` specifically (not `IOptions<T>`), which is the type used elsewhere in this codebase precisely to support live config reload without a restart — but here it's only ever accessed via `.CurrentValue` inside the constructor, so the "monitor" capability is never exercised.

**Impact:** Changing `Chat:MaxParallelOllamaRequests` in the running config (e.g. via the admin settings UI, if that setting is exposed there, or a config-file hot-reload) has **no effect** until the process restarts — the semaphore silently keeps serializing (or parallelizing) requests at whatever value was in effect at startup. This is exactly the kind of "looks live-reloadable, isn't" gap that's easy to miss because the type signature (`IOptionsMonitor`) actively suggests the opposite of the actual behavior.

**Recommendation:** Either (a) switch to `IOptions<MemorySmithOptions>` to make the constructor-only-read behavior explicit and honest, or (b) if live reload is actually wanted here (plausible — this is exactly the kind of tuning knob an operator would want to adjust without a restart on a memory-constrained GPU box), replace the fixed `SemaphoreSlim` with a scheme that can grow/shrink capacity (e.g. re-create the semaphore on `options.OnChange` with a drain/handoff, or use a capacity-checking custom gate instead of `SemaphoreSlim`). (a) is a 2-minute fix; (b) is the "actually do what the type signature promises" fix.

**Confidence:** 90% on the mechanism (directly read the constructor and DI registration); 60% on whether operators actually expect this to be live-reloadable in practice (I didn't find a settings-UI control for this specific value, so the practical impact may currently be limited to editing `appsettings.json` and restarting anyway — in which case this is a latent trap for whenever that UI control gets added, not an active user-facing bug today).

---

## New Finding D3 — Code-search "warm reuse" trusts file size + mtime instead of content hash, with no reconciliation safety net

**Where:** `MemorySmith.App/Services/CodeSearchService.cs`, `CanWarmReuseByMetadata` (~line 1487) vs. the stricter `CanReuseDocument` (~line 1506) a few lines below it in the same file.

**Evidence:**
```csharp
private bool CanWarmReuseByMetadata(ExistingDocumentState existingDocument, string configurationHash, bool embeddingsAvailable, FileInfo fileInfo)
{
    ...
    if (sourceLengthBytes != fileInfo.Length ||
        sourceLastWriteUtc.Ticks != fileInfo.LastWriteTimeUtc.Ticks ||
        !string.Equals(existingDocument.ConfigurationHash, configurationHash, StringComparison.Ordinal))
    {
        return false;
    }
    return !embeddingsAvailable || existingDocument.HasEmbeddings;
}
```
This is gated behind `_options.WarmMetadataReuseEnabled` and exists as a **faster** alternative to `CanReuseDocument`, which does the correct thing and compares a real content hash (`sourceHash`).

**Impact:** Any workflow that preserves a file's byte-length and exact mtime while changing its content will cause the warm-reuse path to skip re-indexing/re-embedding, silently leaving stale chunks in the code-search index. This is a real (if narrow) scenario: `git checkout` of a different commit followed by a build step that touches files without changing length, tarball extraction that preserves original timestamps, rsync with `--times` against a byte-identical-length replacement file, or plain clock skew on a VM. This is a known, industry-standard trade-off (mtime+size caching is what `make`/`ninja`/many build caches do), so it's not inherently wrong — but I found no periodic full-hash reconciliation pass, no "verify N% of warm-reused docs against content hash" sampling check, and no logged warning when warm reuse and the real hash would have disagreed. Right now, if this ever goes wrong, nothing would tell you.

**Recommendation:** Not "stop using the fast path" — but add a cheap safety net: occasionally (e.g. 1-in-N documents, or once per full rebuild) verify a warm-reused document's content hash against what's stored and log a warning (and force a real re-index of that document) on mismatch. This gives you the speed of the fast path with a tripwire for the failure mode, and would have caught this exact class of staleness bug the day it happened rather than leaving it silent indefinitely.

**Confidence:** 80%. I confirmed the code does exactly what's described; I did not find or rule out an existing periodic-reconciliation job elsewhere in the codebase that might already mitigate this (searched only within `CodeSearchService.cs` itself for this pattern), so there's a real chance a scheduled `EnsureIndexedAsync(rebuildIfStale: true, forceRebuild: true)` job elsewhere already provides a coarser version of this safety net on a schedule — worth a quick check of `MemoryMaintenanceTasks.cs`/scheduled jobs before treating this as urgent.

---

## New Finding D4 — Maintenance-agent prohibited-path check is a filename/extension denylist, not a source-code-aware one (currently safe by default, no code-level ceiling against misconfiguration)

**Where:** `MemorySmith.App/Services/MaintenanceAgentServices.cs`, `MaintenanceWritePermissionService.IsProhibitedPath` (~line 515)

**Evidence:**
- `IsProhibitedPath` blocks writes to anything under a `Schemas` path segment, anything named `appsettings*`, `maintenance_agent.json/.yaml`, and anything with extension `.csproj`, `.sln`, `.slnx`, `.props`, `.targets`, `.yaml`, `.yml`.
- It does **not** block `.cs` files by any means — arbitrary source files (including security-critical ones like `SecurityServices.cs` or `GitHubOAuthCallbackHandler.cs`) are not denylisted at all.
- By default, `MaintenanceAgentConfigService.Normalize` sets `config.Write` to `Data/Memories/Working` and `Data/Pages` only (confirmed by reading the normalization logic) — so under default configuration, the denylist gap is moot because the allowlist never includes source directories in the first place.
- I also confirmed `MaintenanceProposalWorkflow` only calls the store's `ApplyAsync` from the explicit human `Approve` path (~line 720) — proposals are not auto-applied, so there's a human review gate as a second backstop regardless of path configuration.

**Impact (calibrated down from what it might look like in isolation):** under current defaults and the human-approval gate, this is **not** an active vulnerability. It's a defense-in-depth gap: if an operator ever widens `MaintenanceAgent:Write` (or the equivalent chat-agent-write-roots setting) to include a source directory — for example, to let the maintenance agent "clean up code comments" or similar — the only things stopping it from proposing edits to `SecurityServices.cs` would be (a) this denylist, which doesn't cover `.cs`, and (b) a human clicking "approve" on the diff. That's a thinner margin than the current wiki-only default provides, and it's not obvious from reading `IsProhibitedPath` alone that `.cs` files are uncovered — the list reads like a general "don't touch build/config" list, which could give a false sense that source files are covered too.

**Recommendation:** No urgent action given the safe default + human-approval gate, but worth either (a) adding an explicit code comment on `IsProhibitedPath` noting that `.cs`/source files are intentionally excluded from the denylist because the allowlist is expected to never include source roots, so a future editor doesn't assume the list is exhaustive, or (b) adding `.cs` (and `.razor`) to the denylist outright as a belt-and-suspenders measure, since the maintenance agent's stated purpose (per its config: memories/pages maintenance) has no legitimate reason to ever touch source code, making the extra restriction free.

**Confidence:** 88% on the mechanism (read both the denylist and the default allowlist directly, and confirmed the human-approval gate); 50% on whether this is worth prioritizing at all, since it requires a specific future misconfiguration to matter — flagging as a documentation/hardening suggestion, not a bug to fix this sprint.

---

## Corrections to Round 1

None. Everything re-touched in this pass (the human-approval gate, the default write-roots, the `AgentSession` per-session semaphore/lock pattern referenced by a comment in `AgentSessionService.cs`) was **investigated as a candidate finding and ruled out** after tracing it — no correction needed to Round 1's content, these are additive.

---

## Still-Unread / Lower-Confidence Areas (unchanged honesty note)

Even after this second pass, the following remain sampled/grep-swept only, not read end-to-end: `ChatToolCatalog.cs` (1,603 lines) beyond the specific tool handlers checked, `PageService.cs` (908 lines), `MemoryGovernanceServices.cs` (635 lines), `AdminSettingsService.cs` (624 lines), `Chat.razor`/`Admin.razor`/`Tasks.razor` beyond the targeted greps run here, and all `MemorySmith.Training` Python code. If the goal is a genuinely exhaustive line-by-line pass, these are the next targets, roughly in that priority order (governance/admin-settings ahead of the Razor UI layer, since misconfigurable server-side settings are higher-leverage than UI bugs).
