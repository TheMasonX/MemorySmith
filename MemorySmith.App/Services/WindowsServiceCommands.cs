using System.Diagnostics;
using System.Reflection;

namespace MemorySmith.App.Services;

public enum WindowsServiceCommandKind
{
    Install,
    Uninstall
}

public sealed record WindowsServiceCommand(
    WindowsServiceCommandKind Kind,
    string ServiceName,
    string DisplayName,
    string Description,
    string StartType,
    IReadOnlyList<string> RuntimeArguments);

public static class WindowsServiceCommands
{
    public const string DefaultServiceName = "MemorySmith";
    private const string DefaultDescription = "MemorySmith local memory wiki, API, MCP endpoint, and maintenance scheduler.";

    public static bool TryHandle(string[] args, out int exitCode)
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

    public static WindowsServiceCommand? Parse(IReadOnlyList<string> args)
    {
        WindowsServiceCommandKind? kind = null;
        var serviceName = DefaultServiceName;
        var displayName = DefaultServiceName;
        var description = DefaultDescription;
        var startType = "auto";
        var runtimeArgs = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (Matches(arg, "--install-service", "/install-service", "install-service"))
            {
                kind = WindowsServiceCommandKind.Install;
                continue;
            }

            if (Matches(arg, "--uninstall-service", "/uninstall-service", "uninstall-service"))
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
                startType = configuredStartType;
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
            ? new WindowsServiceCommand(kind.Value, serviceName, displayName, description, startType, runtimeArgs)
            : null;
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

    private static int Execute(WindowsServiceCommand command)
    {
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
        var binaryPath = BuildBinaryPath(command.RuntimeArguments);
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

    private static bool Matches(string value, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsDotnetHost(string processPath) =>
        string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}