using System.Diagnostics;
using System.Reflection;

namespace MemorySmith.App.Services;

public enum WindowsServiceCommandKind
{
    Help,
    Install,
    Uninstall
}

public sealed record WindowsServiceCommand(
    WindowsServiceCommandKind Kind,
    string ServiceName,
    string DisplayName,
    string Description,
    string StartType,
    string? MemoryDirectory,
    int? Port,
    IReadOnlyList<string> RuntimeArguments);

public static class WindowsServiceCommands
{
    public const string DefaultServiceName = "MemorySmith";
    public const int DefaultPort = 5089;
    private const string DefaultDescription = "MemorySmith local memory wiki, API, MCP endpoint, and maintenance scheduler.";
    private static readonly HashSet<string> ValidStartTypes = new(StringComparer.OrdinalIgnoreCase) { "auto", "demand", "disabled" };

    public static bool TryHandle(string[] args, out int exitCode)
    {
        try
        {
            var command = Parse(args);
            if (command is null)
            {
                exitCode = 0;
                return false;
            }

            exitCode = Execute(command);
            return true;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(GetHelpText());
            exitCode = 2;
            return true;
        }
    }

    public static WindowsServiceCommand? Parse(IReadOnlyList<string> args)
    {
        WindowsServiceCommandKind? kind = null;
        var serviceName = DefaultServiceName;
        var displayName = DefaultServiceName;
        var description = DefaultDescription;
        var startType = "auto";
        string? memoryDirectory = null;
        int? port = null;
        var runtimeArgs = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (Matches(arg, "--help", "-h", "/?", "help"))
            {
                kind = WindowsServiceCommandKind.Help;
                continue;
            }

            if (Matches(arg, "--install-service", "/install-service", "install-service", "--install", "/install", "install"))
            {
                kind = WindowsServiceCommandKind.Install;
                continue;
            }

            if (Matches(arg, "--uninstall-service", "/uninstall-service", "uninstall-service", "--uninstall", "/uninstall", "uninstall"))
            {
                kind = WindowsServiceCommandKind.Uninstall;
                continue;
            }

            if (TryReadOption(args, ref index, arg, "--service-name", out var configuredName))
            {
                serviceName = configuredName;
                if (displayName == DefaultServiceName)
                {
                    displayName = configuredName;
                }
                continue;
            }

            if (TryReadOption(args, ref index, arg, "--service-display-name", out var configuredDisplayName))
            {
                displayName = configuredDisplayName;
                continue;
            }

            if (TryReadOption(args, ref index, arg, "--service-description", out var configuredDescription))
            {
                description = configuredDescription;
                continue;
            }

            if (TryReadOption(args, ref index, arg, "--service-start-type", out var configuredStartType))
            {
                ValidateStartType(configuredStartType);
                startType = configuredStartType;
                continue;
            }

            if (TryReadOption(args, ref index, arg, "--memory-directory", out var configuredMemoryDirectory) ||
                TryReadOption(args, ref index, arg, "--data-path", out configuredMemoryDirectory))
            {
                memoryDirectory = Path.GetFullPath(configuredMemoryDirectory);
                continue;
            }

            if (TryReadOption(args, ref index, arg, "--port", out var configuredPort))
            {
                if (!int.TryParse(configuredPort, out var parsedPort) || parsedPort is < 1 or > 65535)
                {
                    throw new ArgumentException("--port must be an integer from 1 to 65535.");
                }

                port = parsedPort;
                continue;
            }

            if (arg == "--")
            {
                runtimeArgs.AddRange(args.Skip(index + 1));
                break;
            }

            runtimeArgs.Add(arg);
        }

        return kind.HasValue
            ? new WindowsServiceCommand(kind.Value, serviceName, displayName, description, startType, memoryDirectory, port, runtimeArgs)
            : null;
    }

    public static IReadOnlyList<string> BuildRuntimeArguments(WindowsServiceCommand command)
    {
        var runtimeArguments = command.RuntimeArguments.ToList();

        if (command.Kind != WindowsServiceCommandKind.Install)
        {
            return runtimeArguments;
        }

        if (command.Port.HasValue && ContainsOption(runtimeArguments, "--urls"))
        {
            throw new ArgumentException("Use either --port or a runtime --urls argument, not both.");
        }

        if (!ContainsOption(runtimeArguments, "--urls"))
        {
            var port = command.Port ?? DefaultPort;
            runtimeArguments.Add("--urls");
            runtimeArguments.Add($"http://localhost:{port}");
        }

        if (!string.IsNullOrWhiteSpace(command.MemoryDirectory))
        {
            var paths = GetServiceStoragePaths(command.MemoryDirectory);
            AddConfigurationArgument(runtimeArguments, "--MemorySmith:DataPath", paths.MemoryDirectory);
            AddConfigurationArgument(runtimeArguments, "--MemorySmith:EventLogPath", paths.EventLogPath);
            AddConfigurationArgument(runtimeArguments, "--MemorySmith:VarsPath", paths.VarsPath);
        }

        return runtimeArguments;
    }

    public static string BuildBinaryPath(IReadOnlyList<string> runtimeArguments)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Cannot determine the current process path.");
        }

        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var command = IsDotnetHost(processPath) && !string.IsNullOrWhiteSpace(entryAssemblyPath)
            ? $"{Quote(processPath)} {Quote(entryAssemblyPath)}"
            : Quote(processPath);

        foreach (var argument in runtimeArguments)
        {
            command += " " + Quote(argument);
        }

        return command;
    }

        public static string GetHelpText()
        {
                var executable = Path.GetFileName(Environment.ProcessPath) ?? "MemorySmith.App.exe";
                return $$"""
MemorySmith Windows Service CLI

Usage:
    {{executable}} install [options]
    {{executable}} uninstall [options]
    {{executable}} --help

Install options:
    --service-name <name>             Windows Service name. Default: MemorySmith
    --service-display-name <name>     Display name. Default: service name
    --service-description <text>      Service description
    --service-start-type <type>       auto, demand, or disabled. Default: auto
    --memory-directory <path>         Memory record directory used as MemorySmith:DataPath
    --port <1-65535>                  Local HTTP port. Default: {{DefaultPort}}

Runtime arguments:
    Arguments after -- are passed to ASP.NET Core unchanged. Use this for advanced host settings.

Examples:
    {{executable}} install --memory-directory C:\MemorySmith\Memories --port 5089
    {{executable}} install --service-name MemorySmith.Dev --service-start-type demand --memory-directory C:\MemorySmith\Dev\Memories --port 5090
    {{executable}} uninstall --service-name MemorySmith.Dev

Notes:
    Run install and uninstall from an elevated PowerShell session.
    --memory-directory also derives adjacent paths for MemorySmith:EventLogPath and MemorySmith:VarsPath.
    The installed service listens on http://localhost:<port> unless you pass a custom --urls runtime argument after --.
""";
        }

    private static int Execute(WindowsServiceCommand command)
    {
                if (command.Kind == WindowsServiceCommandKind.Help)
                {
                        Console.WriteLine(GetHelpText());
                        return 0;
                }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows Service installation is only available on Windows.");
            return 1;
        }

        return command.Kind switch
        {
            WindowsServiceCommandKind.Install => Install(command),
            WindowsServiceCommandKind.Uninstall => Uninstall(command),
            _ => 1
        };
    }

    private static int Install(WindowsServiceCommand command)
    {
        EnsureInstallDirectories(command);
        var runtimeArguments = BuildRuntimeArguments(command);
        var binaryPath = BuildBinaryPath(runtimeArguments);
        var createExitCode = RunSc(
            "create",
            command.ServiceName,
            "binPath=",
            binaryPath,
            "start=",
            command.StartType,
            "DisplayName=",
            command.DisplayName);

        if (createExitCode != 0)
        {
            return createExitCode;
        }

        return RunSc("description", command.ServiceName, command.Description);
    }

    private static int Uninstall(WindowsServiceCommand command)
    {
        _ = RunSc("stop", command.ServiceName);
        return RunSc("delete", command.ServiceName);
    }

    private static int RunSc(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine(error.Trim());
        }

        return process.ExitCode;
    }

    private static void EnsureInstallDirectories(WindowsServiceCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.MemoryDirectory))
        {
            return;
        }

        var paths = GetServiceStoragePaths(command.MemoryDirectory);
        Directory.CreateDirectory(paths.MemoryDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.EventLogPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.VarsPath)!);
    }

    private static ServiceStoragePaths GetServiceStoragePaths(string memoryDirectory)
    {
        var normalizedMemoryDirectory = Path.GetFullPath(memoryDirectory);
        var trimmed = normalizedMemoryDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(trimmed)?.FullName ?? trimmed;
        return new ServiceStoragePaths(
            normalizedMemoryDirectory,
            Path.Combine(parent, "Events", "audit.log"),
            Path.Combine(parent, "vars.json"));
    }

    private static void AddConfigurationArgument(List<string> runtimeArguments, string optionName, string value)
    {
        if (ContainsOption(runtimeArguments, optionName))
        {
            throw new ArgumentException($"Use either {optionName} as a runtime argument or --memory-directory, not both.");
        }

        runtimeArguments.Add(optionName);
        runtimeArguments.Add(value);
    }

    private static bool TryReadOption(IReadOnlyList<string> args, ref int index, string arg, string optionName, out string value)
    {
        value = string.Empty;
        if (arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(optionName.Length + 1)..];
            return !string.IsNullOrWhiteSpace(value);
        }

        if (!string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"Missing value for {optionName}.");
        }

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void ValidateStartType(string startType)
    {
        if (!ValidStartTypes.Contains(startType))
        {
            throw new ArgumentException("--service-start-type must be one of: auto, demand, disabled.");
        }
    }

    private static bool ContainsOption(IReadOnlyList<string> args, string optionName)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string value, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsDotnetHost(string processPath) =>
        string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private sealed record ServiceStoragePaths(string MemoryDirectory, string EventLogPath, string VarsPath);
}