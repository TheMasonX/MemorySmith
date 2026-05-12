namespace MemorySmith.App.Services;

public class MemorySmithOptions
{
    public string DataPath { get; set; } = Path.Combine("..", "Data", "Memories");
    public string EventLogPath { get; set; } = Path.Combine("..", "Data", "Events", "audit.log");
    public string VarsPath { get; set; } = Path.Combine("..", "Data", "vars.json");
    public string? ApiKey { get; set; }
    public bool AllowRemoteApi { get; set; }
    public MaintenanceOptions Maintenance { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
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