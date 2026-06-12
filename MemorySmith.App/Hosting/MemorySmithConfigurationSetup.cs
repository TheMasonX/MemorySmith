namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;

/// <summary>
/// Configuration layering for the host. Extracted from Program.cs (TSK-0282).
/// The settings-override layer is the one the June 4 reconstruction silently dropped —
/// AdminSettingsService persists edits to that file, so the app must load the same path it
/// writes or admin settings changes are silently ignored.
/// </summary>
public static class MemorySmithConfigurationSetup
{
    public static WebApplicationBuilder AddMemorySmithConfigurationLayers(this WebApplicationBuilder builder)
    {
        if (string.Equals(builder.Environment.EnvironmentName, "LocalDevelopment", StringComparison.OrdinalIgnoreCase))
        {
            builder.WebHost.UseStaticWebAssets();
        }
        // Load optional local secrets file from the service working directory (survives publishes, gitignored in artifacts/)
        var secretsFile = Path.Combine(AppContext.BaseDirectory, "appsettings.Secrets.json");
        if (File.Exists(secretsFile))
            builder.Configuration.AddJsonFile(secretsFile, optional: true, reloadOnChange: false);
        // Runtime settings overrides: AdminSettingsService persists edits to the path resolved by
        // MemorySmithConfigurationPaths, so the app must load the same file it writes — otherwise
        // admin settings changes are silently ignored.
        var settingsOverrideFile = MemorySmithConfigurationPaths.ResolveSettingsOverridePath(builder.Configuration["MemorySmith:SettingsOverridePath"]);
        builder.Configuration.AddJsonFile(settingsOverrideFile, optional: true, reloadOnChange: true);
        return builder;
    }
}
