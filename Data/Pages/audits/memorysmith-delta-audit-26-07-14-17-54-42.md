# MemorySmith delta audit — duplication, package surface, and AI-smell pass

## Executive summary

This pass adds three new themes. First, the app project’s dependency surface is materially larger than the repository’s own dependency-hygiene task scope suggests, and the About page is hand-maintaining a package list that is already drifting from the actual project file. Second, the GitHub Copilot tool-attachment path is a brittle reflection shim that behaves like a speculative middle man. Third, the chat controller still carries explicit provider aliasing / routing logic, which should be folded into the provider contract rather than left as another caller-side branch point. fileciteturn117file0turn126file0turn124file0turn120file0turn113file0

Confidence in the new deltas: **87%**. I did not run an external CPD engine in this environment, so the duplication findings below are based on direct cross-file inspection and semantic comparison, which is the only reliable way to catch Type-4 clones anyway.

## New findings

| ID | Severity | Confidence | Finding | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-012 | High | 93% | The app project carries a very wide package surface for a greenfield app: 18 unconditional package references plus 3 conditional ONNX variants, spanning chat, indexing, Markdown, tooling, telemetry, serialization, and UI. | This increases supply-chain exposure and makes dependency governance harder than it needs to be. Several packages appear to belong to feature subdomains or tooling rather than the always-on runtime. | fileciteturn117file0 |
| D-013 | Medium | 91% | `About.razor` manually duplicates package inventory and license metadata instead of deriving it from the project graph. The list already disagrees with the project file on package versions. | The About page is now a second source of truth for supply-chain reporting, and it is stale. That creates a false sense of dependency hygiene and makes audits harder. | fileciteturn126file0turn117file0 |
| D-014 | Medium | 88% | `ChatController.ResolveProvider()`, `DefaultModelForProvider()`, and `EndpointForProvider()` hard-code provider aliasing and defaults in the caller layer. | This is the same “provider string controls behavior” smell that TSK-0283 is trying to remove, but it is still present in the controller and will keep growing if left there. | fileciteturn120file0turn113file0 |
| D-015 | Medium | 84% | `TryAttachGitHubNativeTools()` uses reflection to discover and populate an SDK property, then silently swallows failure paths when the property shape does not match. | That is a speculative compatibility shim and a middle man. It makes the behavior dependent on undocumented SDK surface details and hides breakage behind debug logging. | fileciteturn124file0 |
| D-016 | Low | 82% | The About-page package inventory is a hand-curated presentation layer artifact that is no longer a reliable explanation of what the app actually depends on. | The page is well intentioned, but it is now UI-driven documentation debt. It should be generated from the build graph or removed in favor of a source-of-truth dependency report. | fileciteturn126file0turn117file0 |

## Detailed findings

### D-012 — Package surface bloat
`MemorySmith.App.csproj` currently references a broad cross-section of packages: Copilot SDK, three Lucene packages, Markdig, two MessagePack stacks, Roslyn, dependency model tooling, Newtonsoft.Json, four OpenTelemetry packages, ASP.NET OpenAPI, Windows Services, MudBlazor, Serilog packages, Swashbuckle, StreamJsonRpc, System.Numerics.Tensors, and TreeSitter, plus conditional ONNX variants. That is a lot of supply-chain surface for a single app project. fileciteturn117file0

**Fix:** split runtime, tooling, and optional AI/search dependencies more aggressively. At minimum, isolate packages that are only needed for training, benchmarking, or development diagnostics into separate projects or feature-specific adapters. Re-check whether all always-on references are truly required by the production app.  
**Task fit:** this is best treated as a follow-on to `TSK-0046` rather than a duplicate of it. `TSK-0046` was about pruning stale references; this delta is broader, about reducing the live runtime dependency footprint itself. fileciteturn119file0turn117file0

### D-013 — Manual package inventory drift
`About.razor` hard-codes a package/license catalog and presents it as a dependency explanation page. That catalog already diverges from the actual csproj: for example, the About page lists older package versions than the project file for `GitHub.Copilot.SDK`, `Markdig`, `MudBlazor`, `Microsoft.AspNetCore.OpenApi`, and others. fileciteturn126file0turn117file0

**Fix:** generate the dependency view from an artifact that is produced during build or CI, or retire the page’s “package inventory” role entirely and link to a generated dependency report. Do not keep two manually edited dependency lists in sync.  
**Confidence:** 93% because the version drift is directly visible in the source.

### D-014 — Provider aliasing still lives in the controller
`ChatController` still resolves providers by name, including special-case aliases for `"GitHub" → "Copilot"` and `"OpenAI" → "DeepSeek"/"OpenRouter"`, then separately resolves default models and endpoints based on provider strings. That is exactly the kind of caller-side branch logic that keeps `IChatProvider` dishonest. fileciteturn120file0turn113file0

**Fix:** move aliasing/defaults into provider metadata or a provider registry abstraction, and keep the controller as a thin caller of that registry. This should be rolled into `TSK-0283` rather than made into a separate task, because it is the same seam-honesty problem showing up at a second call site.  
**Confidence:** 88%

### D-015 — Reflection-based GitHub native-tool shim
`TryAttachGitHubNativeTools()` probes `MessageOptions.Tools` with reflection, then has three type-specific assignment branches and a catch-all that logs only at debug level. That is a compatibility shim, but it is also a middle man and a speculative generality hook: the code exists to paper over SDK surface uncertainty rather than to encode a stable contract. fileciteturn124file0

**Fix:** prefer a compile-time adapter over reflection. If the SDK genuinely varies across versions, isolate that version split in one adapter layer and let the rest of chat plumbing work against a stable tool-registration interface. If the feature is optional, fail with an explicit capability message rather than silent no-op behavior.  
**Task fit:** this is related to `TSK-0283`, but it is not the same problem. `TSK-0283` targets provider contract honesty; this delta targets the GitHub SDK integration shim itself. fileciteturn113file0turn124file0

### D-016 — About page is explanation debt
The About page is trying to explain the package surface, but because the data is hand-maintained, it now explains the wrong thing. The page has become a documentation artifact with its own update burden, which is risky in a repo that is explicitly trying to minimize supply-chain risk and technical debt. fileciteturn126file0turn117file0

**Fix:** if the page stays, render it from a generated dependency manifest. Otherwise, replace it with a much smaller “third-party notices” view that is fed from build output.  
**Confidence:** 82%

## Task mapping and backlog fit

`TSK-0046` is the right ancestor for dependency pruning, but it is archived and narrower than this delta. The new work is about reducing the active package surface and eliminating the stale, manual dependency inventory in the UI, not just removing one or two unused references. fileciteturn119file0turn126file0turn117file0

`TSK-0283` remains the right bucket for provider-contract honesty, but it should be extended to include the controller-side alias/default routing. Without that extension, the seam will stay honest in one layer and dishonest in another. fileciteturn113file0turn120file0

No existing task clearly covers the GitHub native-tool reflection shim; that may deserve its own follow-on item if the team wants to keep SDK compatibility behavior explicit rather than hidden behind debug-only fallback. fileciteturn124file0

## Implementation guidance

The best order is:
1. Replace the manual About-page package inventory with generated dependency data.
2. Reassess the production necessity of the broad package set in `MemorySmith.App.csproj`.
3. Move provider aliasing/defaults into a registry or provider contract.
4. Replace the reflection-based GitHub tool shim with a stable adapter boundary.

That order removes the most visible source-of-truth drift first, then pays down the broader supply-chain surface, then hardens the AI provider seam. fileciteturn126file0turn117file0turn120file0turn124file0

## Assumptions and open questions

- Assumption: the About page is intended to be a user-facing dependency explanation, not merely a developer curiosity page. If it is only for internal use, the drift risk is smaller but still real. fileciteturn126file0
- Assumption: several of the app project dependencies are feature-driven and could be isolated without changing product behavior. That should be verified before pruning. fileciteturn117file0
- Open question: should provider aliasing live in the controller, in each provider adapter, or in a registry service shared by the app? The current code spreads the decision across the call chain. fileciteturn120file0turn113file0
- Open question: should the GitHub tool shim fail closed when the SDK shape is unexpected, or remain a best-effort no-op? The current code chooses best-effort and hides the miss behind debug logging. fileciteturn124file0

## Confidence notes

- D-012: 93% — the package count and breadth are explicit in the csproj.
- D-013: 91% — the About page inventory is plainly stale relative to the csproj.
- D-014: 88% — caller-side provider aliasing is directly visible.
- D-015: 84% — the reflection shim is explicit, but its necessity depends on SDK behavior.
- D-016: 82% — the About page is clearly drift-prone, though the desired long-term role may vary. fileciteturn117file0turn126file0turn120file0turn124file0turn113file0
