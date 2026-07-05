# MemorySmith Code Audit — Delta Report #6 (2026-07-02, continued)

**Scope of this document:** deltas only, on top of reports #1–#5. This pass shifted from large C# service files to the Blazor/Razor UI layer (`MemorySmith.App/Components/**/*.razor`, ~15,000 lines across 28 files), tracing every `MarkupString` (raw-HTML-rendering) sink back to its source to check for XSS. Headline result: **the rendering pipeline itself is unusually well-defended** — multiple prior audits (explicitly cited in code comments as "SEC-XSS-01, Audits #5 and #7") already hardened it. The one new finding this pass surfaced is a **settings-interaction risk**: two independently well-documented, individually-reasonable config toggles compose into a materially more dangerous combination that neither one's description discloses.

---

## Headline delta

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **`Pages:AllowRawHtml` + `Auth:AutoEditorForAuthenticatedUsers` compose into stored-XSS-to-everyone risk that neither setting's description warns about.** `AllowRawHtml` lets any wiki page render raw `<script>`/event-handler HTML; its description says "keep disabled unless you fully trust page authors." `AutoEditorForAuthenticatedUsers` silently grants Edit rights to any authenticated user (Report #3, Finding 3). Enable both — a legitimate combination for an operator who wants both "rich page embeds" and "easy collaborative editing" — and "fully trust page authors" now means "anyone who can complete GitHub OAuth," which (confirmed this pass) has **no allowlist mechanism anywhere in the codebase**. Any such user can write a page containing a raw `<script>` tag that executes for every other viewer, including Admins. | 🔴 New | **85%** |

---

## 1. The `AllowRawHtml` × `AutoEditorForAuthenticatedUsers` × no-OAuth-allowlist chain

**Each link, verified independently:**

**Link 1 — `AllowRawHtml` genuinely allows raw script execution when enabled, and applies uniformly to every page:**
```csharp
// MemorySmithOptions.cs
public bool AllowRawHtml { get; set; }   // defaults to false

// AdminSettingsService.cs
EditableSettingDescriptor.Boolean("MemorySmith:Pages:AllowRawHtml", "Allow raw page HTML", "Pages",
    settings => settings.Pages.AllowRawHtml,
    "Allows trusted markdown pages to render raw HTML. Keep disabled for safer local wiki rendering unless you fully trust page authors and content.");

// PageService.cs
_allowRawHtml = options?.AllowRawHtml ?? false;
...
if (_allowRawHtml && block is HtmlBlock htmlBlock) { ... }   // no per-page or per-author gate
if (_allowRawHtml && current is HtmlInline htmlInline) { ... }
```
There is no per-page opt-in, no author allowlist, and no moderation/approval gate specific to raw-HTML pages — it's a single app-wide switch. (This part is *accurately* described by the setting's own text, in isolation.)

**Link 2 — `AutoEditorForAuthenticatedUsers` grants Edit rights to any authenticated user, confirmed in Report #3:**
```csharp
if (auth.AutoEditorForAuthenticatedUsers) { roles.Add(MemorySmithRoles.Editor); }
```
unconditionally, regardless of the user's actual assigned role — and Editor can create/edit pages (this was already established; see Report #3 §3).

**Link 3 — new this pass — there is no allowlist restricting who can authenticate via GitHub OAuth in the first place:**
```
$ grep -rn "AllowedOrganizations|AllowedUsers|AllowedEmails|AllowlistedUsers" MemorySmithOptions.cs GitHubOAuthCallbackHandler.cs
→ zero results
```
Any GitHub account can complete the OAuth flow and become an authenticated MemorySmith user (getting at minimum `AuthenticatedDefaultRole`, or Editor outright if Link 2's setting is on). Combined with Report #1's Finding C-2 (first authenticated user becomes Admin unconditionally) and Finding C-1 (the leaked OAuth `ClientId`/`ClientSecret` are still live per that report), the actual population of "people who can authenticate" for this specific deployment may already be broader than intended for reasons unrelated to this finding.

**The composed risk:** an operator who (a) turns on `AllowRawHtml` because they specifically trust themselves and perhaps a couple of known collaborators to write embeds, and separately (b) turns on `AutoEditorForAuthenticatedUsers` because they want low-friction collaborative editing without manually assigning roles to every new person, ends up with a system where **anyone who can authenticate — which, per Link 3, is anyone with a GitHub account, unless network-level access is separately restricted — can write a wiki page containing arbitrary JavaScript that executes in the browser of every subsequent viewer, including the Admin.** That's a stored-XSS-to-privilege-escalation chain (Editor writes payload → Admin views the page → payload runs with the Admin's authenticated session, in a Blazor Server app where the session is a live SignalR circuit, not just a static page load).

**Why I'm not rating this higher than 85%:** both underlying mechanisms are 100%-confirmed from source; what I can't verify is (a) how commonly both settings are actually enabled together in real deployments — this may be a rare combination in practice — and (b) whether the operator's actual network topology (e.g., MemorySmith only reachable over a private LAN/VPN, consistent with the `.home.arpa` domain seen in Report #1's leaked secrets) already bounds "anyone who can authenticate" down to a small trusted set regardless of the OAuth allowlist gap. The code-level chain is real and reachable; the real-world exposure depends on deployment choices this audit can't see.

**Recommendation:**
1. Cheapest fix: cross-reference the two settings' descriptions. When `AllowRawHtml` is being enabled in the Admin UI, surface a warning if `AutoEditorForAuthenticatedUsers` is also on (and vice versa) — something like "Raw HTML is enabled and any authenticated user can edit pages; raw HTML from any editor will execute for all viewers." This doesn't require re-architecting either feature, just closing the disclosure gap between them.
2. More thorough fix: scope `AllowRawHtml` to a specific role tier narrower than "Editor" (e.g., require Admin-only page authorship for any page containing raw HTML blocks, checked at save time rather than render time), so enabling raw HTML doesn't automatically extend trust to everyone `AutoEditorForAuthenticatedUsers` happens to cover.
3. Add a `Auth:AllowedGitHubOrganizations` or `Auth:AllowedGitHubUsernames` allowlist option for the OAuth flow — this closes Link 3 independently of the other two and is generally good practice for a self-hosted tool regardless of this specific chain.

**Confidence: 85%** — each link is independently verified at 90%+ confidence from direct source reading; the composite is rated slightly lower because it's a three-setting interaction whose real-world likelihood depends on deployment-specific choices I can't observe from the repo alone.

---

## 2. What this pass checked and ruled out (the UI layer is more hardened than I expected — worth documenting)

Given how much of this audit has been "found a real problem," it's worth being equally clear about where the code held up under scrutiny, especially since XSS is exactly the kind of bug I'd expect to find in a Markdown-rendering wiki+chat app and mostly didn't:

- **`ChatMarkdownRenderer.RenderHtml`** — uses two distinct Markdig pipelines (`SafePipeline`/`TrustedPipeline`), explicitly removes `GenericAttributesExtension` with an inline comment citing a *prior* real finding (`SEC-XSS-01`, Audits #5 and #7) that this extension allowed `onclick`/`onerror` injection via `{key="value"}` markdown syntax. Default calls use the safe, `DisableHtml()`-configured pipeline.
- **`ChatReferenceLinkPolicy`** — a dedicated post-processing pass that strips `on\w+=...` event-handler attributes from any anchor tags surviving the markdown render, again with an explicit comment tying it to the same `SEC-XSS-01` finding. Regex-based attribute stripping is inherently a little fragile as a class of defense, but it's a deliberate, documented, second layer on top of the Markdig-level fix, not the only line of defense.
- **`BuildHighlightedSnippetHtml`** (confirmed in Report #5) — HTML-encodes memory content *before* the Lucene highlighter touches it, specifically to prevent injected markup from surviving into the `MarkupString` render path.
- **`MemoryViewer.razor`'s `RenderMemoryContent`** — always uses the *safe* (`allowRawHtml: false`) pipeline regardless of the `Pages:AllowRawHtml` setting; that setting only affects `PageService`-rendered wiki pages, not memory records. This is a correct, intentional scope boundary — memories don't get the raw-HTML opt-in even when pages do.
- **`SensitiveValue.razor`** (secret-masking UI component) — initially worth checking whether the "hidden" value is actually absent from the client DOM before reveal, or just CSS-hidden (which would leak via view-source). Confirmed the app runs Blazor **Server** (`AddInteractiveServerComponents`/`AddInteractiveServerRenderMode` in `Program.cs`), not WebAssembly — component state including the unmasked `Value` lives server-side, and the raw value is only sent to the browser when `Reveal()` triggers a re-render. No view-source leak.

None of these are new findings — they're confirmation that three-plus rounds of prior audit work on this specific attack surface (XSS via markdown/chat rendering) actually stuck, which is useful to know before spending more of this audit's budget re-checking the same surface.

---

## 3. Coverage note

This pass covered the `MarkupString`/raw-HTML-rendering surface across all 28 Razor files by tracing every sink back to its source, rather than a line-by-line read of the full ~15,000-line UI layer. The two largest files, `Chat.razor` (3,230 lines) and `Admin.razor` (2,326 lines), were read selectively around their rendering and settings logic, not exhaustively — a full line-by-line pass of either (particularly `Admin.razor`, which likely contains the UI-side counterpart to every settings-interaction question raised across Reports #3 and #6) remains outstanding if you want the same depth of coverage the C# service layer has now received across Reports #1–#5.
