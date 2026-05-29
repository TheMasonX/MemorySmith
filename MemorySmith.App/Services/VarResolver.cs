using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

/// <summary>
/// Result of reading a source link's content. For URL sources the Content is null;
/// callers should present the ResolvedUri as a clickable link instead.
/// </summary>
public record SourceContent(
    string ResolvedUri,
    string? Content,
    string ContentType,    // "file" or "url"
    int? StartLine,
    int? EndLine,
    bool Exists);

public sealed record SourceOpenResult(bool Opened, string ResolvedUri, string Message);

/// <summary>
/// Resolves <c>%VariableName%</c> tokens in source link URIs using the wiki variable store.
/// Variables are defined per-wiki in <c>Data/vars.json</c> and are editable via the /variables page.
/// </summary>
public partial class VarResolver
{
    private readonly IVarStore _varStore;
    private readonly MemorySmithOptions _options;

    public VarResolver(IVarStore varStore, IOptions<MemorySmithOptions> options)
    {
        _varStore = varStore;
        _options = options.Value;
    }

    /// <summary>
    /// Expands all <c>%VariableName%</c> tokens in <paramref name="raw"/> using the current variable store.
    /// Tokens with no matching variable are left unchanged.
    /// </summary>
    public string Resolve(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains('%'))
            return raw;

        var vars = _varStore.Load();
        return TokenPattern().Replace(raw, match =>
            vars.TryGetValue(match.Groups[1].Value, out var val) ? val : match.Value);
    }

    /// <summary>
    /// Resolves and reads the content of a source link.
    /// For HTTP/HTTPS URLs returns a <see cref="SourceContent"/> with null Content (URL is unfetchable server-side).
    /// For local file paths reads the file, optionally restricting to the line range specified on <paramref name="link"/>.
    /// </summary>
    /// <param name="link">Source link to read.</param>
    /// <param name="maxBytes">Maximum content bytes to return (truncates with a message if exceeded). Default 16 384.</param>
    public async Task<SourceContent> ReadSourceAsync(SourceLink link, int maxBytes = 16384)
    {
        maxBytes = ClampReadBytes(maxBytes);
        var resolved = Resolve(link.Uri);
        if (string.IsNullOrWhiteSpace(resolved))
            return new SourceContent(resolved, null, "file", link.StartLine, link.EndLine, Exists: false);

        if (resolved.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            resolved.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new SourceContent(resolved, null, "url", null, null, Exists: true);

        if (!TryNormalizePath(resolved, out var fullPath))
            return new SourceContent(resolved, "Source link path is invalid.", "file", link.StartLine, link.EndLine, Exists: false);

        if (!TryAuthorizeSourcePath(fullPath, out var policyMessage))
            return new SourceContent(resolved, policyMessage, "file", link.StartLine, link.EndLine, Exists: false);

        if (!File.Exists(fullPath))
            return new SourceContent(resolved, null, "file", link.StartLine, link.EndLine, Exists: false);

        var before = Math.Max(0, _options.SourceLinks.ReadContextLinesBefore);
        var after = Math.Max(0, _options.SourceLinks.ReadContextLinesAfter);
        var hasRequestedWindow = link.StartLine.HasValue || link.EndLine.HasValue;
        int startIdx;
        int? endIdx;

        if (hasRequestedWindow)
        {
            startIdx = link.StartLine.HasValue ? Math.Max(0, link.StartLine.Value - 1 - before) : 0;
            endIdx = link.EndLine.HasValue
                ? Math.Max(startIdx, link.EndLine.Value - 1 + after)
                : (link.StartLine.HasValue ? Math.Max(startIdx, link.StartLine.Value - 1 + after) : null);
        }
        else if (_options.SourceLinks.AllowUnrestrictedSourceReads)
        {
            startIdx = 0;
            endIdx = null;
        }
        else
        {
            startIdx = 0;
            endIdx = 49;
        }

        var content = await ReadSelectedContentAsync(fullPath, startIdx, endIdx, maxBytes);

        return new SourceContent(fullPath, content, "file", link.StartLine, link.EndLine, Exists: true);
    }

    public Task<SourceOpenResult> OpenWithDefaultAppAsync(SourceLink link, IEnumerable<string>? additionalAllowedRoots = null)
    {
        if (!_options.SourceLinks.AllowOpenWithDefaultApp)
        {
            return Task.FromResult(new SourceOpenResult(false, Resolve(link.Uri), "Opening source links with the default app is disabled."));
        }

        if (!TryResolveLocalFile(link, out var fullPath, out var message, additionalAllowedRoots))
        {
            return Task.FromResult(new SourceOpenResult(false, Resolve(link.Uri), message ?? "Source link could not be opened."));
        }

        try
        {
            StartDefaultAppProcess(CreateDefaultAppStartInfo(fullPath));

            return Task.FromResult(new SourceOpenResult(true, fullPath, "Opened source link."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SourceOpenResult(false, fullPath, ex.Message));
        }
    }

    public bool TryResolveLocalFile(SourceLink link, out string fullPath, out string? message, IEnumerable<string>? additionalAllowedRoots = null)
    {
        var resolved = Resolve(link.Uri);
        fullPath = resolved;
        message = null;

        if (string.IsNullOrWhiteSpace(resolved))
        {
            message = "Source link path is empty.";
            return false;
        }

        if (resolved.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            resolved.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            message = "Web source links should be opened in the browser.";
            return false;
        }

        if (!TryNormalizePath(resolved, out fullPath))
        {
            message = "Source link path is invalid.";
            return false;
        }

        if (!TryAuthorizeSourcePath(fullPath, out message, additionalAllowedRoots))
        {
            return false;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            message = "Source link path does not exist.";
            return false;
        }

        return true;
    }

    protected virtual Process? StartDefaultAppProcess(ProcessStartInfo startInfo) =>
        Process.Start(startInfo);

    private static ProcessStartInfo CreateDefaultAppStartInfo(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(EncodePowerShellCommand($"Invoke-Item -LiteralPath {QuotePowerShellString(fullPath)}"));
            return startInfo;
        }

        var command = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var processStartInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStartInfo.ArgumentList.Add(fullPath);
        return processStartInfo;
    }

    private static string EncodePowerShellCommand(string command) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    private static string QuotePowerShellString(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>Returns all currently defined variables.</summary>
    public IReadOnlyDictionary<string, string> GetVars() => _varStore.Load();

    /// <summary>Persists the given variable dictionary.</summary>
    public void SaveVars(IReadOnlyDictionary<string, string> vars) => _varStore.Save(vars);

    private int ClampReadBytes(int requestedBytes)
    {
        var configuredMax = Math.Max(1, _options.SourceLinks.MaxReadBytes);
        if (requestedBytes <= 0)
        {
            return Math.Min(16384, configuredMax);
        }

        return Math.Min(requestedBytes, configuredMax);
    }

    private static async Task<string> ReadSelectedContentAsync(string fullPath, int startIdx, int? endIdx, int maxBytes)
    {
        using var stream = File.OpenRead(fullPath);
        using var reader = new StreamReader(stream);

        var builder = new StringBuilder(Math.Min(maxBytes, 4096));
        var lineIndex = 0;
        var totalSelectedChars = 0;
        var firstSelectedLine = true;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (lineIndex < startIdx)
            {
                lineIndex++;
                continue;
            }

            if (endIdx.HasValue && lineIndex > endIdx.Value)
            {
                break;
            }

            var separatorLength = firstSelectedLine ? 0 : 1;
            totalSelectedChars += separatorLength + line.Length;

            var remaining = maxBytes - builder.Length;
            if (remaining > 0)
            {
                if (!firstSelectedLine)
                {
                    builder.Append('\n');
                    remaining--;
                }

                if (remaining > 0)
                {
                    builder.Append(line.AsSpan(0, Math.Min(remaining, line.Length)));
                }
            }

            firstSelectedLine = false;
            lineIndex++;
        }

        var content = builder.ToString();
        if (totalSelectedChars > maxBytes)
        {
            content += $"\n[... truncated — {totalSelectedChars - maxBytes} more chars]";
        }

        return content;
    }

    private bool TryAuthorizeSourcePath(string fullPath, out string? message, IEnumerable<string>? additionalAllowedRoots = null)
    {
        if (IsDeniedSourcePath(fullPath, out message))
        {
            return false;
        }

        if (_options.SourceLinks.AllowUnrestrictedSourceReads)
        {
            message = null;
            return true;
        }

        var roots = GetAllowedSourceRoots(additionalAllowedRoots);
        if (roots.Any(root => IsUnderRoot(fullPath, root)))
        {
            message = null;
            return true;
        }

        message = "Source link path is outside the configured allowed source roots.";
        return false;
    }

    private bool IsDeniedSourcePath(string fullPath, out string? message)
    {
        foreach (var root in GetDeniedSourceRoots())
        {
            if (IsUnderRoot(fullPath, root))
            {
                message = "Source link path is blocked by the configured denied source roots.";
                return true;
            }
        }

        message = null;
        return false;
    }

    private List<string> GetAllowedSourceRoots(IEnumerable<string>? additionalAllowedRoots = null)
    {
        var vars = _varStore.Load();
        var roots = new List<string>();

        foreach (var variableName in _options.SourceLinks.AllowedFileRootVariables)
        {
            if (vars.TryGetValue(variableName, out var value) && TryNormalizePath(value, out var root))
            {
                roots.Add(root);
            }
        }

        foreach (var configuredRoot in _options.SourceLinks.AllowedFileRoots)
        {
            var resolvedRoot = Resolve(configuredRoot);
            if (TryNormalizePath(resolvedRoot, out var root))
            {
                roots.Add(root);
            }
        }

        if (additionalAllowedRoots is not null)
        {
            foreach (var additionalRoot in additionalAllowedRoots)
            {
                if (TryNormalizePath(additionalRoot, out var root))
                {
                    roots.Add(root);
                }
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> GetDeniedSourceRoots()
    {
        var vars = _varStore.Load();
        var roots = new List<string>();

        foreach (var variableName in _options.SourceLinks.DeniedFileRootVariables)
        {
            if (vars.TryGetValue(variableName, out var value) && TryNormalizePath(value, out var root))
            {
                roots.Add(root);
            }
        }

        foreach (var configuredRoot in _options.SourceLinks.DeniedFileRoots)
        {
            var resolvedRoot = Resolve(configuredRoot);
            if (TryNormalizePath(resolvedRoot, out var root))
            {
                roots.Add(root);
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryNormalizePath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static bool IsUnderRoot(string fullPath, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"%(\w+)%")]
    private static partial Regex TokenPattern();
}
