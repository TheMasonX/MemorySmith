namespace MemorySmith.App.Services;

public class MemorySmithOptions
{
    public string DataPath { get; set; } = Path.Combine("..", "Data", "Memories");
    public string PagesPath { get; set; } = Path.Combine("..", "Data", "Pages");
    public string EventLogPath { get; set; } = Path.Combine("..", "Data", "Events", "audit.log");
    public string VarsPath { get; set; } = Path.Combine("..", "Data", "vars.json");
    public string? ApiKey { get; set; }
    public bool AllowRemoteApi { get; set; }
    public MaintenanceOptions Maintenance { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
    public SourceLinkOptions SourceLinks { get; set; } = new();
    public ChatOptions Chat { get; set; } = new();
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
    public string OllamaModel { get; set; } = "llama3.1";
    public string SystemPromptPath { get; set; } = Path.Combine("Prompts", "wiki-chat-agent.md");
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int MaxContextRecords { get; set; } = 5;
    public int MaxContextPages { get; set; } = 5;
    public int MaxHistoryMessages { get; set; } = 16;
    public int MaxAttachmentCharacters { get; set; } = 120000;
    public bool AgentWritesEnabled { get; set; } = true;
}