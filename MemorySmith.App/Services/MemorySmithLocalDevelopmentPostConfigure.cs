using System.Text.Json;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed class MemorySmithLocalDevelopmentPostConfigure : IPostConfigureOptions<MemorySmithOptions>
{
    private readonly IHostEnvironment _environment;
    private readonly string _settingsOverridePath;

    public MemorySmithLocalDevelopmentPostConfigure(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _settingsOverridePath = MemorySmithConfigurationPaths.ResolveSettingsOverridePath(configuration["MemorySmith:SettingsOverridePath"]);
    }

    public void PostConfigure(string? name, MemorySmithOptions options)
    {
        var overrides = LoadOverrideKeys();
        if (!string.IsNullOrWhiteSpace(options.SecurityProfile))
        {
            ApplySecurityProfile(options, overrides);
        }

        if (!string.Equals(_environment.EnvironmentName, "LocalDevelopment", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyIfMissing(overrides, "MemorySmith:AllowRemoteApi", () => options.AllowRemoteApi = true);
        ApplyIfMissing(overrides, "MemorySmith:Auth:RequireHttpsForRemoteAuth", () => options.Auth.RequireHttpsForRemoteAuth = false);
        ApplyIfMissing(overrides, "MemorySmith:Auth:OpenLocalEditorCompatibility", () => options.Auth.OpenLocalEditorCompatibility = false);
        ApplyIfMissing(overrides, "MemorySmith:Auth:RateLimits:LoginPermitLimit", () => options.Auth.RateLimits.LoginPermitLimit = 1000);
        ApplyIfMissing(overrides, "MemorySmith:Auth:RateLimits:LoginWindowMinutes", () => options.Auth.RateLimits.LoginWindowMinutes = 1);
        ApplyIfMissing(overrides, "MemorySmith:Auth:RateLimits:LockoutMinutes", () => options.Auth.RateLimits.LockoutMinutes = 1);
        ApplyIfMissing(overrides, "MemorySmith:Auth:RateLimits:MaxProgressiveLockoutMinutes", () => options.Auth.RateLimits.MaxProgressiveLockoutMinutes = 1);
        ApplyIfMissing(overrides, "MemorySmith:Pages:AllowRawHtml", () => options.Pages.AllowRawHtml = true);
        ApplyIfMissing(overrides, "MemorySmith:Markdown:MermaidEnabled", () => options.Markdown.MermaidEnabled = true);
        ApplyIfMissing(overrides, "MemorySmith:Markdown:MermaidRestrictionMode", () => options.Markdown.MermaidRestrictionMode = MermaidRestrictionModes.Standard);
        ApplyIfMissing(overrides, "MemorySmith:Blazor:MaximumReceiveMessageSizeBytes", () => options.Blazor.MaximumReceiveMessageSizeBytes = 4 * 1024 * 1024);
        ApplyIfMissing(overrides, "MemorySmith:Chat:RequestTimeoutSeconds", () => options.Chat.RequestTimeoutSeconds = 900);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxContextRecords", () => options.Chat.MaxContextRecords = 12);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxContextPages", () => options.Chat.MaxContextPages = 12);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxContextItemCharacters", () => options.Chat.MaxContextItemCharacters = 8000);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxHistoryMessages", () => options.Chat.MaxHistoryMessages = 40);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxAttachmentCharacters", () => options.Chat.MaxAttachmentCharacters = 250000);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxAttachmentBytes", () => options.Chat.MaxAttachmentBytes = 32 * 1024 * 1024);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxToolIterations", () => options.Chat.MaxToolIterations = 4);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxToolCallsPerTurn", () => options.Chat.MaxToolCallsPerTurn = 8);
        ApplyIfMissing(overrides, "MemorySmith:Chat:MaxToolResultCharacters", () => options.Chat.MaxToolResultCharacters = 30000);
        ApplyIfMissing(overrides, "MemorySmith:Chat:AgentWritesEnabled", () => options.Chat.AgentWritesEnabled = true);
        ApplyIfMissing(overrides, "MemorySmith:Limits:MaxPageSize", () => options.Limits.MaxPageSize = 500);
        ApplyIfMissing(overrides, "MemorySmith:Limits:MaxSearchLimit", () => options.Limits.MaxSearchLimit = 500);
        ApplyIfMissing(overrides, "MemorySmith:Limits:MaxContentLength", () => options.Limits.MaxContentLength = 100000);
        ApplyIfMissing(overrides, "MemorySmith:Limits:MaxTags", () => options.Limits.MaxTags = 100);
        ApplyIfMissing(overrides, "MemorySmith:Limits:MaxReferences", () => options.Limits.MaxReferences = 500);
        ApplyIfMissing(overrides, "MemorySmith:SourceLinks:MaxReadBytes", () => options.SourceLinks.MaxReadBytes = 262144);
    }

    private static void ApplySecurityProfile(MemorySmithOptions options, IReadOnlySet<string> overrides)
    {
        var profile = MemorySmithSecurityProfiles.Normalize(options.SecurityProfile);
        options.SecurityProfile = profile;

        switch (profile)
        {
            case MemorySmithSecurityProfiles.LocalDev:
                ApplyIfMissing(overrides, "MemorySmith:AllowRemoteApi", () => options.AllowRemoteApi = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:RequireHttpsForRemoteAuth", () => options.Auth.RequireHttpsForRemoteAuth = false);
                ApplyIfMissing(overrides, "MemorySmith:Auth:AnonymousAccess", () => options.Auth.AnonymousAccess = MemorySmithRoles.Viewer);
                ApplyIfMissing(overrides, "MemorySmith:Auth:AutoEditorForAuthenticatedUsers", () => options.Auth.AutoEditorForAuthenticatedUsers = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:Setup:AllowLoopbackBootstrap", () => options.Auth.Setup.AllowLoopbackBootstrap = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:OpenLocalEditorCompatibility", () => options.Auth.OpenLocalEditorCompatibility = true);
                ApplyIfMissing(overrides, "MemorySmith:Markdown:MermaidEnabled", () => options.Markdown.MermaidEnabled = true);
                ApplyIfMissing(overrides, "MemorySmith:Markdown:MermaidRestrictionMode", () => options.Markdown.MermaidRestrictionMode = MermaidRestrictionModes.Standard);
                break;
            case MemorySmithSecurityProfiles.RemoteHardened:
                ApplyIfMissing(overrides, "MemorySmith:AllowRemoteApi", () => options.AllowRemoteApi = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:Enabled", () => options.Auth.Enabled = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:RequireHttpsForRemoteAuth", () => options.Auth.RequireHttpsForRemoteAuth = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:AnonymousAccess", () => options.Auth.AnonymousAccess = "None");
                ApplyIfMissing(overrides, "MemorySmith:Auth:AutoEditorForAuthenticatedUsers", () => options.Auth.AutoEditorForAuthenticatedUsers = false);
                ApplyIfMissing(overrides, "MemorySmith:Auth:Setup:AllowLoopbackBootstrap", () => options.Auth.Setup.AllowLoopbackBootstrap = false);
                ApplyIfMissing(overrides, "MemorySmith:Auth:OpenLocalEditorCompatibility", () => options.Auth.OpenLocalEditorCompatibility = false);
                ApplyIfMissing(overrides, "MemorySmith:Markdown:MermaidEnabled", () => options.Markdown.MermaidEnabled = true);
                ApplyIfMissing(overrides, "MemorySmith:Markdown:MermaidRestrictionMode", () => options.Markdown.MermaidRestrictionMode = MermaidRestrictionModes.Strict);
                break;
            default:
                ApplyIfMissing(overrides, "MemorySmith:AllowRemoteApi", () => options.AllowRemoteApi = false);
                ApplyIfMissing(overrides, "MemorySmith:Auth:Enabled", () => options.Auth.Enabled = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:RequireHttpsForRemoteAuth", () => options.Auth.RequireHttpsForRemoteAuth = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:AnonymousAccess", () => options.Auth.AnonymousAccess = MemorySmithRoles.Viewer);
                ApplyIfMissing(overrides, "MemorySmith:Auth:AutoEditorForAuthenticatedUsers", () => options.Auth.AutoEditorForAuthenticatedUsers = false);
                ApplyIfMissing(overrides, "MemorySmith:Auth:Setup:AllowLoopbackBootstrap", () => options.Auth.Setup.AllowLoopbackBootstrap = true);
                ApplyIfMissing(overrides, "MemorySmith:Auth:OpenLocalEditorCompatibility", () => options.Auth.OpenLocalEditorCompatibility = true);
                ApplyIfMissing(overrides, "MemorySmith:Markdown:MermaidEnabled", () => options.Markdown.MermaidEnabled = true);
                ApplyIfMissing(overrides, "MemorySmith:Markdown:MermaidRestrictionMode", () => options.Markdown.MermaidRestrictionMode = MermaidRestrictionModes.Restricted);
                break;
        }
    }

    private static void ApplyIfMissing(IReadOnlySet<string> overrides, string key, Action apply)
    {
        if (!overrides.Contains(key))
        {
            apply();
        }
    }

    private HashSet<string> LoadOverrideKeys()
    {
        if (!File.Exists(_settingsOverridePath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var stream = File.OpenRead(_settingsOverridePath);
            using var document = JsonDocument.Parse(stream);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectKeys(document.RootElement, string.Empty, keys);
            return keys;
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void CollectKeys(JsonElement element, string prefix, ISet<string> keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var nextPrefix = string.IsNullOrWhiteSpace(prefix)
                ? property.Name
                : prefix + ":" + property.Name;

            keys.Add(nextPrefix);

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                CollectKeys(property.Value, nextPrefix, keys);
            }
        }
    }
}