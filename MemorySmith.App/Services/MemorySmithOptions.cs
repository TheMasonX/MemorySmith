namespace MemorySmith.App.Services;

using System.Text.Json.Serialization;
using MemorySmith.Storage;

public class MemorySmithOptions
{
    public string DataPath { get; set; } = Path.Combine("..", "Data", "Memories");
    public string PagesPath { get; set; } = Path.Combine("..", "Data", "Pages");
    public string EventLogPath { get; set; } = Path.Combine("..", "Data", "Events", "audit.log");
    public string VarsPath { get; set; } = Path.Combine("..", "Data", "vars.json");
    public string? SettingsOverridePath { get; set; }
    public string? ApiKey { get; set; }
    public bool AllowRemoteApi { get; set; }
    public DatabaseOptions Database { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public AuditOptions Audit { get; set; } = new();
    public HistoryOptions History { get; set; } = new();
    public PageOptions Pages { get; set; } = new();
    public SemanticSearchOptions SemanticSearch { get; set; } = new();
    public MaintenanceOptions Maintenance { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
    public SourceLinkOptions SourceLinks { get; set; } = new();
    public ChatOptions Chat { get; set; } = new();
    public MaintenanceAgentOptions MaintenanceAgent { get; set; } = new();
}

public class AuthOptions
{
    public bool Enabled { get; set; } = true;
    public string AnonymousAccess { get; set; } = "Viewer";
    public string AuthenticatedDefaultRole { get; set; } = "Viewer";
    public bool AutoEditorForAuthenticatedUsers { get; set; }
    public bool LocalPasswordEnabled { get; set; } = true;
    public bool RequireHttpsForRemoteAuth { get; set; } = true;
    public bool OpenLocalEditorCompatibility { get; set; } = true;
    public AuthSetupOptions Setup { get; set; } = new();
    public AuthRateLimitOptions RateLimits { get; set; } = new();
    public AuthProviderOptions Providers { get; set; } = new();
}

public class AuthSetupOptions
{
    public bool AllowLoopbackBootstrap { get; set; } = true;
    public string? BootstrapTokenHash { get; set; }
}

public class AuthRateLimitOptions
{
    public int LoginPermitLimit { get; set; } = 5;
    public int LoginWindowMinutes { get; set; } = 15;
    public int LockoutMinutes { get; set; } = 15;
    public int MaxProgressiveLockoutMinutes { get; set; } = 60;
}

public class AuthProviderOptions
{
    public ExternalProviderOption GitHub { get; set; } = new();
    public ExternalProviderOption Google { get; set; } = new();
    public ExternalProviderOption Microsoft { get; set; } = new();
}

public class ExternalProviderOption
{
    public bool Enabled { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public class AuditOptions
{
    public bool JsonlEnabled { get; set; } = true;
    public string JsonlPath { get; set; } = Path.Combine("..", "Data", "Events", "audit-{yyyy}-W{week}.jsonl");
    public string JsonlRotation { get; set; } = "Weekly";
    public bool CompressRotatedLogs { get; set; } = true;
    public bool HashChainEnabled { get; set; } = true;
}

public class HistoryOptions
{
    public string RootPath { get; set; } = Path.Combine("..", "Data", ".history");
    public string PageMode { get; set; } = "Snapshot";
    public string MemoryMode { get; set; } = "JsonPatchWithCheckpoints";
    public int MemoryCheckpointEveryVersions { get; set; } = 20;
}

public class PageOptions
{
    public bool AllowRawHtml { get; set; }
}

public class SemanticSearchOptions
{
    public bool EmbeddingsEnabled { get; set; } = true;
    public string ModelPath { get; set; } = Path.Combine("Models", "embedding-model.onnx");
    public string VocabularyPath { get; set; } = Path.Combine("Models", "vocab.txt");
    public int MaxInputTokens { get; set; } = 512;
    public int MaxIndexedTextCharacters { get; set; } = 6000;
    public string QueryPrefix { get; set; } = "query: ";
    public string DocumentPrefix { get; set; } = "passage: ";
}

public class MaintenanceOptions
{
    public bool Enabled { get; set; } = true;
    public int TriageMinutes { get; set; } = 5;
    public int IndexingMinutes { get; set; } = 60;
    public int ConsolidationHours { get; set; } = 24;
    public int StartupGraceSeconds { get; set; } = 30;
}

public class LimitOptions
{
    public int MaxPageSize { get; set; } = 100;
    public int MaxSearchLimit { get; set; } = 100;
    public int MaxContentLength { get; set; } = 20000;
    public int MaxTags { get; set; } = 50;
    public int MaxReferences { get; set; } = 200;
}

public class SourceLinkOptions
{
    public int MaxReadBytes { get; set; } = 65536;
    public bool AllowOpenWithDefaultApp { get; set; }
    public List<string> AllowedFileRootVariables { get; set; } = ["MemorySmithRepo"];
    public List<string> AllowedFileRoots { get; set; } = [];
}

public class ChatOptions
{
    public string Provider { get; set; } = "Ollama";
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "gemma4:e4b";
    public int? OllamaContextWindowTokens { get; set; }
    public string GitHubModel { get; set; } = "gpt-4.1";
    public string? GitHubCliPath { get; set; }
    public string? GitHubCliUrl { get; set; }
    public string GitHubTokenEnvironmentVariable { get; set; } = "GITHUB_TOKEN";
    public List<ChatModelOption> GitHubModels { get; set; } =
    [
        new() { Name = "gpt-4.1", ChatMultiplier = 0, IsPreferred = true, Description = "Free/standard Copilot GPT option when available" },
        new() { Name = "gpt-4.1-mini", ChatMultiplier = 0, IsPreferred = true, Description = "Free/low-cost GPT mini option when available" },
        new() { Name = "gpt-4o-mini", ChatMultiplier = 0, IsPreferred = true, Description = "Free/low-cost GPT-4o mini option when available" },
        new() { Name = "claude-3.5-haiku", IsPreferred = true, Description = "Lower-cost Claude Haiku option before Sonnet" },
        new() { Name = "gpt-5.1-mini", Description = "GPT-5.1 mini option when available" },
        new() { Name = "gpt-4o", Description = "GPT-4o option when available" },
        new() { Name = "gpt-5", Description = "GPT-5 option when available" },
        new() { Name = "claude-sonnet-4.5", Description = "Claude Sonnet option when available after cheaper candidates" }
    ];
    public string SystemPromptPath { get; set; } = Path.Combine("Prompts", "wiki-chat-agent.md");
    public int RequestTimeoutSeconds { get; set; } = 600;
    public int MaxContextRecords { get; set; } = 5;
    public int MaxContextPages { get; set; } = 5;
    public bool PreloadContextEnabled { get; set; } = true;
    public int MaxPreloadedContextRecords { get; set; } = 2;
    public int MaxPreloadedContextPages { get; set; } = 1;
    public int MaxContextItemCharacters { get; set; } = 4000;
    public int MaxHistoryMessages { get; set; } = 16;
    public int MaxAttachmentCharacters { get; set; } = 120000;
    public long MaxAttachmentBytes { get; set; } = 8 * 1024 * 1024;
    public bool ToolCallsEnabled { get; set; } = true;
    public int MaxToolIterations { get; set; } = 2;
    public int MaxToolCallsPerTurn { get; set; } = 3;
    public int MaxToolResultCharacters { get; set; } = 12000;
    public bool AgentWritesEnabled { get; set; }
}

public class ChatModelOption
{
    public string Name { get; set; } = string.Empty;
    public double? ChatMultiplier { get; set; }
    public bool IsPreferred { get; set; }
    public string? Description { get; set; }
    public int? ContextWindowTokens { get; set; }
    public string? RateLimit { get; set; }
}

public class MaintenanceAgentOptions
{
    [JsonPropertyName("config_path")]
    public string ConfigPath { get; set; } = Path.Combine("..", "Data", "maintenance_agent.yaml");

    [JsonPropertyName("read")]
    public List<string> Read { get; set; } = [Path.Combine("..", "Data", "Memories"), Path.Combine("..", "Data", "Pages")];

    [JsonPropertyName("write")]
    public List<string> Write { get; set; } = [Path.Combine("..", "Data", "Memories", "Working"), Path.Combine("..", "Data", "Pages")];

    [JsonPropertyName("direct_write")]
    public bool DirectWrite { get; set; }

    [JsonPropertyName("use_llm")]
    public bool UseLlm { get; set; } = true;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Ollama";

    [JsonPropertyName("ollama_endpoint")]
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gemma4:e4b";

    [JsonPropertyName("agent_version")]
    public string AgentVersion { get; set; } = "maintenance-agent.v1";

    [JsonPropertyName("max_findings_per_task")]
    public int MaxFindingsPerTask { get; set; } = 50;

    [JsonPropertyName("tasks")]
    public Dictionary<string, bool> Tasks { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spot_checks"] = true,
        ["staleness_scan"] = true,
        ["consistency_checks"] = true,
        ["relationship_integrity"] = true,
        ["topic_map"] = true,
        ["synthesis"] = false,
        ["embedding_chunking_maintenance"] = true
    };

    [JsonPropertyName("schedule")]
    public MaintenanceAgentScheduleOptions Schedule { get; set; } = new();

    [JsonPropertyName("resource_probe")]
    public MaintenanceAgentResourceProbeOptions ResourceProbe { get; set; } = new();

    [JsonPropertyName("storage")]
    public MaintenanceAgentStorageOptions Storage { get; set; } = new();
}

public class MaintenanceAgentScheduleOptions
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("weekly_day")]
    public string WeeklyDay { get; set; } = "Sunday";

    [JsonPropertyName("weekly_hour_local")]
    public int WeeklyHourLocal { get; set; } = 3;

    [JsonPropertyName("minimum_hours_between_runs")]
    public int MinimumHoursBetweenRuns { get; set; } = 24;
}

public class MaintenanceAgentResourceProbeOptions
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("skip_when_busy")]
    public bool SkipWhenBusy { get; set; } = true;

    [JsonPropertyName("busy_process_names")]
    public List<string> BusyProcessNames { get; set; } =
    [
        "steam",
        "epicgameslauncher",
        "fortniteclient-win64-shipping",
        "r5apex",
        "cyberpunk2077",
        "starfield",
        "eldenring"
    ];
}

public class MaintenanceAgentStorageOptions
{
    [JsonPropertyName("proposals_path")]
    public string ProposalsPath { get; set; } = Path.Combine("..", "Data", "Proposals");

    [JsonPropertyName("topic_map_cache_path")]
    public string TopicMapCachePath { get; set; } = Path.Combine("..", "Data", "Graph", "topic-map-cache.json");

    [JsonPropertyName("last_run_path")]
    public string LastRunPath { get; set; } = Path.Combine("..", "Data", "Events", "maintenance-agent-last-run.json");
}