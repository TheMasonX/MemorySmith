namespace MemorySmith.Storage;

public sealed class DatabaseOptions
{
    public string Provider { get; set; } = "SQLite";
    public string ConnectionString { get; set; } = "Data Source=../Data/memorysmith.db";
    public bool ApplyMigrationsOnStartup { get; set; } = true;
    public bool UseWal { get; set; } = true;
    public int BusyTimeoutSeconds { get; set; } = 30;
}
