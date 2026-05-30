namespace MemorySmith.App.Services;

public static class MemorySmithConfigurationPaths
{
    public const string DefaultSettingsOverrideFileName = "appsettings.LocalOverrides.json";

    public static string ResolveSettingsOverridePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var defaultPath = Path.Combine(AppContext.BaseDirectory, DefaultSettingsOverrideFileName);
        var discoveryCandidates = new[]
        {
            defaultPath,
            DiscoverFromAncestors(Path.Combine("MemorySmith.App", DefaultSettingsOverrideFileName)),
            DiscoverFromAncestors(Path.Combine("artifacts", "MemorySmith.App", DefaultSettingsOverrideFileName))
        };

        foreach (var candidate in discoveryCandidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return defaultPath;
    }

    private static string? DiscoverFromAncestors(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}