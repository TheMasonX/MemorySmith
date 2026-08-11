## Continued Audit — Additional Bug / Architecture Findings

I continued from the first pass and found another 7 source-backed issues / hardening recommendations. These are intentionally numbered after the prior set.

Scope note: I continued using the reconstructed attached export from the manifest/chunks. I still could not run the project tests because the sandbox has no dotnet CLI available, so these are static source findings.

## Additional Source-Backed Findings

| ID | Title | Area | Classification | Priority | Impact | Probability | Severity | Confidence |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| BUG-007 | Code search is classified as ReadOnly but returns snippets, absolute paths, repo root, and index path | Source disclosure / tool governance | Source-backed risk | P1 | 8 | 7 | 56 | 86% |
| BUG-008 | Multiple services write the same settings override file with no shared lock and same temp file suffix | Config integrity / race | Source-backed bug | P1 | 8 | 6 | 48 | 84% |
| BUG-009 | Agent-session concurrency cap is checked non-atomically before session creation | Resource control / concurrency | Source-backed race risk | P2 | 7 | 6 | 42 | 78% |
| BUG-010 | system_prompt_addendum is exposed as if useful but is stored only and not injected into the model request | Agent contract / UX correctness | Source-backed bug | P2 | 6 | 7 | 42 | 90% |
| BUG-011 | Code-search shard merge trusts shard-provided document paths, absolute paths, snippets, and embeddings | Index integrity / provenance | Source-backed risk | P1 | 8 | 5 | 40 | 82% |
| BUG-012 | Raw user search/query text is written into the event store | Privacy / log hygiene | Source-backed risk | P2 | 6 | 6 | 36 | 80% |
| BUG-013 | Anonymous OAuth bridge exposes unauthenticated /authorize and /token forwarding endpoints | Auth surface / deployment hardening | Source-backed architectural risk | P2 | 6 | 5 | 30 | 74% |

# Detailed Findings

## BUG-007: Code search is classified as ReadOnly but returns source snippets and local filesystem paths

Area: Tool governance / source disclosurePriority: P1Confidence: 86%

### Claim

The [redacted source identifier] tool is categorized as ReadOnly and is available in both chat and MCP, but its payload includes code snippets, AbsolutePath, repository root, index path, and indexed file metadata. That makes it closer to a sensitive source-read tool than a harmless read-only wiki search tool. [redacted source reference], [redacted source reference]

### Evidence

In [redacted source file], [redacted source identifier] is registered with ChatToolRisk.ReadOnly, AvailableInChat: true, and AvailableInMcp: true. The tool serializes result fields including DocumentPath, AbsolutePath, StartLine, EndLine, Snippet, and MatchReason. It also calls GetStatusAsync, and the status object includes repository/index metadata. [redacted source reference], [redacted source reference]

In [redacted source file], CodeSearchResult explicitly includes AbsolutePath, and CodeSearchStatus includes RepositoryRoot and IndexPath. Search results are built from indexed chunks and include snippets and absolute paths. [redacted source reference]

### Why this matters

If the chat surface is available to lower-privilege users, this tool can disclose local repository layout and source snippets. The project already has a SensitiveRead tool tier for source bundles, so code search returning actual source snippets and absolute paths should probably align with that tier.

### Counterarguments

If every chat/MCP user is trusted to read local source code, this is acceptable. But the source itself distinguishes ReadOnly vs. SensitiveRead, and code search appears to cross that boundary.

### Recommendation

- Reclassify [redacted source identifier] and [redacted source identifier] as SensitiveRead, or split them:

- code_search_metadata = read-only, no absolute paths/snippets.

- code_search_source = sensitive-read, returns snippets and path info.

- Strip AbsolutePath, RepositoryRoot, and IndexPath from non-admin/non-sensitive responses.

- Add tests that viewer/chat-only callers cannot retrieve source snippets or local paths unless granted a source-read policy.

### knowledge system takeaway

For our knowledge system, any tool that can return code excerpts, repo paths, source bundle locations, or local filesystem metadata should be treated as source-sensitive, not generic read-only.

## BUG-008: Settings writes can clobber each other because multiple services write the same file without a shared lock

Area: Configuration integrityPriority: P1Confidence: 84%

### Claim

AdminSettingsService and ChatModelProfileService both load, mutate, and overwrite the same settings override file using the same temp path pattern, but I found no shared lock/transaction across those services. Concurrent updates can lose one writer’s changes or collide on the .tmp file. [redacted source reference], [redacted source reference]

### Evidence

[redacted source file] loads the settings JSON, mutates it, writes to _settingsPath + ".tmp", then moves the temp file over _settingsPath. [redacted source reference]

[redacted source file] independently loads the same settings root, mutates knowledge system:Chat and knowledge system:MaintenanceAgent, writes to _settingsPath + ".tmp", then moves it over _settingsPath. [redacted source reference]

### Actual behavior risk

Two requests can do this:

- Request A loads settings.

- Request B loads settings.

- Request A writes settings with changed security/admin setting.

- Request B writes settings with changed model profile, based on stale settings.

- Request A’s update is lost.

There is also a possible temp-file race because both services use the same temp filename.

### Counterarguments

If settings mutations are rare and serialized by the UI, this may be uncommon. But the services themselves do not enforce serialization.

### Recommendation

- Introduce a singleton SettingsOverrideStore with:

- process-wide async lock,

- atomic temp filename per write,

- read-modify-write function,

- optional revision/ETag check.

- Route both admin settings and model profile mutations through that store.

- Add concurrency tests with two simultaneous updates to disjoint settings paths.

### knowledge system takeaway

If we adopt a JSON-backed config/profiles system, use one durable settings store abstraction. Do not let unrelated services independently perform load/mutate/overwrite cycles.

## BUG-009: Agent-session concurrency cap is not atomic

Area: Agent session lifecycle / resource controlPriority: P2Confidence: 78%

### Claim

Agent session creation checks the active session count and then saves the new session later, but the count check and insert are not one atomic operation. Concurrent session creation requests for the same principal can exceed the configured cap. [redacted source reference], [redacted source reference], [redacted source reference]

### Evidence

[redacted source file] gets activeCount = await _store.GetActiveCountForPrincipalAsync(...), rejects if activeCount >= cap, computes scope, constructs an AgentSession, then calls _store.SaveAsync(session, ct). [redacted source reference]

[redacted source file] counts active sessions by enumerating _sessions.Values; SaveAsync later writes _sessions[session.SessionId] = session. [redacted source reference]

[redacted source file] counts rows with Status IN ('Active', 'Idle'), and SaveAsync later inserts/updates the session. There is no evidence in the reviewed code of a transaction that combines “count active sessions for principal” and “insert new session.” [redacted source reference]

### Why this matters

This undermines session caps under concurrent MCP/agent calls. For a local app this might be tolerable, but if remote API/MCP is enabled, caps are a primary resource-control mechanism.

### Counterarguments

The cap is probably a soft guardrail, not a hard security invariant. The practical impact depends on how often concurrent [redacted source identifier] calls occur.

### Recommendation

- Add a per-principal creation lock in AgentSessionService, or

- For SQLite persistence, wrap count+insert in a transaction with an appropriate isolation strategy, or

- Maintain a small PrincipalSessionCounters table updated transactionally.

### Validation Criteria

- Start N concurrent session-creation requests for one principal with cap=1.

- Assert only one succeeds.

- Run the same test against both in-memory and SQLite stores.

### knowledge system takeaway

If our knowledge system supports sub-agents, concurrency/session budgets must be enforced atomically. Otherwise “agent fan-out control” is advisory rather than reliable.

## BUG-010: system_prompt_addendum is exposed as a feature but is not injected into agent calls

Area: Agent-session API contractPriority: P2Confidence: 90%

### Claim

The MCP agent schema exposes system_prompt_addendum and describes it as extra instructions appended to the sub-agent’s system context, but the implementation stores it on the session and does not pass it into the MemoryChatRequest used for invocation. [redacted source reference], [redacted source reference]

### Evidence

[redacted source file] accepts system_prompt_addendum from the MCP arguments and passes it into CreateSessionAsync. The schema says it is “optional extra instructions appended to the sub-agent’s system context,” while also saying injection is a Phase 3 feature. [redacted source reference]

[redacted source file] stores the effective addendum in AgentSession.SystemPromptAddendum, but InvokeCoreAsync constructs new MemoryChatRequest(...) with message, mode, history, model, provider, and session ID only. No addendum is passed to the model request. [redacted source reference]

[redacted source file] confirms this with a TODO: the field is stored but not injected until a future MemoryChatRequest.SystemPromptAddendum exists. [redacted source reference]

### Actual behavior

Callers can provide the parameter, receive no error, and the session can store the value, but the sub-agent prompt is not affected by it.

### Counterarguments

The schema text partially discloses that injection is not implemented. However, the first part of the description still describes it as appended instructions, so this is easy for clients/agents to misuse.

### Recommendation

Either:

- remove system_prompt_addendum from the public schema until implemented, or

- return a clear error like “not implemented,” or

- wire it into MemoryChatRequest and enforce sanitization/profile policy.

### knowledge system takeaway

Do not expose “future” prompt-control knobs in production agent schemas. If a field is accepted, it must either work or fail loudly.

## BUG-011: Code-search shard merge trusts shard-provided paths and snippets

Area: Index integrity / source provenancePriority: P1Confidence: 82%

### Claim

The shard merge path checks the shard database file extension and existence, but then imports TargetKey, DocumentPath, AbsolutePath, line ranges, snippets, search text, embeddings, and timestamps from the shard. I found no evidence in the reviewed merge code that imported rows are revalidated against the local repository root or recomputed from local source files before insertion. [redacted source reference], [redacted source reference]

### Evidence

[redacted source file] MergeShardAsync validates that the shard path is non-empty, full path normalized, extension is .db/.sqlite/.sqlite3, and the file exists. Then LoadShardChunksAsync reads TargetKey, DocumentPath, AbsolutePath, ChunkId, SourceHash, line ranges, Snippet, SearchText, EmbeddingJson, and IndexedAtUtc from the shard’s CodeSearchChunks table. The insert path then writes those imported fields into the main CodeSearchChunks table. [redacted source reference]

[redacted source file] exposes [redacted source identifier] as a write-tier MCP tool and validates the shard path before calling the service. [redacted source reference]

### Why this matters

A shard is a portable database file. If its contents are stale, malicious, or generated against a different checkout, the main index can be polluted with false snippets, wrong absolute paths, or misleading embeddings. This is particularly dangerous if agents treat code-search results as primary evidence.

### Counterarguments

The tool is write-tier and should require editor-level permission. If shards are produced only by trusted CI, risk is lower.

### Recommendation

- Treat shard rows as claims, not source truth.

- On merge, validate each row:

- DocumentPath resolves under the configured repo root.

- local file exists.

- local content hash matches SourceHash.

- snippet/line range is recomputed from local file, not trusted from shard.

- Store shard provenance: shard file path/hash, merge timestamp, merge actor, source commit if available.

- Mark unverified shard rows as “staged” until local source validation passes.

### knowledge system takeaway

This is directly relevant to our KB discipline: imported indexes or reports must not be treated as primary source. For SmartView/SmartAdvisor, code-search shards should only accelerate discovery; findings still need current source reads before promotion.

## BUG-012: Raw query text is appended to the event store

Area: Logging / privacy / customer-sensitive dataPriority: P2Confidence: 80%

### Claim

The memory search service records raw query text into an event store for lexical, semantic, hybrid, and context-pack requests. If users search for customer names, credentials, proprietary code symbols, bug details, or pasted snippets, those strings become durable event data. [redacted source reference]

### Evidence

[redacted source file] calls RecordQueryEvent(...) in search/context-pack methods. RecordQueryEvent appends a MemoryEvent with Action = $"Query:{kind}" and Details = text ?? string.Empty. [redacted source reference]

### Why this matters

For an internal engineering wiki, search strings often contain exactly the kind of sensitive information we do not want in broad logs: customer names, defect details, symbols, paths, incident IDs, and pasted code.

### Counterarguments

Query logs can be useful for diagnostics and search quality tuning. The issue is not existence of telemetry; it is raw durable query capture without visible redaction/retention controls in the reviewed code.

### Recommendation

- Store length/hash/token-count by default, not raw text.

- Add opt-in debug logging for raw query text with short retention.

- Redact obvious secrets and large pasted snippets.

- Add category-aware logging: customer-sensitive, source-sensitive, security-sensitive, etc.

## BUG-013: Anonymous OAuth bridge forwards /authorize and /token traffic without local auth

Area: Deployment hardening / auth bridge surfacePriority: P2Confidence: 74%

### Claim

The project includes an anonymous OAuth bridge at root routes /authorize and /token. /authorize redirects to GitHub’s authorize endpoint with the incoming query string, and /token reads the incoming body and forwards it to GitHub’s token endpoint, returning GitHub’s response payload/status/content type. This is source-backed behavior; whether it is a bug depends on intended deployment mode.

### Evidence

[redacted source file] has [Route("")], [IgnoreAntiforgeryToken], [AllowAnonymous] on GET /authorize, and [AllowAnonymous] on POST /token. Authorize() redirects to [redacted external link] using the current request query string. ExchangeCode() reads the request body, posts it to [redacted external link] and returns the response. [redacted source reference]

### Risk

This creates an unauthenticated forwarding surface in the application. It is limited to GitHub endpoints, so it is not a generic open proxy, but it does create another externally reachable auth-related endpoint pair if the app is exposed.

### Counterarguments

This may exist specifically to support a local OAuth/client integration pattern. I did not find enough surrounding deployment documentation in the inspected snippets to prove it is unsafe in its intended environment.

### Recommendation

- Gate the bridge behind an explicit feature flag.

- Disable it by default in remote-hardened mode.

- Add request size limits and content-type validation.

- Add tests/documentation proving which clients rely on it.

- Use a separate route prefix like /oauth-bridge/github/* so it cannot be confused with the main app auth surface.

# Suggested Next Fix Order

- BUG-007: Reclassify code search as sensitive-read or strip paths/snippets for ordinary read-only callers.

- BUG-008: Centralize settings writes behind a locked/revisioned settings store.

- BUG-011: Validate code-search shard content against local source before merge.

- BUG-010: Remove or implement system_prompt_addendum.

- BUG-009: Make agent-session caps atomic.

- BUG-012: Redact/hash query telemetry.

- BUG-013: Feature-flag and harden the OAuth bridge.
