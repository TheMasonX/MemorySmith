namespace MemorySmith.App.Services.Training;

public static class TrainingPathResolver
{
    public static string ResolveConfiguredPath(string configuredPath, string? contentRootPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        string? fallback = null;
        foreach (var baseDirectory in EnumerateCandidateBaseDirectories(contentRootPath))
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
            fallback ??= candidate;
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return fallback ?? Path.GetFullPath(configuredPath);
    }

    public static string ResolvePythonExecutablePath(string configuredVenvPath, string? contentRootPath)
    {
        var venvRoot = ResolveConfiguredPath(configuredVenvPath, contentRootPath);
        var windowsPython = Path.Combine(venvRoot, "Scripts", "python.exe");
        if (File.Exists(windowsPython) || OperatingSystem.IsWindows())
        {
            return windowsPython;
        }

        return Path.Combine(venvRoot, "bin", "python");
    }

    private static IEnumerable<string> EnumerateCandidateBaseDirectories(string? contentRootPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in new[] { contentRootPath, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(seed))
            {
                continue;
            }

            var current = Path.GetFullPath(seed);
            while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
            {
                yield return current;

                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }
    }
}