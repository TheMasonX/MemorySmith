using MemorySmith.Core.Models;

namespace MemorySmith.App.Services;

internal static class MemoryDiagnosticFormatting
{
    private const int MaxContextPackWarnings = 20;
    private const int MaxMarkdownDiagnosticsPerRecord = 5;

    public static IReadOnlyList<string> ToWarningSummaries(IEnumerable<MemoryContextPackRecord> records) =>
        records
            .SelectMany(record => record.Diagnostics
                .Where(IsWarningOrError)
                .Select(diagnostic => $"{record.Id}: {diagnostic.Code} - {diagnostic.Message}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxContextPackWarnings)
            .ToList();

    public static string FormatMarkdownSection(IReadOnlyList<MemoryDiagnostic> diagnostics)
    {
        var warnings = diagnostics
            .Where(IsWarningOrError)
            .Take(MaxMarkdownDiagnosticsPerRecord)
            .ToList();

        return warnings.Count == 0
            ? string.Empty
            : "Diagnostics:" + Environment.NewLine +
              string.Join(Environment.NewLine, warnings.Select(diagnostic => $"- {diagnostic.Code}: {diagnostic.Message}")) +
              Environment.NewLine;
    }

    private static bool IsWarningOrError(MemoryDiagnostic diagnostic) =>
        string.Equals(diagnostic.Severity, "Warning", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(diagnostic.Severity, "Error", StringComparison.OrdinalIgnoreCase);
}
