# MemorySmith — Audit #9

## Architecture Deepening Review: Seams, Shallow Modules, and Locality

**Generated:** 2026-06-11
**Subject:** `master` @ `6368bddf` (CI fully green, 507 tests / 0 failed / 10 skipped)
**Focus:** Find deepening opportunities — refactors that turn shallow modules into deep ones — for testability and AI-navigability. Informed by the project wiki (domain glossary) and council decisions (ADR-equivalents). Distinct lens from Audit #8 (duplication/consistency): this audit grades modules by interface depth, seam honesty, and the deletion test.
**Method:** Three survey passes (chat pipeline; storage/retrieval; host composition + UI), each grading modules with a fixed vocabulary, followed by spot-verification of every load-bearing claim against source.

---

## 0. Vocabulary (used exactly, throughout)

- **Module** — anything with an interface and an implementation (function, class, package, slice).
- **Interface** — everything a caller must know to use the module: types, invariants, error modes, ordering, config. Not just the type signature.
- **Deep** — a lot of behaviour behind a small interface (high leverage). **Shallow** — interface nearly as complex as the implementation.
- **Seam** — where an interface lives; a place behaviour can be altered without editing in place. **Adapter** — a concrete thing satisfying an interface at a seam. One adapter = hypothetical seam; **two adapters = real seam**.
- **Locality** — change, bugs, and knowledge concentrated in one place. **Leverage** — what callers get from depth.
- **Deletion test** — imagine deleting the module: if complexity vanishes, it was a pass-through; if complexity reappears across N callers, it was earning its keep.

## 0.1 Decisions respected (not re-litigated)

- **TSK-0271**: `memorysmith_semantic_search` / `memorysmith_unified_search` were deliberately removed; hybrid is the routing default. No candidate reintroduces them.
- **Council, agent-session timeouts**: TimeoutPhase shipped additive; the MinInferenceSeconds fail-fast floor was deferred pending telemetry.
- **DI convention**: explicit factories over bare `AddSingleton<T>()` (the empty-ChatToolCatalog incident). All proposals keep it.

## 0.2 Relationship to the existing backlog

Audits #1–8 already filed decomposition tasks. This audit **updates** five of them with sharper, evidence-backed findings (TSK-0042, TSK-0043, TSK-0157, TSK-0189, TSK-0191) and files **five new tasks** for friction nothing owned (TSK-0282 – TSK-0286). Already tracked elsewhere and excluded here: TSK-0279 (BuildTools split), TSK-0280 (shared test factory), TSK-0281 (CI data lint).

---

## 1. The composition root is a single 862-line script — **Strong** → **TSK-0282** (new)

**Files:** `MemorySmith.App/Program.cs`; `TagGovernanceTests.ExternalAuthCallbacks_RecordDurableSuccessAndFailureEvidence`

**Problem.** Top-level statements with no seams: ~30 service registrations, the OpenTelemetry block, the exception-handler and security-header lambdas, request logging, two page-asset endpoint handlers, and a 185-line GitHub OAuth callback lambda (lines 133–317: user creation, provider links, role assignment, durable audit evidence) all live inline. This is shallow-by-absence: high-complexity implementation with no interface to reach any piece.

**Incident evidence.** The June 4 reconstruction silently dropped ~16 blocks (security headers, exception handler, request logging, OTel, the settings-override load — admin edits silently ignored) and ran that way for a week. The only thing pinning the OAuth callback today is a source-inspection test that greps `Program.cs` for symbol names as text — it would pass with the code commented out.

**Solution.** Carve into named registration modules (`AddMemorySmithStorage` / `Security` / `Chat` / `Maintenance` / `Telemetry`) plus a `UseMemorySmithPipeline` module; extract the OAuth callback into an external-auth callback module with a real, fake-ticket-testable interface; move the page-asset endpoints into their own module.

**Benefits.** Locality: each pipeline concern changes in one named place — the silently-dropped-block failure class disappears (dropping a module is dropping one visible call line). Leverage: the dishonest text-grep test becomes a real unit test on the callback module; factory tests can pin each module independently. Establishes the pattern candidates 2–3 follow.

## 2. The chat turn engine is trapped inside the Chat page — **Strong** → **TSK-0189** (updated)

**Files:** `MemorySmith.App/Components/Pages/Chat.razor` (3,230 lines); 4 text-grep tests in `PagesAndChatTests`

**Problem.** Six concerns share one `@code` block: the streaming state machine, debounced localStorage session persistence (~20 `SaveSessionsAsync` call sites), a 9-method proposal approve/reject/respond state machine, attachments, model-profile selection, and the question-card protocol. The page has no interface at all; the proposal workflow — the governance heart of agent writes — is effectively untestable in-process. Four tests pin behaviour by reading the file as text (draft debounce, render throttle, bounded render cache, draft-persist scope): text-grep tests are what a codebase grows where a seam should be.

**Solution.** Extract a chat turn engine module (state machine over turns: start, consume stream updates, resolve proposal outcomes) and a chat session store seam with two adapters — browser localStorage and in-memory for tests (instantly a real seam). The page keeps markup, bindings, and JS interop.

**Benefits.** Proposal-state and streaming-state bugs concentrate in the engine; all four text-grep tests become real unit tests; the approve-all batch semantics (TSK-0018 history) finally get direct coverage.

## 3. MemoryChatAgent runs two tool loops — **Strong** → **TSK-0042** (step 1 LANDED 2026-06-12)

> **Status:** the loop is now unified in `MemoryChatAgent.ToolLoop.cs` (`RunToolLoopAsync`, single driver; ChatServices.cs is 3,744 lines and StreamAsync is a thin event consumer — the line citations below describe the pre-change state). Council-reviewed: `council/audit9-implementation-track-council-20260612`. Step 2 (file split) remains, gated on the acquisition-arms check recorded in TSK-0042.

**Files:** `MemorySmith.App/Services/ChatServices.cs` (3,996 lines; agent at 1697–3995)

**Problem.** `IChatAgent` is a 3-method interface fronting ~2,300 lines — and the tool loop exists twice: `SendAsync` delegates to `CompleteWithToolCallsAsync` (line 2081) while `StreamAsync` carries its own inline loop (~1887–2080). Model resolution is duplicated (lines 1838 and 3273). A tool-execution bug must be found and fixed in two places — a textbook locality failure. The file also glues four modules together (protocol records, both providers, the agent).

**Solution.** Extract a single tool-call loop module both entry points drive (streaming emits events; non-streaming collects them); then split the file along existing type boundaries.

**Benefits.** One place for loop bugs (iteration caps, result truncation, error surfacing); the loop becomes unit-testable with the existing `FakeChatProvider` pattern; the agent-session path inherits fixes for free. Deletion test: deleting either loop today forces the other to absorb its callers — proof the halves want to be one deep module.

## 4. The chat provider seam is real but dishonest — **Worth exploring** → **TSK-0283** (new)

**Files:** `ChatServices.cs` — seam at 250–273; leaks at 131, 149, 2523–2526, 2561–2564

**Problem.** The seam is real (two production adapters plus test fakes swap freely), but callers branch on the provider's name string: `DefaultModelForProvider` ("GitHub" → one config, else another), `ResolveUsageMetadata` ("GitHub" → a separate model-metadata list), `ChatErrorMessages.Format` — and the `ProviderMatches` helper is copy-pasted twice (lines 149 and 2526). The interface does not carry the full contract; provider identity leaks through the seam.

**Solution.** Push the leaked knowledge into the provider's `Capabilities` (default model, usage-metadata resolution, error-message hints); delete the name branches and the duplicate helper.

**Benefits.** A third adapter plugs in with zero caller edits (today: three branch sites). Everything a provider must declare lives on the adapter. The council's sub-agent provider allowlist is unaffected (session governance, not seam plumbing).

## 5. Code search has no seam at all — **Worth exploring** → **TSK-0284** (new)

**Files:** `MemorySmith.App/Services/CodeSearchService.cs` (3,115 lines); `McpController`; `ChatToolCatalog`; `ChatToolExecutionContext.CodeSearch`

**Problem.** The MCP endpoint, the tool catalog, and the ambient tool context all depend on the concrete class — no interface exists. Inside, ten responsibilities co-locate: hybrid search (258–424), index builds (503–752), five chunking strategies (Roslyn / TreeSitter / Markdown / heuristic / fixed-window, 812–1368), embedding batches, its own SQLite schema, shard merge, and 1,100+ lines of build-log/telemetry. Callers cannot be tested without real code search against temp directories.

**Solution.** First, introduce the code search seam at the four entry points callers use (search, status, ensure-indexed, merge-shard). Second, promote the five chunking strategies to adapters behind a chunking strategy seam — five adapters on day one, the realest seam in this audit.

**Benefits.** Catalog/MCP tests substitute a fake; chunk-shape knowledge concentrates per language family. Incident precedent: the benchmark project broke CI for weeks because it constructed the concrete class and the constructor widened underneath it.

## 6. The retrieval engine is buried in the memory CRUD service — **Worth exploring** → **TSK-0191** (updated)

**Files:** `MemorySmith.App/Services/MemoryApplicationService.cs` (1,461 lines); `SemanticEmbeddingSearchService.cs`

**Problem.** One 18-method module spans six domains: memory-record CRUD, lexical ranking (with two tokenizer pipelines side by side), RRF fusion, context-pack graph traversal, alias expansion, telemetry. The hand-curated `SearchAliases` dictionary — load-bearing for recall — is invisible config inside a service class. The token-scoring fallback for the semantic signal is an inline `if/else`, not a second adapter on the embedding-provider seam — exactly why the fallback is silent (the semantic-search-gap wiki record and TSK-0200 both complain). `GetStatsAsync` / `GetTelemetryAsync` fail the deletion test outright (pure pass-throughs).

**Solution.** Extract a memory retrieval engine module (lexical + semantic + RRF + aliases behind a small search interface); promote the token fallback to a fallback adapter on the existing embedding-provider seam, giving it a place to report its own provider metadata; delete the two pass-throughs.

**Benefits.** Ranking changes stop touching the CRUD/governance surface; aliases become a visible, separately testable module; TSK-0200 gets its seam for free. **Guardrail:** internal only — must not reintroduce standalone semantic/unified search tools (TSK-0271).

## 7. Nine ceremonial store interfaces, one adapter — **Worth exploring** → **TSK-0157** (updated)

**Files:** `MemorySmith.Storage/SqliteMemorySmithDatabase.cs` (1,404 lines); `MetadataStoreInterfaces.cs`

**Problem.** One class implements nine sub-interfaces (users, roles, provider links, login history, audit, settings, version history, semantic-index metadata, API tokens). Every interface is anaemic CRUD (2–7 methods, no invariants, no documented error modes) with exactly one adapter — hypothetical seams. Even the test fixture instantiates the concrete class, so the interfaces buy zero test isolation. Nine domains in one file create navigation cost and merge pressure.

**Solution.** (a) Navigability first: split the implementation into partial files per store, keep the interfaces — cheap, zero risk. (b) The deeper cut: collapse sub-interfaces nobody substitutes into the database facade, keeping only those where a second adapter is plausible. The deletion test sides with (b) for most of the nine; (a) is the safe first move and doesn't foreclose (b).

**Benefits.** Each store's SQL, mapping, and schema live in one findable file — and the codebase stops advertising seams it doesn't have, which matters for AI navigation (nine interfaces imply substitutability that doesn't exist).

## 8. File gravity: unrelated modules glued by filename — **Worth exploring** → **TSK-0285** (new, SecurityServices) + **TSK-0043** (updated, MaintenanceAgentServices)

**Files:** `SecurityServices.cs` (1,258); `MaintenanceAgentServices.cs` (2,187)

**Problem.** Pure navigation friction — the modules inside are mostly fine; their co-location isn't. `SecurityServices.cs` holds four unrelated concerns (authorization policies, local auth, the hash-chained audit log, version history) plus `AuditedPageService` — the page-service decorator — hiding at line 1190 where nobody would look. `MaintenanceAgentServices.cs` holds ten types, from a 296-line topic-map/Mermaid generator to a 58-line diff formatter to the scheduler.

**Solution.** Mechanical file moves along existing type boundaries — no interface or behaviour changes. The maintenance proposal cluster (store + workflow) is a genuinely deep module and moves as one unit.

**Benefits.** Locality of knowledge only — the cheapest depth there is, and the dimension AI navigation feels most: grep-for-filename currently lies about where modules live.

## 9. Every tool depends on a 15-field ambient context — **Speculative** → **TSK-0286** (new)

**Files:** `ChatToolCatalog.cs` lines 33–62; construction sites in `MemoryChatAgent`, `McpController`, `AgentSessionService`

**Problem.** `ChatToolExecutionContext` is the implicit interface of all 22 catalog tools: 15 fields spanning data access (memories, pages, tasks, code search — the last as a concrete class), caller identity, write governance, and Phase-3 delegation fields. A tool needing two fields receives fifteen; widening it for one tool touches every construction site.

**Solution.** Cluster the fields into cohesive sub-contexts (data access · caller identity · write governance · delegation). Sequencing matters: TSK-0284's seam removes the worst field, and Phase 3 (TSK-0276) will evolve the delegation fields — do this opportunistically with those, not before.

**Benefits.** Narrower per-tool knowledge; cheaper test setup. Graded speculative: the record works today and the pain is amortized.

---

## 10. Surveyed and healthy — leave these alone

- **Task domain module** (`TaskDomainService.cs`, 1,267 lines) — big file, but one cohesive deep module behind a real 12-method seam (`ITaskService`). Size alone is not friction.
- **Memory store seam** (`IMemoryStore`) — 4 adapters (file + 3 test doubles). **Page service seam** (`IPageService`) — decorator adapter (`AuditedPageService`) wraps it with callers unaware. Both honest, deep, working.
- **Embedding provider seam** (`ITextEmbeddingProvider`) — 2-method interface hiding ONNX lifecycle, WordPiece tokenizer, disk/LRU caches; 10+ test fakes. Candidate 6 completes it rather than changing it.
- **Context planner** (`ChatContextPlanner`, 132 lines) — deep pure module (nine regexes + budget logic behind one call). Only gap: no direct unit tests — a cheap win, not a refactor.
- **Maintenance proposal workflow** — status-transition depth behind a 5-method store seam; passes the deletion test cleanly.
- **Agent session service** — earned depth (GPU slots, timeout phases, lifecycle); no duplication with the chat tool loop. Its store seam now has two production adapters (TSK-0278).

## 11. Top recommendation

**Start with candidate 1 (TSK-0282), the composition root.** It has the only proven incident (the June 4 lost-pipeline week); it retires the most dangerous test in the suite (the OAuth text-grep that passes with the code commented out); and it establishes the named-module + seam discipline that the chat turn engine (TSK-0189) and tool-loop unification (TSK-0042) reuse. Bounded scope, behaviour-preserving, factory tests stay green throughout.

Suggested sequence: TSK-0282 → TSK-0042 (loop unification first, then file split) → TSK-0189 → TSK-0283 → TSK-0284 → the rest opportunistically.
