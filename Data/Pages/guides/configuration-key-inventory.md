# Configuration Key Inventory

> **Generated from** `AdminSettingsService.BuildEditableSettings()`  
> **Last updated:** 2026-07-11  
> **Total editable keys:** 222  
> **Companion page:** [Configuration Reference](configuration-reference.md) — grouped operator guidance  
> **Coverage check:** `Scripts/Test-ConfigKeyCoverage.ps1`

This page lists **every editable configuration key** exposed by the admin UI, grouped by category. Each entry shows the label, value kind, sensitivity, help text, and which `configuration-reference.md` group covers it.

---

## Core paths (7 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:DataPath` | Memory records path | String | Filesystem path for structured `MemoryRecord` JSON files |
| `MemorySmith:PagesPath` | Markdown pages path | String | Filesystem path for markdown-backed wiki pages and assets |
| `MemorySmith:EventLogPath` | Event log path | String | Path for the legacy file event log |
| `MemorySmith:VarsPath` | Variables path | String | Path to `vars.json` for source-link variable map |
| `MemorySmith:DataProtectionKeysPath` | Data protection keys path | String | ASP.NET Core data-protection keys directory |
| `MemorySmith:SettingsOverridePath` | Settings override path | String *(file-managed)* | Optional override file location (not editable from `/admin`) |
| `MemorySmith:Blazor:MaximumReceiveMessageSizeBytes` | Blazor max receive bytes | Integer | Maximum SignalR payload size accepted |

---

## Security (10 keys)

| Key | Label | Kind | Sensitive | Purpose |
|-----|-------|------|-----------|---------|
| `MemorySmith:SecurityProfile` | Security profile | Choice | No | Preset: `local-dev`, `secure-local`, `remote-hardened` |
| `MemorySmith:AllowRemoteApi` | Allow remote API | Boolean | No | Non-loopback HTTP API/MCP request guard |
| `MemorySmith:ApiKey` | Shared API key | String | **Yes** | Write-only key for remote API/MCP access |
| `MemorySmith:ContentSecurityPolicyEnabled` | Enable CSP header | Boolean | No | Content-Security-Policy response header |
| `MemorySmith:ContentSecurityPolicy` | CSP value | String | No | Raw CSP header value |
| `MemorySmith:XContentTypeOptionsEnabled` | Enable X-Content-Type-Options | Boolean | No | MIME-sniffing defense |
| `MemorySmith:XContentTypeOptions` | X-Content-Type-Options value | String | No | Response value (`nosniff` etc.) |
| `MemorySmith:ReferrerPolicyEnabled` | Enable Referrer-Policy | Boolean | No | Outbound referrer disclosure control |
| `MemorySmith:ReferrerPolicy` | Referrer-Policy value | String | No | Response value |
| `MemorySmith:XFrameOptionsEnabled` | Enable X-Frame-Options | Boolean | No | Clickjacking defense |
| `MemorySmith:XFrameOptions` | X-Frame-Options value | String | No | Response value (`DENY` etc.) |
| `MemorySmith:PermissionsPolicyEnabled` | Enable Permissions-Policy | Boolean | No | Browser feature access constraint |
| `MemorySmith:PermissionsPolicy` | Permissions-Policy value | String | No | Response value |

---

## Database (5 keys)

| Key | Label | Kind | Sensitive | Purpose |
|-----|-------|------|-----------|---------|
| `MemorySmith:Database:Provider` | Database provider | Choice | No | `SQLite` (only supported provider) |
| `MemorySmith:Database:ConnectionString` | Connection string | String | **Yes** | Write-only SQLite connection string |
| `MemorySmith:Database:ApplyMigrationsOnStartup` | Apply migrations on startup | Boolean | No | Run SQLite metadata migrations at startup |
| `MemorySmith:Database:UseWal` | Use SQLite WAL | Boolean | No | Write-ahead logging for better concurrency |
| `MemorySmith:Database:BusyTimeoutSeconds` | SQLite busy timeout | Integer | No | Max wait for locked database |

---

## Auth (12 keys)

| Key | Label | Kind | Sensitive | Purpose |
|-----|-------|------|-----------|---------|
| `MemorySmith:Auth:Enabled` | Authentication enabled | Boolean | No | Interactive sign-in control |
| `MemorySmith:Auth:AnonymousAccess` | Anonymous access | Choice | No | Role for signed-out visitors (`None`, `Viewer`) |
| `MemorySmith:Auth:AuthenticatedDefaultRole` | Default signed-in role | Choice | No | Role for new users (`Viewer`, `Editor`) |
| `MemorySmith:Auth:AutoEditorForAuthenticatedUsers` | Auto editor for signed-in | Boolean | No | Treat authenticated users as editors |
| `MemorySmith:Auth:LocalPasswordEnabled` | Local password sign-in | Boolean | No | Built-in username/password provider |
| `MemorySmith:Auth:AllowAdminCreateLocalUsers` | Allow admin-created local users | Boolean | No | Admin-created local password accounts |
| `MemorySmith:Auth:RequireHttpsForRemoteAuth` | Require HTTPS for remote auth | Boolean | No | Block remote auth over HTTP |
| `MemorySmith:Auth:OpenLocalEditorCompatibility` | Pre-setup local write compat | Boolean | No | Bootstrap compatibility valve |
| `MemorySmith:Auth:Setup:AllowLoopbackBootstrap` | Allow loopback bootstrap | Boolean | No | First-admin setup from loopback |
| `MemorySmith:Auth:Setup:BootstrapTokenHash` | Bootstrap token hash | String | **Yes** | Write-only hash for first-admin setup |
| `MemorySmith:Auth:RateLimits:LoginPermitLimit` | Login permit limit | Integer | No | Max failed login attempts in window |
| `MemorySmith:Auth:RateLimits:LoginWindowMinutes` | Login window minutes | Integer | No | Rolling window for login attempts |

---

## Auth providers (12 keys)

| Key | Label | Kind | Sensitive | Purpose |
|-----|-------|------|-----------|---------|
| `MemorySmith:Auth:Providers:GitHub:Enabled` | GitHub OAuth enabled | Boolean | No | GitHub OAuth login |
| `MemorySmith:Auth:Providers:GitHub:ClientId` | GitHub client id | String | **Yes** | OAuth client id |
| `MemorySmith:Auth:Providers:GitHub:ClientSecret` | GitHub client secret | String | **Yes** | OAuth client secret |
| `MemorySmith:Auth:Providers:Google:Enabled` | Google OAuth enabled | Boolean | No | Google OAuth login |
| `MemorySmith:Auth:Providers:Google:ClientId` | Google client id | String | **Yes** | OAuth client id |
| `MemorySmith:Auth:Providers:Google:ClientSecret` | Google client secret | String | **Yes** | OAuth client secret |
| `MemorySmith:Auth:Providers:Microsoft:Enabled` | Microsoft OAuth enabled | Boolean | No | Microsoft identity login |
| `MemorySmith:Auth:Providers:Microsoft:ClientId` | Microsoft client id | String | **Yes** | Entra application client id |
| `MemorySmith:Auth:Providers:Microsoft:ClientSecret` | Microsoft client secret | String | **Yes** | Entra client secret |
| *(GitHub/Google/Microsoft each have 3 keys: Enabled, ClientId, ClientSecret)* | | | | |

---

## Audit (5 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Audit:JsonlEnabled` | JSONL audit enabled | Boolean | Append-only JSONL audit entries |
| `MemorySmith:Audit:JsonlPath` | JSONL audit path | String | Path template for rotated audit files |
| `MemorySmith:Audit:JsonlRotation` | JSONL audit rotation | String | Rotation label (default: Weekly) |
| `MemorySmith:Audit:CompressRotatedLogs` | Compress rotated logs | Boolean | Compress audit files after rotation |
| `MemorySmith:Audit:HashChainEnabled` | Audit hash chain | Boolean | Tamper-evident hash chaining |

---

## Logging (16 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Logging:MinimumLevel` | Minimum log level | Choice | Minimum Serilog level (Verbose–Fatal) |
| `MemorySmith:Logging:EnableConsole` | Enable console sink | Boolean | Console output |
| `MemorySmith:Logging:EnableStructuredFile` | Enable structured file sink | Boolean | Structured JSONL logs |
| `MemorySmith:Logging:StructuredFilePath` | Structured file path | String | Path for structured JSONL |
| `MemorySmith:Logging:StructuredFileRetainedDays` | Structured retention days | Integer | Max days for rolling log files |
| `MemorySmith:Logging:WindowsEventLogEnabled` | Windows Event Log enabled | Boolean | Windows Event Log output |
| `MemorySmith:Logging:WindowsEventLogSource` | Windows Event Log source | String | Event source name |
| `MemorySmith:Logging:WindowsEventLogName` | Windows Event Log name | String | Event log channel |
| `MemorySmith:Logging:RequestLoggingEnabled` | Request logging enabled | Boolean | HTTP request logging middleware |
| `MemorySmith:Logging:SlowRequestThresholdMs` | Slow request threshold (ms) | Integer | Warning-level threshold |
| `MemorySmith:Logging:BenchmarkLoggingEnabled` | Benchmark logging enabled | Boolean | Benchmark timing events |
| `MemorySmith:Logging:BenchmarkSlowThresholdMs` | Benchmark slow threshold (ms) | Integer | Benchmark threshold |
| `MemorySmith:Logging:MetricsWindowDays` | Metrics window days | Integer | Log-derived metric window |
| `MemorySmith:Logging:MetricsSampleLimit` | Metrics sample limit | Integer | Max log entries for metrics |
| `MemorySmith:Logging:MaxDiagnosticsLogResults` | Diagnostics max log results | Integer | Max entries from log-search endpoints |

---

## Telemetry (16 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Telemetry:Enabled` | Telemetry enabled | Boolean | OpenTelemetry instrumentation |
| `MemorySmith:Telemetry:ServiceName` | Service name | String | OTel service name |
| `MemorySmith:Telemetry:TracingEnabled` | Tracing enabled | Boolean | Trace instrumentation |
| `MemorySmith:Telemetry:MetricsEnabled` | Metrics enabled | Boolean | Metrics instrumentation |
| `MemorySmith:Telemetry:InstrumentMemoryOperations` | Instrument memory ops | Boolean | Custom MemorySmith spans |
| `MemorySmith:Telemetry:AspNetCoreInstrumentationEnabled` | ASP.NET Core instrumentation | Boolean | HTTP request spans/metrics |
| `MemorySmith:Telemetry:HttpClientInstrumentationEnabled` | HttpClient instrumentation | Boolean | Outbound HttpClient spans |
| `MemorySmith:Telemetry:RuntimeInstrumentationEnabled` | Runtime metrics instrumentation | Boolean | Runtime/system metrics |
| `MemorySmith:Telemetry:RecordExceptions` | Record exceptions | Boolean | Exception events in spans |
| `MemorySmith:Telemetry:TraceSamplingPercentage` | Trace sampling % | Integer | Parent-based sampling (0–100) |
| `MemorySmith:Telemetry:ExporterEnabled` | OTLP exporter enabled | Boolean | Export to collector |
| `MemorySmith:Telemetry:OtlpEndpoint` | OTLP endpoint | String | Collector URL |
| `MemorySmith:Telemetry:OtlpProtocol` | OTLP protocol | Choice | `grpc` or `http/protobuf` |
| `MemorySmith:Telemetry:ExcludedRequestPathPrefixes` | Excluded path prefixes | StringList | Paths excluded from tracing |

---

## History (4 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:History:RootPath` | Version history path | String | Root for memory/page history artifacts |
| `MemorySmith:History:PageMode` | Page history mode | String | Storage mode for page snapshots |
| `MemorySmith:History:MemoryMode` | Memory history mode | String | Storage mode for memory diffs |
| `MemorySmith:History:MemoryCheckpointEveryVersions` | Memory checkpoint interval | Integer | Versions between full checkpoints |

---

## Pages (4 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Pages:DefaultMinimumRole` | Default page visibility | Choice | Minimum role for pages |
| `MemorySmith:Pages:AllowRawHtml` | Allow raw page HTML | Boolean | Raw HTML rendering |
| `MemorySmith:Markdown:MermaidEnabled` | Enable Mermaid diagrams | Boolean | Mermaid rendering |
| `MemorySmith:Markdown:MermaidRestrictionMode` | Mermaid restriction mode | Choice | `standard`, `restricted`, `strict` |

---

## Search (26 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:SemanticSearch:EmbeddingsEnabled` | Semantic embeddings enabled | Boolean | ONNX embedding search |
| `MemorySmith:SemanticSearch:ModelPath` | Embedding model path | String | ONNX model path |
| `MemorySmith:SemanticSearch:VocabularyPath` | Vocabulary path | String | ONNX vocabulary path |
| `MemorySmith:SemanticSearch:TokenizerKind` | Tokenizer kind | String | WordPiece etc. |
| `MemorySmith:SemanticSearch:PoolingMode` | Pooling mode | String | `Mean` or `Cls` |
| `MemorySmith:SemanticSearch:ExecutionProvider` | Execution provider | Choice | `Cpu`, `Cuda`, `OpenVino` |
| `MemorySmith:SemanticSearch:PrewarmOnStartupEnabled` | Prewarm on startup | Boolean | Background ONNX warmup |
| `MemorySmith:SemanticSearch:CpuFallbackEnabled` | CPU fallback | Boolean | Fallback on GPU failure |
| `MemorySmith:SemanticSearch:CudaDeviceId` | CUDA device id | Integer | GPU device selection |
| `MemorySmith:SemanticSearch:OpenVinoDeviceId` | OpenVINO device id | String | OpenVINO target |
| `MemorySmith:SemanticSearch:MaxInputTokens` | Max input tokens | Integer | Model input token cap |
| `MemorySmith:SemanticSearch:MaxIndexedTextCharacters` | Max indexed chars | Integer | Record char limit for indexing |
| `MemorySmith:SemanticSearch:QueryPrefix` | Query prefix | String | Embedding query prefix |
| `MemorySmith:SemanticSearch:DocumentPrefix` | Document prefix | String | Embedding document prefix |
| `MemorySmith:CodeSearch:HybridVectorWeight` | Code-search hybrid vector weight | Decimal | Vector similarity weight |
| `MemorySmith:CodeSearch:HybridLexicalWeight` | Code-search hybrid lexical weight | Decimal | Lexical evidence weight |
| `MemorySmith:CodeSearch:ZeroLexicalEvidencePenalty` | Zero lexical penalty | Decimal | Penalty for no lexical evidence |
| `MemorySmith:CodeSearch:LexicalScoreSaturation` | Lexical score saturation | Decimal | Saturation factor |
| `MemorySmith:CodeSearch:LexicalFrequencyBonusScale` | Lexical frequency scale | Decimal | Repeated-token bonus |
| `MemorySmith:CodeSearch:MaxLexicalFrequencyBonusPerToken` | Max lexical frequency bonus | Decimal | Per-token bonus cap |
| `MemorySmith:CodeSearch:MinTokenCoverageWeight` | Min token coverage weight | Decimal | Partial-intent penalty |
| `MemorySmith:CodeSearch:MaxTokenCoverageWeight` | Max token coverage weight | Decimal | Full-intent boost |
| `MemorySmith:CodeSearch:VectorPrefilterFullScanFallbackCandidateCount` | Sparse prefilter fallback | Integer | Fallback pass threshold |
| `MemorySmith:CodeSearch:MaxResults` | Code-search max results | Integer | Operator cap |
| `MemorySmith:CodeSearch:MaxResultsPerDocument` | Max results per document | Integer | Per-document cap |
| `MemorySmith:TaskSearch:HybridSemanticEnabled` | Task hybrid semantic search | Boolean | Hybrid lexical+semantic for tasks |

---

## Tasks (2 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:TaskAttachments:StoragePath` | Task attachment storage path | String | Upload storage directory |
| `MemorySmith:TaskAttachments:MaxFileBytes` | Task attachment max bytes | Integer | Max upload size |

---

## Governance (1 key)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Governance:TagPolicyPath` | Tag policy path | String | JSON tag policy path |

---

## Training (15 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Training:ChatTranscriptEnabled` | Chat transcript capture | Boolean | Transcript JSONL for fine-tuning |
| `MemorySmith:Training:StoreChatContent` | Store chat transcript content | Boolean | Literal user/assistant text |
| `MemorySmith:Training:TranscriptRetentionDays` | Transcript retention days | Integer | Max age before cleanup |
| `MemorySmith:Training:TranscriptRedactionEnabled` | Transcript redaction | Boolean | Redact tokens/secrets |
| `MemorySmith:Training:FeedbackEnabled` | Chat feedback capture | Boolean | Thumbs/feedback persistence |
| `MemorySmith:Training:MaxRunMinutes` | Training run max minutes | Integer | Wall-clock timeout |
| `MemorySmith:Training:PreferenceFormat` | Preference format | Choice | `FilteredSft`, `Dpo`, `Orpo` |
| `MemorySmith:Training:ActiveModelTag` | Active model tag | String | Promoted fine-tuned model |
| `MemorySmith:Training:FallbackModelTag` | Fallback model tag | String | Base model when no tuned target |
| `MemorySmith:Training:TranscriptDirectory` | Transcript directory | String | Daily transcript JSONL path |
| `MemorySmith:Training:TrainingDataExportPath` | Training export path | String | SFT/DPO/ORPO export directory |
| `MemorySmith:Training:RunsDirectory` | Training runs directory | String | Per-run artifacts |
| `MemorySmith:Training:PythonVenvPath` | Python venv path | String | Training harness venv |
| `MemorySmith:Training:PythonHarnessScript` | Training harness script | String | Python entrypoint |

---

## Maintenance (5 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Maintenance:Enabled` | Maintenance enabled | Boolean | Background maintenance loops |
| `MemorySmith:Maintenance:TriageMinutes` | Triage interval minutes | Integer | Status transition evaluation |
| `MemorySmith:Maintenance:IndexingMinutes` | Indexing interval minutes | Integer | Search index refresh |
| `MemorySmith:Maintenance:ConsolidationHours` | Consolidation interval hours | Integer | Duplicate/related check |
| `MemorySmith:Maintenance:StartupGraceSeconds` | Startup grace seconds | Integer | Delay before maintenance starts |
| `MemorySmith:Maintenance:AutomaticDeprecationEnabled` | Automatic deprecation | Boolean | Auto-deprecate low-score records |

---

## Limits (5 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Limits:MaxPageSize` | Max page size | Integer | API page size cap |
| `MemorySmith:Limits:MaxSearchLimit` | Max search limit | Integer | Search result cap |
| `MemorySmith:Limits:MaxContentLength` | Max memory content length | Integer | Memory content char cap |
| `MemorySmith:Limits:MaxTags` | Max tags per memory | Integer | Tag count cap |
| `MemorySmith:Limits:MaxReferences` | Max references per memory | Integer | Reference/conflict count cap |

---

## Source links (9 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:SourceLinks:AllowOpenWithDefaultApp` | Open with OS | Boolean | OS default app for file links |
| `MemorySmith:SourceLinks:MaxReadBytes` | Max source read bytes | Integer | Read size cap |
| `MemorySmith:SourceLinks:AllowUnrestrictedSourceReads` | Allow unrestricted reads | Boolean | Broad file access |
| `MemorySmith:SourceLinks:ReadContextLinesBefore` | Context lines before | Integer | Leading context |
| `MemorySmith:SourceLinks:ReadContextLinesAfter` | Context lines after | Integer | Trailing context |
| `MemorySmith:SourceLinks:AllowedFileRootVariables` | Allowed root variables | StringList | Variable-based source roots |
| `MemorySmith:SourceLinks:AllowedFileRoots` | Allowed root paths | StringList | Absolute source roots |
| `MemorySmith:SourceLinks:DeniedFileRootVariables` | Denied root variables | StringList | Blocked variable roots |
| `MemorySmith:SourceLinks:DeniedFileRoots` | Denied root paths | StringList | Blocked absolute roots |

---

## MCP (3 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Mcp:EnabledTools` | Enabled MCP tools | StringList | Opt-in sensitive/write tools |
| `MemorySmith:Mcp:DisabledTools` | Disabled MCP tools | StringList | Hide/reject specific tools |
| `MemorySmith:Mcp:MaxToolResponseCharacters` | Max tool response chars | Integer | Response truncation cap |

---

## Chat (28 keys)

| Key | Label | Kind | Purpose |
|-----|-------|------|---------|
| `MemorySmith:Chat:Provider` | Default chat provider | Choice | `Ollama`, `GitHubCopilot`, `OpenAI` |
| `MemorySmith:Chat:OllamaEndpoint` | Ollama endpoint | String | Ollama API base URL |
| `MemorySmith:Chat:OllamaModel` | Ollama model | String | Default Ollama model |
| `MemorySmith:Chat:OllamaContextWindowTokens` | Ollama context window | NullableInteger | Optional context window size |
| `MemorySmith:Chat:GitHubModel` | GitHub model | String | Default GitHub model id |
| `MemorySmith:Chat:GitHubCliPath` | GitHub CLI path | String | Explicit CLI path |
| `MemorySmith:Chat:GitHubCliUrl` | GitHub CLI URL | String | CLI install/help URL |
| `MemorySmith:Chat:GitHubTokenEnvironmentVariable` | GitHub token env var | String | Env var name for token |
| `MemorySmith:Chat:OpenAIEndpoint` | OpenAI-compatible endpoint | String | Base URL for Open AI API |
| `MemorySmith:Chat:OpenAIApiKeyEnvironmentVariable` | OpenAI API key env var | String | Env var name for API key |
| `MemorySmith:Chat:OpenAIModel` | OpenAI-compatible model | String | Default model name |
| `MemorySmith:Chat:SystemPromptPath` | System prompt path | String | Chat agent system prompt |
| `MemorySmith:Chat:RequestTimeoutSeconds` | Request timeout seconds | Integer | Provider request timeout |
| `MemorySmith:Chat:MaxContextRecords` | Max memory context records | Integer | Memory record limit |
| `MemorySmith:Chat:MaxContextPages` | Max page context results | Integer | Page result limit |
| `MemorySmith:Chat:PreloadContextEnabled` | Preload chat context | Boolean | Context planner preload |
| `MemorySmith:Chat:MaxPreloadedContextRecords` | Max preloaded memories | Integer | Preloaded memory cap |
| `MemorySmith:Chat:MaxPreloadedContextPages` | Max preloaded pages | Integer | Preloaded page cap |
| `MemorySmith:Chat:MaxContextItemCharacters` | Max context item chars | Integer | Per-item char limit |
| `MemorySmith:Chat:MaxHistoryMessages` | Max history messages | Integer | Conversation history cap |
| `MemorySmith:Chat:MaxAttachmentCharacters` | Max attachment chars | Integer | Attachment text cap |
| `MemorySmith:Chat:MaxAttachmentBytes` | Max attachment bytes | Integer | Attachment size cap |
| `MemorySmith:Chat:AttachmentTempFileRetentionHours` | Attachment retention hours | Integer | Temp file cleanup age |
| `MemorySmith:Chat:ClipboardFetchExternalImagesEnabled` | Fetch external clipboard URLs | Boolean | HTTP image fetching |
| `MemorySmith:Chat:ToolCallsEnabled` | Tool calls enabled | Boolean | MemorySmith tool protocol |
| `MemorySmith:Chat:MaxToolIterations` | Max tool iterations | Integer | Follow-up loop bound |
| `MemorySmith:Chat:MaxToolCallsPerTurn` | Max tool calls per turn | Integer | Per-turn tool cap |
| `MemorySmith:Chat:MaxToolResultCharacters` | Max tool result chars | Integer | Tool output truncation cap |
| `MemorySmith:Chat:AgentWritesEnabled` | Agent writes enabled | Boolean | Agent mode write proposals |
| `MemorySmith:Chat:AgentWriteApprovalMode` | Agent write approval mode | String | `Manual` or `auto_accept` |
| `MemorySmith:Chat:AgentWriteRoots` | Chat proposal write roots | StringList | Paths agent proposals may target |

---

## Maintenance agent (27 keys)

| Key | Label | Kind | Sensitive | Purpose |
|-----|-------|------|-----------|---------|
| `MemorySmith:MaintenanceAgent:Read` | Agent read roots | StringList | No | Paths agent may read |
| `MemorySmith:MaintenanceAgent:Write` | Agent write roots | StringList | No | Paths agent may write |
| `MemorySmith:MaintenanceAgent:UseLlm` | Use LLM review | Boolean | No | LLM-assisted review |
| `MemorySmith:MaintenanceAgent:Provider` | Agent provider | Choice | No | `Ollama` or `GitHub` |
| `MemorySmith:MaintenanceAgent:OllamaEndpoint` | Agent Ollama endpoint | String | No | Ollama endpoint for agent |
| `MemorySmith:MaintenanceAgent:Model` | Agent model | String | No | Model id for reviews |
| `MemorySmith:MaintenanceAgent:ModelProfileId` | Maintenance model profile | String | No | Admin Models profile override |
| `MemorySmith:MaintenanceAgent:ProposalReviewModelProfileId` | Proposal review model profile | String | No | Review-specific profile |
| `MemorySmith:MaintenanceAgent:AdminChatModelProfileId` | Admin chat model profile | String | No | Admin chat profile |
| `MemorySmith:MaintenanceAgent:AgentVersion` | Agent prompt version | String | No | Version label in prompts |
| `MemorySmith:MaintenanceAgent:MaxFindingsPerTask` | Max findings per task | Integer | No | Finding count cap |
| `MemorySmith:MaintenanceAgent:DirectWrite` | Allow direct agent writes | Boolean | No | Direct write mode |
| `MemorySmith:MaintenanceAgent:ActionUx:ShowAccept` | Show accept action | Boolean | No | Show Accept on proposals |
| `MemorySmith:MaintenanceAgent:ActionUx:ShowRespond` | Show respond action | Boolean | No | Show Respond on proposals |
| `MemorySmith:MaintenanceAgent:ActionUx:ShowReject` | Show reject action | Boolean | No | Show Reject on proposals |
| `MemorySmith:MaintenanceAgent:ActionUx:DefaultAction` | Proposal default action | Choice | No | Primary action (`Accept`, `Respond`, `Reject`) |
| `MemorySmith:MaintenanceAgent:ActionUx:RevisionRequired` | Revision required | Boolean | No | Require revision before accept |
| `MemorySmith:MaintenanceAgent:Tasks:spot_checks` | Enable spot checks | Boolean | No | General wiki spot-check reviews |
| `MemorySmith:MaintenanceAgent:Tasks:staleness_scan` | Enable staleness scan | Boolean | No | Stale/expiry checks |
| `MemorySmith:MaintenanceAgent:Tasks:consistency_checks` | Enable consistency checks | Boolean | No | Contradiction/weak-evidence checks |
| `MemorySmith:MaintenanceAgent:Tasks:relationship_integrity` | Enable relationship integrity | Boolean | No | Reference/conflict target checks |
| `MemorySmith:MaintenanceAgent:Tasks:topic_map` | Enable topic map maintenance | Boolean | No | Topic-map summary refresh |
| `MemorySmith:MaintenanceAgent:Tasks:synthesis` | Enable synthesis maintenance | Boolean | No | Synthesized current-state knowledge |
| `MemorySmith:MaintenanceAgent:Tasks:embedding_chunking_maintenance` | Enable embedding chunking maint. | Boolean | No | Embedding/chunking readiness |
| `MemorySmith:MaintenanceAgent:Schedule:Enabled` | Weekly scheduler enabled | Boolean | No | Scheduled weekly runs |
| `MemorySmith:MaintenanceAgent:Schedule:WeeklyDay` | Weekly run day | Choice | No | Day of week |
| `MemorySmith:MaintenanceAgent:Schedule:WeeklyHourLocal` | Weekly run hour | Integer | No | Local hour (0–23) |
| `MemorySmith:MaintenanceAgent:Schedule:MinimumHoursBetweenRuns` | Min hours between runs | Integer | No | Run spacing guard |
| `MemorySmith:MaintenanceAgent:ResourceProbe:Enabled` | Resource probe enabled | Boolean | No | Local activity check |
| `MemorySmith:MaintenanceAgent:ResourceProbe:SkipWhenBusy` | Skip when busy | Boolean | No | Skip on busy processes |
| `MemorySmith:MaintenanceAgent:ResourceProbe:BusyProcessNames` | Busy process names | StringList | No | Process names marking busy |
| `MemorySmith:MaintenanceAgent:Storage:ProposalsPath` | Proposals path | String | No | Proposal artifact directory |
| `MemorySmith:MaintenanceAgent:Storage:TopicMapCachePath` | Topic map cache path | String | No | Cached topic-map file |
| `MemorySmith:MaintenanceAgent:Storage:LastRunPath` | Last run state path | String | No | Last-run state file |
| `MemorySmith:MaintenanceAgent:Storage:ActivityLogPath` | Activity log path | String | No | Run summary JSONL |
| `MemorySmith:MaintenanceAgent:Storage:TranscriptLogPath` | Transcript log path | String | No | Admin chat transcript JSONL |
| `MemorySmith:MaintenanceAgent:Storage:TranscriptRetentionEntries` | Transcript retention entries | Integer | No | Max transcript entries |
| `MemorySmith:MaintenanceAgent:Storage:TranscriptRedactionEnabled` | Transcript redaction | Boolean | No | Redact secrets in transcripts |

---

## Agent notes

- **Sensitive keys** (marked **Yes** above) are write-only in the admin UI. The app reports `Configured` or `Not configured` instead of echoing values.
- **StringList** values are edited one entry per line in the admin UI.
- **NullableInteger** values accept blank to clear the override.
- Keys not listed here are file-managed (e.g., `MemorySmith:SettingsOverridePath`) or complex structured entries (e.g., `MemorySmith:Chat:ModelProfiles`).
- This inventory is generated from `AdminSettingsService.BuildEditableSettings()`. Run `Scripts/Test-ConfigKeyCoverage.ps1` to verify that every key has a documented entry.
