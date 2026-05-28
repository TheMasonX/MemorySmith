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

        return Path.Combine(AppContext.BaseDirectory, DefaultSettingsOverrideFileName);
    }
}