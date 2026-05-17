namespace MemorySmith.App.Services;

public class MemorySmithOptions
{
    public string DataPath { get; set; } = Path.Combine("..", "Data", "Memories");
    public string PagesPath { get; set; } = Path.Combine("..", "Data", "Pages");
    public string EventLogPath { get; set; } = Path.Combine("..", "Data", "Events", "audit.log");
    public string VarsPath { get; set; } = Path.Combine("..", "Data", "vars.json");
    public string? ApiKey { get; set; }
    public bool AllowRemoteApi { get; set; }
    public PageOptions Pages { get; set; } = new();
    public SemanticSearchOptions SemanticSearch { get; set; } = new();
    public MaintenanceOptions Maintenance { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
    public SourceLinkOptions SourceLinks { get; set; } = new();
    public ChatOptions Chat { get; set; } = new();
}

public class PageOptions
{
    public bool AllowRawHtml { get; set; }
}

public class SemanticSearchOptions
{
    public bool EmbeddingsEnabled { get; set; } = true;
    public string ModelPath { get; set; } = Path.Combine("..", "Data", "Models", "embedding-model.onnx");
    public string VocabularyPath { get; set; } = Path.Combine("..", "Data", "Models", "vocab.txt");
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
    public string GitHubModel { get; set; } = "gpt-4o-mini";
    public string? GitHubCliPath { get; set; }
    public string? GitHubCliUrl { get; set; }
    public string GitHubTokenEnvironmentVariable { get; set; } = "GITHUB_TOKEN";
    public List<ChatModelOption> GitHubModels { get; set; } =
    [
        new() { Name = "gpt-4o-mini", IsPreferred = true, Description = "Fast GPT-4o mini option" },
        new() { Name = "gpt-5.1-mini", IsPreferred = true, Description = "GPT-5.1 mini option when available" },
        new() { Name = "gpt-4o", Description = "GPT-4o option when available" },
        new() { Name = "gpt-5", Description = "GPT-5 option when available" },
        new() { Name = "claude-sonnet-4.5", Description = "Claude Sonnet option when available" }
    ];
    public string SystemPromptPath { get; set; } = Path.Combine("Prompts", "wiki-chat-agent.md");
    public int RequestTimeoutSeconds { get; set; } = 600;
    public int MaxContextRecords { get; set; } = 5;
    public int MaxContextPages { get; set; } = 5;
    public int MaxHistoryMessages { get; set; } = 16;
    public int MaxAttachmentCharacters { get; set; } = 120000;
    public long MaxAttachmentBytes { get; set; } = 8 * 1024 * 1024;
    public bool AgentWritesEnabled { get; set; } = true;
}

public class ChatModelOption
{
    public string Name { get; set; } = string.Empty;
    public double? ChatMultiplier { get; set; }
    public bool IsPreferred { get; set; }
    public string? Description { get; set; }
}