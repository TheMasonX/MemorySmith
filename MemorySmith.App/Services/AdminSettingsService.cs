using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed class AdminSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly AuditLogService _audit;
    private readonly string _settingsPath;
    private readonly IReadOnlyList<EditableSettingDescriptor> _editableSettings;

    public AdminSettingsService(IOptionsMonitor<MemorySmithOptions> options, IConfiguration configuration, AuditLogService audit)
    {
        _options = options;
        _configuration = configuration;
        _audit = audit;
        var configuredSettingsPath = configuration["MemorySmith:SettingsOverridePath"];
        _settingsPath = string.IsNullOrWhiteSpace(configuredSettingsPath)
            ? Path.Combine(AppContext.BaseDirectory, "appsettings.LocalDevelopment.json")
            : Path.GetFullPath(configuredSettingsPath);
        _editableSettings = BuildEditableSettings();
    }

    public IReadOnlyList<AdminSettingItem> ListEditableSettings()
    {
        var settings = _options.CurrentValue;
        return _editableSettings
            .Select(descriptor => descriptor.ToItem(settings))
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AdminSettingUpdateResult> UpdateAsync(AdminSettingUpdateRequest request, CancellationToken cancellationToken)
    {
        var descriptor = _editableSettings.FirstOrDefault(item => string.Equals(item.Key, request.Key, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return new AdminSettingUpdateResult(false, "This setting cannot be edited from the admin UI.");
        }

        if (!descriptor.TryConvert(request.Value, out var convertedValue, out var error))
        {
            return new AdminSettingUpdateResult(false, error ?? "The setting value is invalid.");
        }

        JsonObject root;
        try
        {
            root = await LoadSettingsRootAsync(cancellationToken);
        }
        catch (JsonException)
        {
            return new AdminSettingUpdateResult(false, "The local settings file is not valid JSON.");
        }

        SetJsonValue(root, descriptor.Key.Split(':'), convertedValue);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var tempPath = _settingsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, root.ToJsonString(JsonOptions) + Environment.NewLine, cancellationToken);
        File.Move(tempPath, _settingsPath, overwrite: true);

        if (_configuration is IConfigurationRoot rootConfiguration)
        {
            rootConfiguration.Reload();
        }

        await _audit.RecordAsync(
            "settings.updated",
            "Setting",
            descriptor.Key,
            MemorySmithAuditOutcomes.Success,
            details: new { descriptor.Key, Value = descriptor.IsSensitive ? "Configured" : Convert.ToString(convertedValue, CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken);

        return new AdminSettingUpdateResult(true, null);
    }

    private async Task<JsonObject> LoadSettingsRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return new JsonObject();
        }

        await using var stream = File.OpenRead(_settingsPath);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        return node as JsonObject ?? new JsonObject();
    }

    private static void SetJsonValue(JsonObject root, IReadOnlyList<string> path, object value)
    {
        var current = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var segment = path[i];
            if (current[segment] is not JsonObject child)
            {
                child = new JsonObject();
                current[segment] = child;
            }

            current = child;
        }

        current[path[^1]] = value switch
        {
            bool boolean => JsonValue.Create(boolean),
            int integer => JsonValue.Create(integer),
            long longValue => JsonValue.Create(longValue),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }

    private static IReadOnlyList<EditableSettingDescriptor> BuildEditableSettings() =>
    [
        EditableSettingDescriptor.Choice("MemorySmith:Auth:AnonymousAccess", "Anonymous access", "Auth", settings => settings.Auth.AnonymousAccess, ["None", MemorySmithRoles.Viewer]),
        EditableSettingDescriptor.Choice("MemorySmith:Auth:AuthenticatedDefaultRole", "Default signed-in role", "Auth", settings => MemorySmithPermissionHandler.NormalizeAuthenticatedDefaultRole(settings.Auth.AuthenticatedDefaultRole), [MemorySmithRoles.Viewer, MemorySmithRoles.Editor]),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:AutoEditorForAuthenticatedUsers", "Auto editor for signed-in users", "Auth", settings => settings.Auth.AutoEditorForAuthenticatedUsers),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:OpenLocalEditorCompatibility", "Pre-setup local write compatibility", "Auth", settings => settings.Auth.OpenLocalEditorCompatibility),
        EditableSettingDescriptor.Boolean("MemorySmith:Pages:AllowRawHtml", "Allow raw page HTML", "Pages", settings => settings.Pages.AllowRawHtml),
        EditableSettingDescriptor.Boolean("MemorySmith:Maintenance:Enabled", "Maintenance enabled", "Maintenance", settings => settings.Maintenance.Enabled),
        EditableSettingDescriptor.Integer("MemorySmith:Maintenance:TriageMinutes", "Triage interval minutes", "Maintenance", settings => settings.Maintenance.TriageMinutes, 1, 1440),
        EditableSettingDescriptor.Integer("MemorySmith:Maintenance:IndexingMinutes", "Indexing interval minutes", "Maintenance", settings => settings.Maintenance.IndexingMinutes, 1, 1440),
        EditableSettingDescriptor.Integer("MemorySmith:Maintenance:ConsolidationHours", "Consolidation interval hours", "Maintenance", settings => settings.Maintenance.ConsolidationHours, 1, 720),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:ConfigPath", "Agent config path", "Maintenance agent", settings => settings.MaintenanceAgent.ConfigPath, 500),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:UseLlm", "Use LLM review", "Maintenance agent", settings => settings.MaintenanceAgent.UseLlm),
        EditableSettingDescriptor.Choice("MemorySmith:MaintenanceAgent:Provider", "Agent provider", "Maintenance agent", settings => settings.MaintenanceAgent.Provider, ["Ollama", "GitHub"]),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:OllamaEndpoint", "Agent Ollama endpoint", "Maintenance agent", settings => settings.MaintenanceAgent.OllamaEndpoint, 200),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:Model", "Agent model", "Maintenance agent", settings => settings.MaintenanceAgent.Model, 100),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:DirectWrite", "Allow direct agent writes", "Maintenance agent", settings => settings.MaintenanceAgent.DirectWrite),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Schedule:Enabled", "Weekly scheduler enabled", "Maintenance agent", settings => settings.MaintenanceAgent.Schedule.Enabled),
        EditableSettingDescriptor.Integer("MemorySmith:MaintenanceAgent:Schedule:WeeklyHourLocal", "Weekly run hour", "Maintenance agent", settings => settings.MaintenanceAgent.Schedule.WeeklyHourLocal, 0, 23),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:ResourceProbe:SkipWhenBusy", "Skip when busy", "Maintenance agent", settings => settings.MaintenanceAgent.ResourceProbe.SkipWhenBusy),
        EditableSettingDescriptor.Integer("MemorySmith:Limits:MaxPageSize", "Max page size", "Limits", settings => settings.Limits.MaxPageSize, 1, 1000),
        EditableSettingDescriptor.Integer("MemorySmith:Limits:MaxSearchLimit", "Max search limit", "Limits", settings => settings.Limits.MaxSearchLimit, 1, 1000),
        EditableSettingDescriptor.Integer("MemorySmith:Limits:MaxContentLength", "Max content length", "Limits", settings => settings.Limits.MaxContentLength, 1000, 250000),
        EditableSettingDescriptor.Boolean("MemorySmith:SemanticSearch:EmbeddingsEnabled", "Semantic embeddings enabled", "Search", settings => settings.SemanticSearch.EmbeddingsEnabled),
        EditableSettingDescriptor.Integer("MemorySmith:SemanticSearch:MaxInputTokens", "Max embedding input tokens", "Search", settings => settings.SemanticSearch.MaxInputTokens, 64, 4096),
        EditableSettingDescriptor.Integer("MemorySmith:SemanticSearch:MaxIndexedTextCharacters", "Max indexed text characters", "Search", settings => settings.SemanticSearch.MaxIndexedTextCharacters, 500, 50000),
        EditableSettingDescriptor.Boolean("MemorySmith:SourceLinks:AllowOpenWithDefaultApp", "Open source links with OS", "Source links", settings => settings.SourceLinks.AllowOpenWithDefaultApp),
        EditableSettingDescriptor.Integer("MemorySmith:SourceLinks:MaxReadBytes", "Max source read bytes", "Source links", settings => settings.SourceLinks.MaxReadBytes, 1024, 1048576),
        EditableSettingDescriptor.Choice("MemorySmith:Chat:Provider", "Default chat provider", "Chat", settings => settings.Chat.Provider, ["Ollama", "GitHubCopilot"]),
        EditableSettingDescriptor.String("MemorySmith:Chat:OllamaEndpoint", "Ollama endpoint", "Chat", settings => settings.Chat.OllamaEndpoint, 200),
        EditableSettingDescriptor.String("MemorySmith:Chat:OllamaModel", "Ollama model", "Chat", settings => settings.Chat.OllamaModel, 100),
        EditableSettingDescriptor.String("MemorySmith:Chat:GitHubModel", "GitHub model", "Chat", settings => settings.Chat.GitHubModel, 100),
        EditableSettingDescriptor.Boolean("MemorySmith:Chat:PreloadContextEnabled", "Preload chat context", "Chat", settings => settings.Chat.PreloadContextEnabled),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxPreloadedContextRecords", "Max preloaded memories", "Chat", settings => settings.Chat.MaxPreloadedContextRecords, 0, 25),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxPreloadedContextPages", "Max preloaded pages", "Chat", settings => settings.Chat.MaxPreloadedContextPages, 0, 25),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxContextItemCharacters", "Max context item characters", "Chat", settings => settings.Chat.MaxContextItemCharacters, 500, 50000),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxHistoryMessages", "Max history messages", "Chat", settings => settings.Chat.MaxHistoryMessages, 0, 200),
        EditableSettingDescriptor.Boolean("MemorySmith:Chat:ToolCallsEnabled", "Tool calls enabled", "Chat", settings => settings.Chat.ToolCallsEnabled),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxToolIterations", "Max tool iterations", "Chat", settings => settings.Chat.MaxToolIterations, 0, 10),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxToolCallsPerTurn", "Max tool calls per turn", "Chat", settings => settings.Chat.MaxToolCallsPerTurn, 0, 20),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxToolResultCharacters", "Max tool result characters", "Chat", settings => settings.Chat.MaxToolResultCharacters, 1000, 100000),
        EditableSettingDescriptor.Boolean("MemorySmith:Chat:AgentWritesEnabled", "Agent writes enabled", "Chat", settings => settings.Chat.AgentWritesEnabled)
    ];

    private sealed record EditableSettingDescriptor(
        string Key,
        string Label,
        string Category,
        AdminSettingValueKind ValueKind,
        Func<MemorySmithOptions, object?> GetValue,
        IReadOnlyList<string> Options,
        int? Min,
        int? Max,
        int? MaxLength,
        bool IsSensitive)
    {
        public AdminSettingItem ToItem(MemorySmithOptions settings)
        {
            var value = GetValue(settings);
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return new AdminSettingItem(Key, Label, Category, ValueKind.ToString(), text, text, Options, Min, Max, MaxLength);
        }

        public bool TryConvert(string? rawValue, out object value, out string? error)
        {
            value = string.Empty;
            error = null;
            rawValue ??= string.Empty;

            switch (ValueKind)
            {
                case AdminSettingValueKind.Boolean:
                    if (bool.TryParse(rawValue, out var boolean))
                    {
                        value = boolean;
                        return true;
                    }

                    error = "Use true or false.";
                    return false;
                case AdminSettingValueKind.Integer:
                    if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    {
                        error = "Use a whole number.";
                        return false;
                    }

                    if (Min.HasValue && integer < Min.Value || Max.HasValue && integer > Max.Value)
                    {
                        error = $"Use a value between {Min} and {Max}.";
                        return false;
                    }

                    value = integer;
                    return true;
                case AdminSettingValueKind.Choice:
                    var choice = Options.FirstOrDefault(option => string.Equals(option, rawValue, StringComparison.OrdinalIgnoreCase));
                    if (choice is null)
                    {
                        error = "Choose one of the allowed values.";
                        return false;
                    }

                    value = choice;
                    return true;
                default:
                    var text = rawValue.Trim();
                    if (MaxLength.HasValue && text.Length > MaxLength.Value)
                    {
                        error = $"Use {MaxLength.Value} characters or fewer.";
                        return false;
                    }

                    value = text;
                    return true;
            }
        }

        public static EditableSettingDescriptor Boolean(string key, string label, string category, Func<MemorySmithOptions, bool> getValue) =>
            new(key, label, category, AdminSettingValueKind.Boolean, settings => getValue(settings), [], null, null, null, false);

        public static EditableSettingDescriptor Integer(string key, string label, string category, Func<MemorySmithOptions, int> getValue, int min, int max) =>
            new(key, label, category, AdminSettingValueKind.Integer, settings => getValue(settings), [], min, max, null, false);

        public static EditableSettingDescriptor Choice(string key, string label, string category, Func<MemorySmithOptions, string> getValue, IReadOnlyList<string> options) =>
            new(key, label, category, AdminSettingValueKind.Choice, settings => getValue(settings), options, null, null, null, false);

        public static EditableSettingDescriptor String(string key, string label, string category, Func<MemorySmithOptions, string> getValue, int maxLength) =>
            new(key, label, category, AdminSettingValueKind.String, settings => getValue(settings), [], null, null, maxLength, false);
    }
}

public enum AdminSettingValueKind
{
    Boolean,
    Integer,
    String,
    Choice
}

public sealed record AdminSettingItem(
    string Key,
    string Label,
    string Category,
    string ValueKind,
    string Value,
    string DisplayValue,
    IReadOnlyList<string> Options,
    int? Min,
    int? Max,
    int? MaxLength);

public sealed record AdminSettingUpdateRequest(string Key, string? Value);

public sealed record AdminSettingUpdateResult(bool Succeeded, string? Error);