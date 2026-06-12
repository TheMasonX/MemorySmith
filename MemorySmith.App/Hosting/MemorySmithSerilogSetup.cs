namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

/// <summary>
/// Serilog host logging: console, structured rolling file, and (on Windows) the event log,
/// driven by MemorySmith:Logging options. Extracted from Program.cs (TSK-0282) — the structured
/// file sink was among the blocks the June 4 reconstruction silently dropped.
/// </summary>
public static class MemorySmithSerilogSetup
{
    public static WebApplicationBuilder UseMemorySmithSerilog(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, loggerConfiguration) =>
        {
            var loggingOptions = context.Configuration.GetSection("MemorySmith:Logging").Get<LoggingOptions>() ?? new LoggingOptions();
            var minimumLevel = ParseLogLevel(loggingOptions.MinimumLevel, LogEventLevel.Information);

            loggerConfiguration
                .MinimumLevel.Is(minimumLevel)
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "MemorySmith.App")
                .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);

            if (loggingOptions.EnableConsole)
            {
                loggerConfiguration.WriteTo.Console();
            }

            if (loggingOptions.EnableStructuredFile)
            {
                var structuredFilePath = ResolveLogPath(loggingOptions.StructuredFilePath);
                var structuredFileDirectory = Path.GetDirectoryName(structuredFilePath);
                if (string.IsNullOrWhiteSpace(structuredFileDirectory))
                {
                    structuredFileDirectory = AppContext.BaseDirectory;
                    structuredFilePath = Path.Combine(structuredFileDirectory, Path.GetFileName(structuredFilePath));
                }

                Directory.CreateDirectory(structuredFileDirectory);
                loggerConfiguration.WriteTo.File(
                    new CompactJsonFormatter(),
                    structuredFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: Math.Max(1, loggingOptions.StructuredFileRetainedDays),
                    shared: true);
            }

            if (OperatingSystem.IsWindows() && loggingOptions.WindowsEventLogEnabled)
            {
                loggerConfiguration.WriteTo.EventLog(
                    source: string.IsNullOrWhiteSpace(loggingOptions.WindowsEventLogSource) ? "MemorySmith.App" : loggingOptions.WindowsEventLogSource,
                    manageEventSource: false,
                    restrictedToMinimumLevel: LogEventLevel.Warning);
            }
        });
        return builder;
    }

    internal static string ResolveLogPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    internal static LogEventLevel ParseLogLevel(string? rawLevel, LogEventLevel fallback)
    {
        if (Enum.TryParse<LogEventLevel>(rawLevel, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
