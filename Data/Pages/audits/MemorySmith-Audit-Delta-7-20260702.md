# MemorySmith Code Audit — Delta Report #7 (2026-07-02, continued)

**Scope of this document:** deltas only, on top of reports #1–#6. This pass did a full read of `Admin.razor`'s code-behind (~1,650 lines, lines 671–2326) — the Users/OAuth/Models/Configuration/Variables/Audit/History admin console. One significant new finding, plus one thing checked and ruled out worth documenting.

---

## Headline delta

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **Nothing prevents an Admin from disabling every sign-in method at once, permanently locking themselves and everyone else out of the web UI.** `LocalPasswordEnabled` (a JSON config setting) and each OAuth provider's enabled state (a separate SQLite table) are toggled through two completely independent code paths, neither aware of the other, and **neither checks whether at least one authentication method would remain active.** The codebase already has a cross-setting validation mechanism (`TryValidateCrossSettingConstraints`) — it's just never been extended to cover this case. | 🔴 New | **88%** |

---

## 1. No guardrail against total authentication self-lockout

**The two independent toggle paths:**

**Path A — `LocalPasswordEnabled`**, edited like any other setting via the generic Configuration-tab flow:
```csharp
// AdminSettingsService.cs — UpdateAsync, called for every setting save
SetJsonValue(root, descriptor.Key.Split(':'), convertedValue);
if (!TryValidateCrossSettingConstraints(root, out var crossSettingError)) { ... }
```
`TryValidateCrossSettingConstraints` exists and is called on **every** setting update — but its entire body only validates two code-search weight relationships:
```csharp
if (hybridVectorWeight <= 0 && hybridLexicalWeight <= 0) { error = "..."; return false; }
if (minCoverageWeight > maxCoverageWeight) { error = "..."; return false; }
return true;
```
Nothing here inspects `Auth:LocalPasswordEnabled` or cross-references it against provider state.

**Path B — OAuth provider enable/disable**, via `Admin.razor`'s `ToggleProviderAsync`:
```csharp
private bool CanToggleProvider(AuthProviderRecord provider) =>
    !IsSystemProvider(provider) && (provider.IsEnabled || IsProviderConfiguredForUse(provider.ProviderName));
```
The only guard is "not the internal `System` pseudo-provider" and "either already enabled, or configured enough to enable." There's no "is this the last remaining enabled provider" check, and provider state (`Database.ProviderLinks.SetProviderEnabledAsync`) lives in the SQLite database — a **completely separate persistence layer** from the JSON settings file `LocalPasswordEnabled` lives in, so even if someone wanted to write the missing check, it has to reach across two different stores to do it.

**The reachable bad state:** an Admin who (a) turns off `LocalPasswordEnabled` in the Configuration tab (a single toggle, no confirmation dialog beyond the generic save flow) and (b) disables every configured OAuth provider in the OAuth tab (each individually has a confirmation dialog via `Dialogs.ConfirmDestructiveActionAsync`, but nothing warns "this is the last one") ends up with **zero working sign-in methods**, for every user, including themselves.

**Why recovery isn't graceful:** Report #3 documented the app's bootstrap-gating mechanism (`CreateFirstAdminAsync`'s loopback-or-token check) — but that path is explicitly scoped to *"no admin exists yet"* (`!await db.Users.HasAnyAdminAsync(ct)`). In this scenario an Admin account already exists; the bootstrap flow doesn't apply. Recovery requires direct server/file access to manually edit the JSON settings file (`MemorySmith:Auth:LocalPasswordEnabled` back to `true`) or the SQLite database directly — not a UI-reachable fix, and a materially more error-prone recovery path than the one this codebase already built for the "no admin" case.

**Why I rate this 88% and not higher:** the mechanism is fully verified from source on both sides (no code path prevents the combination, confirmed by reading both `TryValidateCrossSettingConstraints`'s complete body and `ToggleProviderAsync`/`CanToggleProvider` in full). The discount is because I haven't traced whether some *other* layer I haven't read yet (e.g., a startup health-check, or `MemorySmithLocalDevelopmentPostConfigure.cs`-style safety net that force-re-enables local auth if nothing else is configured) exists and would catch this at app-restart time even if the UI allows setting it up — I found no evidence of such a safety net in the files I've read so far across this whole audit, but I also haven't read every startup/health-check code path specifically looking for one.

**Recommendation:**
1. Minimal fix: in `ToggleProviderAsync`, before disabling a provider, check whether `LocalPasswordEnabled` is true OR any *other* provider besides this one is currently enabled; block the toggle with a clear error if not. Symmetrically, in `AdminSettingsService.UpdateAsync` (or a small addition to `TryValidateCrossSettingConstraints` specifically for the `Auth:LocalPasswordEnabled` key), check whether at least one provider is currently enabled before allowing `LocalPasswordEnabled` to be set to `false`. Both checks need read access to the other store (`Database.ProviderLinks` from the settings-update path, or `IOptionsMonitor<MemorySmithOptions>` from the provider-toggle path) — a small, contained addition given both are already injected into their respective classes' constructors or available via DI.
2. Lower-effort alternative if the full cross-store check feels like too much for now: add a prominent, specific warning (not just the generic destructive-action confirmation text) on both toggle paths when the action would leave zero enabled methods, even if it doesn't hard-block it — "This will disable the last remaining sign-in method for this instance" is a materially different message than "Disable provider?".

---

## 2. Checked and ruled out: settings-export payload doesn't leak stored secrets

`Admin.razor`'s Configuration tab includes a JSON export/import ("transfer payload") feature (`SerializeSettingValues`/`CopySettingsTransferPayloadAsync`) that copies every visible setting's current value to the clipboard — worth checking given how many sensitive settings this app has (API keys, OAuth secrets, bootstrap token hash). Traced the actual value source: `EditableSettingDescriptor`'s sensitive settings are explicitly rendered write-only (`AdminSettingsService.cs:470`: `var editValue = IsSensitive ? string.Empty : text;`) — the UI never populates `PendingValue` with an existing stored secret for sensitive fields, only with whatever the admin has freshly typed into the field during the current session (if anything). So the export payload can't leak an already-configured secret's value; at most it could echo back a new secret the admin themselves just typed in but hadn't saved yet, which is their own input, not a disclosure. No issue found.

---

## 3. Coverage note

This pass completed a full read of `Admin.razor`'s code-behind (1,650 of its 2,326 lines — the remainder is markup already scanned in Report #6 for `MarkupString` sinks). Combined with Report #6, this closes out both of the "next target" files flagged at the end of the previous report. Remaining unexamined at full depth from the original ~52k-LOC C# tree: `TaskDomainService.cs`, the bulk of `CodeSearchService.cs` (~2,000 of its 3,115 lines), `Chat.razor`'s non-rendering logic (the bulk of its 3,230 lines — this pass and Report #6 only covered its `MarkupString` rendering path), and the training/Python harness scripts.
