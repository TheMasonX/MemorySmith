using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed record ChatModelProfileView(
    string Id,
    string Name,
    string Provider,
    string Model,
    int? ContextWindowTokens,
    bool Enabled,
    bool IsDefault,
    bool IsMaintenanceRunDefault,
    bool IsProposalReviewDefault,
    bool IsAdminMaintenanceChatDefault,
    IReadOnlyList<string> AllowedRoles,
    string? Description,
    bool IsImplicit = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Provider} / {Model}" : $"{Name} - {Provider} / {Model}";
}

public sealed record ChatModelProfileUpsertRequest(
    string? Id,
    string Name,
    string Provider,
    string Model,
    int? ContextWindowTokens,
    bool Enabled,
    bool IsDefault,
    bool IsMaintenanceRunDefault,
    bool IsProposalReviewDefault,
    bool IsAdminMaintenanceChatDefault,
    IReadOnlyList<string>? AllowedRoles = null,
    string? Description = null);

public sealed record ChatModelProfileMutationResult(bool Succeeded, string? Error, ChatModelProfileView? Profile = null);

public sealed class ChatModelProfileService
{
    public const string NoDefaultProfileId = "__none__";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly IReadOnlyList<string> SupportedProviders = ["Ollama", "GitHub"];
    private static readonly IReadOnlyList<string> SupportedRoles = [MemorySmithRoles.Viewer, MemorySmithRoles.Editor, MemorySmithRoles.Admin];

    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly AuditLogService _audit;
    private readonly string _settingsPath;

    public ChatModelProfileService(IOptionsMonitor<MemorySmithOptions> options, IConfiguration configuration, AuditLogService audit)
    {
        _options = options;
        _configuration = configuration;
        _audit = audit;
        var configuredSettingsPath = configuration["MemorySmith:SettingsOverridePath"];
        _settingsPath = string.IsNullOrWhiteSpace(configuredSettingsPath)
            ? Path.Combine(AppContext.BaseDirectory, "appsettings.LocalDevelopment.json")
            : Path.GetFullPath(configuredSettingsPath);
    }

    public IReadOnlyList<string> ProviderOptions => SupportedProviders;
    public IReadOnlyList<string> RoleOptions => SupportedRoles;

    public IReadOnlyList<ChatModelProfileView> ListProfiles()
    {
        var chat = _options.CurrentValue.Chat;
        var maintenance = _options.CurrentValue.MaintenanceAgent;
        var explicitProfiles = chat.ModelProfiles
            .Select(profile => ToView(profile, chat.DefaultModelProfileId, maintenance.ModelProfileId, maintenance.ProposalReviewModelProfileId, maintenance.AdminChatModelProfileId))
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
            .OrderByDescending(profile => profile.IsDefault)
            .ThenByDescending(profile => profile.IsMaintenanceRunDefault || profile.IsProposalReviewDefault || profile.IsAdminMaintenanceChatDefault)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (explicitProfiles.Count > 0 || HasExplicitProfileConfiguration(chat))
        {
            return explicitProfiles;
        }

        return [CreateImplicitDefaultProfile(chat)];
    }

    public IReadOnlyList<ChatModelProfileView> ListEnabledProfilesForRoles(IEnumerable<string> roles) =>
        ListProfiles()
            .Where(profile => profile.Enabled && IsProfileAllowedForRoles(profile, roles))
            .ToList();

    public ChatModelProfileView? GetDefaultProfileForRoles(IEnumerable<string> roles)
    {
        var profiles = ListEnabledProfilesForRoles(roles);
        return profiles.FirstOrDefault(profile => profile.IsDefault);
    }

    public async Task<ChatModelProfileMutationResult> UpsertAsync(ChatModelProfileUpsertRequest request, CancellationToken cancellationToken)
    {
        if (!TryNormalizeRequest(request, out var normalized, out var error))
        {
            return new ChatModelProfileMutationResult(false, error);
        }

        var profiles = _options.CurrentValue.Chat.ModelProfiles
            .Select(CloneProfile)
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
            .ToList();
        var existingIndex = profiles.FindIndex(profile => string.Equals(profile.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            profiles[existingIndex] = normalized;
        }
        else
        {
            normalized.Id = EnsureUniqueId(normalized.Id, profiles.Select(profile => profile.Id));
            profiles.Add(normalized);
        }

        var defaultId = _options.CurrentValue.Chat.DefaultModelProfileId;
        if (request.IsDefault)
        {
            if (!normalized.Enabled)
            {
                return new ChatModelProfileMutationResult(false, "Only enabled profiles can be the chat default.");
            }

            defaultId = normalized.Id;
        }
        else if (string.Equals(defaultId, normalized.Id, StringComparison.OrdinalIgnoreCase) && !normalized.Enabled)
        {
            defaultId = NoDefaultProfileId;
        }

        var maintenance = _options.CurrentValue.MaintenanceAgent;
        var maintenanceRunProfileId = UpdateAssignment(maintenance.ModelProfileId, normalized.Id, request.IsMaintenanceRunDefault, normalized.Enabled, "maintenance runs", out error);
        if (error is not null)
        {
            return new ChatModelProfileMutationResult(false, error);
        }

        var proposalReviewProfileId = UpdateAssignment(maintenance.ProposalReviewModelProfileId, normalized.Id, request.IsProposalReviewDefault, normalized.Enabled, "proposal reviews", out error);
        if (error is not null)
        {
            return new ChatModelProfileMutationResult(false, error);
        }

        var adminChatProfileId = UpdateAssignment(maintenance.AdminChatModelProfileId, normalized.Id, request.IsAdminMaintenanceChatDefault, normalized.Enabled, "admin maintenance chat", out error);
        if (error is not null)
        {
            return new ChatModelProfileMutationResult(false, error);
        }

        await SaveProfilesAsync(profiles, defaultId, maintenanceRunProfileId, proposalReviewProfileId, adminChatProfileId, "chat.model_profile.saved", normalized.Id, cancellationToken);
        return new ChatModelProfileMutationResult(true, null, ToView(normalized, defaultId, maintenanceRunProfileId, proposalReviewProfileId, adminChatProfileId));
    }

    public async Task<ChatModelProfileMutationResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeId(id);
        var profiles = _options.CurrentValue.Chat.ModelProfiles
            .Select(CloneProfile)
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id) && !string.Equals(profile.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (profiles.Count == _options.CurrentValue.Chat.ModelProfiles.Count)
        {
            return new ChatModelProfileMutationResult(false, "Model profile was not found.");
        }

        var defaultId = string.Equals(_options.CurrentValue.Chat.DefaultModelProfileId, normalizedId, StringComparison.OrdinalIgnoreCase)
            ? NoDefaultProfileId
            : _options.CurrentValue.Chat.DefaultModelProfileId;
        if (profiles.Count == 0)
        {
            defaultId = NoDefaultProfileId;
        }

        var maintenance = _options.CurrentValue.MaintenanceAgent;
        var maintenanceRunProfileId = ClearAssignment(maintenance.ModelProfileId, normalizedId);
        var proposalReviewProfileId = ClearAssignment(maintenance.ProposalReviewModelProfileId, normalizedId);
        var adminChatProfileId = ClearAssignment(maintenance.AdminChatModelProfileId, normalizedId);

        await SaveProfilesAsync(profiles, defaultId, maintenanceRunProfileId, proposalReviewProfileId, adminChatProfileId, "chat.model_profile.deleted", normalizedId, cancellationToken);
        return new ChatModelProfileMutationResult(true, null);
    }

    public static bool IsProfileAllowedForRoles(ChatModelProfileView profile, IEnumerable<string> roles)
    {
        var roleSet = roles.Where(role => !string.IsNullOrWhiteSpace(role)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roleSet.Contains(MemorySmithRoles.Admin) || profile.AllowedRoles.Count == 0)
        {
            return true;
        }

        return profile.AllowedRoles.Any(role => roleSet.Contains(role));
    }

    private async Task SaveProfilesAsync(
        IReadOnlyList<ChatModelProfileOptions> profiles,
        string? defaultId,
        string? maintenanceRunProfileId,
        string? proposalReviewProfileId,
        string? adminChatProfileId,
        string action,
        string targetId,
        CancellationToken cancellationToken)
    {
        JsonObject root;
        try
        {
            root = await LoadSettingsRootAsync(cancellationToken);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The local settings file is not valid JSON.");
        }

        var memorySmith = GetOrCreateObject(root, "MemorySmith");
        var chat = GetOrCreateObject(memorySmith, "Chat");
        chat["DefaultModelProfileId"] = defaultId ?? string.Empty;
        chat["ModelProfiles"] = JsonSerializer.SerializeToNode(profiles, JsonOptions);
        var maintenanceAgent = GetOrCreateObject(memorySmith, "MaintenanceAgent");
        maintenanceAgent["ModelProfileId"] = maintenanceRunProfileId ?? string.Empty;
        maintenanceAgent["ProposalReviewModelProfileId"] = proposalReviewProfileId ?? string.Empty;
        maintenanceAgent["AdminChatModelProfileId"] = adminChatProfileId ?? string.Empty;

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var tempPath = _settingsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, root.ToJsonString(JsonOptions) + Environment.NewLine, cancellationToken);
        File.Move(tempPath, _settingsPath, overwrite: true);

        if (_configuration is IConfigurationRoot rootConfiguration)
        {
            rootConfiguration.Reload();
        }

        await _audit.RecordAsync(
            action,
            "ChatModelProfile",
            targetId,
            MemorySmithAuditOutcomes.Success,
                details: new { targetId, defaultId, maintenanceRunProfileId, proposalReviewProfileId, adminChatProfileId, profileCount = profiles.Count },
            cancellationToken: cancellationToken);
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

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject child)
        {
            return child;
        }

        child = new JsonObject();
        parent[key] = child;
        return child;
    }

    private static bool HasExplicitProfileConfiguration(ChatOptions chat) =>
        chat.ModelProfiles.Count > 0 || !string.IsNullOrWhiteSpace(chat.DefaultModelProfileId);

    private static ChatModelProfileView CreateImplicitDefaultProfile(ChatOptions chat)
    {
        var provider = NormalizeProvider(chat.Provider);
        var model = ProviderMatches(provider, "GitHub") ? chat.GitHubModel : chat.OllamaModel;
        return new ChatModelProfileView(
            "legacy-default",
            string.IsNullOrWhiteSpace(model) ? "Default chat model" : model,
            provider,
            model,
            ProviderMatches(provider, "Ollama") ? chat.OllamaContextWindowTokens : null,
            Enabled: !string.IsNullOrWhiteSpace(model),
            IsDefault: !string.IsNullOrWhiteSpace(model),
                IsMaintenanceRunDefault: false,
                IsProposalReviewDefault: false,
                IsAdminMaintenanceChatDefault: false,
            AllowedRoles: [],
            Description: "Derived from existing Chat provider/model settings.",
            IsImplicit: true);
    }

            private static ChatModelProfileView ToView(ChatModelProfileOptions profile, string? defaultId, string? maintenanceRunProfileId, string? proposalReviewProfileId, string? adminChatProfileId)
    {
        var id = NormalizeId(profile.Id);
        var provider = NormalizeProvider(profile.Provider);
        return new ChatModelProfileView(
            id,
            string.IsNullOrWhiteSpace(profile.Name) ? profile.Model.Trim() : profile.Name.Trim(),
            provider,
            profile.Model.Trim(),
            profile.ContextWindowTokens,
            profile.Enabled,
            string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
            string.Equals(id, maintenanceRunProfileId, StringComparison.OrdinalIgnoreCase),
            string.Equals(id, proposalReviewProfileId, StringComparison.OrdinalIgnoreCase),
            string.Equals(id, adminChatProfileId, StringComparison.OrdinalIgnoreCase),
            NormalizeRoles(profile.AllowedRoles),
            string.IsNullOrWhiteSpace(profile.Description) ? null : profile.Description.Trim());
    }

    private static string? UpdateAssignment(string? currentId, string profileId, bool shouldAssign, bool profileEnabled, string label, out string? error)
    {
        error = null;
        if (shouldAssign)
        {
            if (!profileEnabled)
            {
                error = $"Only enabled profiles can be assigned to {label}.";
                return currentId;
            }

            return profileId;
        }

        return string.Equals(currentId, profileId, StringComparison.OrdinalIgnoreCase) ? string.Empty : currentId;
    }

    private static string? ClearAssignment(string? currentId, string deletedId) =>
        string.Equals(currentId, deletedId, StringComparison.OrdinalIgnoreCase) ? string.Empty : currentId;

    private static bool TryNormalizeRequest(ChatModelProfileUpsertRequest request, out ChatModelProfileOptions profile, out string? error)
    {
        profile = new ChatModelProfileOptions();
        error = null;
        var name = request.Name.Trim();
        var model = request.Model.Trim();
        var provider = NormalizeProvider(request.Provider);
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Profile name is required.";
            return false;
        }

        if (!SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            error = "Choose a supported provider.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            error = "Model id is required.";
            return false;
        }

        if (request.ContextWindowTokens is < 512 or > 262144)
        {
            error = "Context window must be between 512 and 262144 tokens, or blank.";
            return false;
        }

        var roles = NormalizeRoles(request.AllowedRoles ?? []);
        var unsupportedRole = roles.FirstOrDefault(role => !SupportedRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
        if (unsupportedRole is not null)
        {
            error = $"Unsupported role '{unsupportedRole}'.";
            return false;
        }

        profile = new ChatModelProfileOptions
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? CreateId(name) : NormalizeId(request.Id),
            Name = name,
            Provider = provider,
            Model = model,
            ContextWindowTokens = request.ContextWindowTokens,
            Enabled = request.Enabled,
            AllowedRoles = roles.ToList(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()
        };
        return true;
    }

    private static ChatModelProfileOptions CloneProfile(ChatModelProfileOptions profile) => new()
    {
        Id = NormalizeId(profile.Id),
        Name = profile.Name.Trim(),
        Provider = NormalizeProvider(profile.Provider),
        Model = profile.Model.Trim(),
        ContextWindowTokens = profile.ContextWindowTokens,
        Enabled = profile.Enabled,
        AllowedRoles = NormalizeRoles(profile.AllowedRoles).ToList(),
        Description = string.IsNullOrWhiteSpace(profile.Description) ? null : profile.Description.Trim()
    };

    private static string NormalizeProvider(string provider) =>
        ProviderMatches(provider, "GitHub") ? "GitHub" : "Ollama";

    private static bool ProviderMatches(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(left, "GitHub", StringComparison.OrdinalIgnoreCase) && string.Equals(right, "Copilot", StringComparison.OrdinalIgnoreCase)) ||
        (string.Equals(left, "GitHubCopilot", StringComparison.OrdinalIgnoreCase) && string.Equals(right, "GitHub", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> NormalizeRoles(IEnumerable<string> roles) => roles
        .Where(role => !string.IsNullOrWhiteSpace(role))
        .Select(role => SupportedRoles.FirstOrDefault(supported => string.Equals(supported, role.Trim(), StringComparison.OrdinalIgnoreCase)) ?? role.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(role => Array.IndexOf(SupportedRoles.ToArray(), role))
        .ToList();

    private static string EnsureUniqueId(string id, IEnumerable<string> existingIds)
    {
        var existing = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(id))
        {
            return id;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = FormattableString.Invariant($"{id}-{suffix}");
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return id + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    private static string CreateId(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        var chars = text.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var id = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    }

    private static string NormalizeId(string value) => CreateId(value);
}