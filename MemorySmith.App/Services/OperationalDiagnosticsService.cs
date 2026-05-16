using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed record OperationalDiagnosticsSnapshot(
    DateTime ObservedAtUtc,
    string EnvironmentName,
    string ContentRootPath,
    string BaseDirectory,
    EffectiveMemorySmithConfiguration Configuration,
    IReadOnlyList<StoragePathStatus> Paths,
    IReadOnlyList<EndpointInfo> Endpoints,
    StorageDiagnosticsSnapshot StorageDiagnostics);

public sealed record EffectiveMemorySmithConfiguration(
    string DataPath,
    string EventLogPath,
    string VarsPath,
    bool ApiKeyConfigured,
    bool AllowRemoteApi,
    string WindowsServiceName,
    IReadOnlyList<string> ConfiguredUrls,
    MaintenanceOptions Maintenance,
    LimitOptions Limits,
    SourceLinkOptions SourceLinks);

public sealed record StoragePathStatus(
    string Name,
    string Path,
    string Kind,
    bool Exists,
    long? FileCount,
    long? SizeBytes,
    long? AvailableFreeBytes,
    string? Error);

public sealed record EndpointInfo(string Name, string Path, string Description);

public class OperationalDiagnosticsService
{
    private readonly IOptions<MemorySmithOptions> _options;
    private readonly StorageDiagnostics _storageDiagnostics;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public OperationalDiagnosticsService(
        IOptions<MemorySmithOptions> options,
        StorageDiagnostics storageDiagnostics,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _options = options;
        _storageDiagnostics = storageDiagnostics;
        _environment = environment;
        _configuration = configuration;
    }

    public OperationalDiagnosticsSnapshot GetSnapshot()
    {
        var settings = _options.Value;
        var dataPath = GetFullPath(settings.DataPath);
        var eventLogPath = GetFullPath(settings.EventLogPath);
        var varsPath = GetFullPath(settings.VarsPath);

        return new OperationalDiagnosticsSnapshot(
            DateTime.UtcNow,
            _environment.EnvironmentName,
            _environment.ContentRootPath,
            AppContext.BaseDirectory,
            new EffectiveMemorySmithConfiguration(
                dataPath,
                eventLogPath,
                varsPath,
                !string.IsNullOrWhiteSpace(settings.ApiKey),
                settings.AllowRemoteApi,
                _configuration["MemorySmith:WindowsService:Name"] ?? WindowsServiceCommands.DefaultServiceName,
                GetConfiguredUrls(),
                settings.Maintenance,
                settings.Limits,
                settings.SourceLinks),
            [
                GetDirectoryStatus("Memory data", dataPath),
                GetFileStatus("Event log", eventLogPath),
                GetFileStatus("Variables", varsPath)
            ],
            GetEndpoints(),
            _storageDiagnostics.GetSnapshot());
    }

    private IReadOnlyList<string> GetConfiguredUrls()
    {
        var configured = _configuration["urls"] ?? _configuration["ASPNETCORE_URLS"];
        return string.IsNullOrWhiteSpace(configured)
            ? []
            : configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyList<EndpointInfo> GetEndpoints() =>
    [
        new("Blazor workbench", "/memories", "Memory browsing, search, and editing"),
        new("Health", "/health", "Stats, maintenance telemetry, and operational diagnostics"),
        new("Variables", "/variables", "Source-link path variable management"),
        new("REST memories", "/api/memories", "Memory CRUD and search API"),
        new("Stats", "/api/stats", "Counts, activity, and maintenance telemetry"),
        new("Diagnostics", "/api/diagnostics", "Redacted runtime configuration and storage diagnostics"),
        new("MCP", "/mcp", "HTTP JSON-RPC MCP tools")
    ];

    private static StoragePathStatus GetDirectoryStatus(string name, string path)
    {
        try
        {
            var exists = Directory.Exists(path);
            return new StoragePathStatus(
                name,
                path,
                "Directory",
                exists,
                exists ? Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories).LongCount() : null,
                null,
                GetAvailableFreeBytes(path),
                null);
        }
        catch (Exception ex)
        {
            return new StoragePathStatus(name, path, "Directory", false, null, null, null, ex.Message);
        }
    }

    private static StoragePathStatus GetFileStatus(string name, string path)
    {
        try
        {
            var file = new FileInfo(path);
            return new StoragePathStatus(
                name,
                path,
                "File",
                file.Exists,
                null,
                file.Exists ? file.Length : null,
                GetAvailableFreeBytes(Path.GetDirectoryName(path) ?? path),
                null);
        }
        catch (Exception ex)
        {
            return new StoragePathStatus(name, path, "File", false, null, null, null, ex.Message);
        }
    }

    private static long? GetAvailableFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    private static string GetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}