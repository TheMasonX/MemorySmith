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
    IReadOnlyList<OperationalWarning> Warnings,
    EmbeddingProviderStatus SemanticEmbeddings,
    StorageDiagnosticsSnapshot StorageDiagnostics);

public sealed record EffectiveMemorySmithConfiguration(
    string DataPath,
    string PagesPath,
    string EventLogPath,
    string VarsPath,
    TagPolicyLoadStatus TagPolicy,
    bool ApiKeyConfigured,
    bool AllowRemoteApi,
    string WindowsServiceName,
    IReadOnlyList<string> ConfiguredUrls,
    MaintenanceOptions Maintenance,
    LimitOptions Limits,
    SourceLinkOptions SourceLinks,
    SemanticSearchOptions SemanticSearch,
    ChatOptions Chat,
    TelemetryOptions Telemetry);

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

public sealed record OperationalWarning(string Code, string Severity, string Message);

public class OperationalDiagnosticsService
{
    private readonly IOptions<MemorySmithOptions> _options;
    private readonly StorageDiagnostics _storageDiagnostics;
    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly TagPolicyService _tagPolicy;

    public OperationalDiagnosticsService(
        IOptions<MemorySmithOptions> options,
        StorageDiagnostics storageDiagnostics,
        ITextEmbeddingProvider embeddingProvider,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        TagPolicyService tagPolicy)
    {
        _options = options;
        _storageDiagnostics = storageDiagnostics;
        _embeddingProvider = embeddingProvider;
        _environment = environment;
        _configuration = configuration;
        _tagPolicy = tagPolicy;
    }

    public OperationalDiagnosticsSnapshot GetSnapshot()
    {
        var settings = _options.Value;
        var dataPath = GetFullPath(settings.DataPath);
        var pagesPath = GetFullPath(settings.PagesPath);
        var eventLogPath = GetFullPath(settings.EventLogPath);
        var varsPath = GetFullPath(settings.VarsPath);
        var tagPolicyStatus = _tagPolicy.GetLoadStatus();
        var sourceLinks = new SourceLinkOptions
        {
            MaxReadBytes = settings.SourceLinks.MaxReadBytes,
            AllowUnrestrictedSourceReads = settings.SourceLinks.AllowUnrestrictedSourceReads,
            ReadContextLinesBefore = settings.SourceLinks.ReadContextLinesBefore,
            ReadContextLinesAfter = settings.SourceLinks.ReadContextLinesAfter,
            AllowOpenWithDefaultApp = settings.SourceLinks.AllowOpenWithDefaultApp,
            AllowedFileRootVariables = settings.SourceLinks.AllowedFileRootVariables.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            AllowedFileRoots = settings.SourceLinks.AllowedFileRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DeniedFileRootVariables = settings.SourceLinks.DeniedFileRootVariables.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DeniedFileRoots = settings.SourceLinks.DeniedFileRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        return new OperationalDiagnosticsSnapshot(
            DateTime.UtcNow,
            _environment.EnvironmentName,
            _environment.ContentRootPath,
            AppContext.BaseDirectory,
            new EffectiveMemorySmithConfiguration(
                dataPath,
                pagesPath,
                eventLogPath,
                varsPath,
                tagPolicyStatus,
                !string.IsNullOrWhiteSpace(settings.ApiKey),
                settings.AllowRemoteApi,
                _configuration["MemorySmith:WindowsService:Name"] ?? WindowsServiceCommands.DefaultServiceName,
                GetConfiguredUrls(),
                settings.Maintenance,
                settings.Limits,
                sourceLinks,
                settings.SemanticSearch,
                settings.Chat,
                settings.Telemetry),
            [
                GetDirectoryStatus("Memory data", dataPath, "*.json"),
                GetDirectoryStatus("Pages", pagesPath, "*.md"),
                GetFileStatus("Event log", eventLogPath),
                GetFileStatus("Variables", varsPath)
            ],
            GetEndpoints(),
            GetWarnings(settings, tagPolicyStatus),
                _embeddingProvider.GetStatus(),
            _storageDiagnostics.GetSnapshot());
    }

    private static List<OperationalWarning> GetWarnings(MemorySmithOptions settings, TagPolicyLoadStatus tagPolicyStatus)
    {
        var warnings = new List<OperationalWarning>();
        if (settings.AllowRemoteApi && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            warnings.Add(new OperationalWarning(
                "remote-api-without-api-key",
                "High",
                "Remote API access is enabled without MemorySmith:ApiKey. Non-loopback API/MCP requests are blocked until MemorySmith:ApiKey is configured."));
        }

            if (tagPolicyStatus.UsingFallback && tagPolicyStatus.Reason != "missing")
            {
                warnings.Add(new OperationalWarning(
                "tag-policy-load-failed",
                "Medium",
                tagPolicyStatus.Message));
            }

        return warnings;
    }

    private string[] GetConfiguredUrls()
    {
        var configured = _configuration["urls"] ?? _configuration["ASPNETCORE_URLS"];
        return string.IsNullOrWhiteSpace(configured)
            ? []
            : configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyList<EndpointInfo> GetEndpoints() =>
    [
        new("Blazor workbench", "/memories", "Memory browsing, search, and editing"),
        new("Pages", "/pages", "Markdown-backed persistent pages"),
        new("Chat", "/chat", "Memory-enhanced chat and agent mode"),
        new("Health", "/health", "Stats, maintenance telemetry, and operational diagnostics"),
        new("Variables", "/variables", "Source-link path variable management"),
        new("REST memories", "/api/memories", "Memory CRUD and search API"),
        new("REST pages", "/api/pages", "Markdown page CRUD and search API"),
        new("Combined search", "/api/search", "Combined memory and page search API"),
        new("Chat API", "/api/chat", "Provider-backed chat and agent API"),
        new("Chat config", "/api/chat/config", "Current chat provider, default model, and provider model discovery"),
        new("Stats", "/api/stats", "Counts, activity, and maintenance telemetry"),
        new("Diagnostics", "/api/diagnostics", "Redacted runtime configuration and storage diagnostics"),
        new("Diagnostic logs", "/api/diagnostics/logs", "Structured application log search with optional Windows Event Log source"),
        new("Diagnostic log metrics", "/api/diagnostics/logs/metrics", "Log-derived error, warning, request, and latency trend metrics"),
        new("MCP", "/mcp", "HTTP JSON-RPC MCP tools")
    ];

    private static StoragePathStatus GetDirectoryStatus(string name, string path, string searchPattern)
    {
        try
        {
            var exists = Directory.Exists(path);
            return new StoragePathStatus(
                name,
                path,
                "Directory",
                exists,
                exists ? Directory.EnumerateFiles(path, searchPattern, SearchOption.AllDirectories).LongCount() : null,
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