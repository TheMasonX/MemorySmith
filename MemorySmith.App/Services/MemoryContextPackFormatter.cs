using System.Text.Json;
using MemorySmith.Core.Models;

namespace MemorySmith.App.Services;

internal static class MemoryContextPackFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Format(
        MemoryContextPack pack,
        string? format,
        Func<string, string>? resolveUri = null,
        bool includeSourceLinksInMarkdown = false)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var projected = new
            {
                schemaVersion = pack.SchemaVersion,
                query = pack.Query,
                generatedAt = pack.GeneratedAt,
                warnings = pack.Warnings,
                diagnostics = pack.Diagnostics.Select(diagnostic => new
                {
                    code = diagnostic.Code,
                    severity = diagnostic.Severity,
                    message = diagnostic.Message
                }),
                records = pack.Records.Select(record => new
                {
                    id = record.Id,
                    title = record.Title,
                    status = record.Status,
                    confidence = record.Confidence,
                    tags = record.Tags,
                    references = record.References,
                    conflicts = record.Conflicts,
                    reverseReferences = record.ReverseReferences,
                    diagnostics = record.Diagnostics.Select(diagnostic => new
                    {
                        code = diagnostic.Code,
                        severity = diagnostic.Severity,
                        message = diagnostic.Message
                    }),
                    sourceLinks = record.SourceLinks.Select(sourceLink => new
                    {
                        label = sourceLink.Label,
                        uri = ResolveUri(sourceLink.Uri, resolveUri),
                        startLine = sourceLink.StartLine,
                        endLine = sourceLink.EndLine
                    }),
                    usageCount = record.UsageCount,
                    lastUpdated = record.LastUpdated,
                    relationship = record.Relationship,
                    score = record.Score,
                    matchReason = record.MatchReason,
                    content = record.Content
                })
            };

            return JsonSerializer.Serialize(projected, JsonOptions);
        }

        var warnings = pack.Warnings.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}Warnings:{Environment.NewLine}" + string.Join(Environment.NewLine, pack.Warnings.Select(warning => $"- {warning}")) + Environment.NewLine;

        if (pack.Records.Count == 0)
        {
            return $"# Context Pack{Environment.NewLine}Query: {pack.Query ?? string.Empty}{Environment.NewLine}Generated: {pack.GeneratedAt:O}{warnings}{Environment.NewLine}No context pack records.";
        }

        var sections = pack.Records.Select(record =>
        {
            var scoreLine = record.Score.HasValue ? $"Score: {record.Score:0.######}" : "Score: linked context";
            var matchLine = string.IsNullOrWhiteSpace(record.MatchReason) ? string.Empty : $"Match: {record.MatchReason}{Environment.NewLine}";
            var sourceLinks = includeSourceLinksInMarkdown && record.SourceLinks.Count > 0
                ? $"Source Links: {string.Join(", ", record.SourceLinks.Select(sourceLink => FormatSourceLink(sourceLink, resolveUri)))}{Environment.NewLine}"
                : string.Empty;
            var incomingRefs = record.ReverseReferences.Count > 0
                ? $"Incoming References: {FormatLinks(record.ReverseReferences)}{Environment.NewLine}"
                : string.Empty;
            var diagnostics = MemoryDiagnosticFormatting.FormatMarkdownSection(record.Diagnostics);

            return $"## {record.Id}: {record.Title}{Environment.NewLine}" +
                   $"Relationship: {record.Relationship}{Environment.NewLine}" +
                   $"Status: {record.Status}; Confidence: {record.Confidence:P0}; Uses: {record.UsageCount}{Environment.NewLine}" +
                   $"Tags: {string.Join(", ", record.Tags)}{Environment.NewLine}" +
                   $"References: {FormatLinks(record.References)}{Environment.NewLine}" +
                   $"Conflicts: {FormatLinks(record.Conflicts)}{Environment.NewLine}" +
                   incomingRefs +
                   sourceLinks +
                   $"{scoreLine}{Environment.NewLine}" +
                   matchLine +
                   diagnostics +
                   record.Content;
        });

        return $"# Context Pack{Environment.NewLine}Query: {pack.Query ?? string.Empty}{Environment.NewLine}Generated: {pack.GeneratedAt:O}{warnings}{Environment.NewLine}" +
            string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static string ResolveUri(string uri, Func<string, string>? resolveUri) =>
        resolveUri is null ? uri : resolveUri(uri);

    private static string FormatSourceLink(SourceLink sourceLink, Func<string, string>? resolveUri)
    {
        var resolved = ResolveUri(sourceLink.Uri, resolveUri);
        var label = string.IsNullOrWhiteSpace(sourceLink.Label) ? resolved : sourceLink.Label;
        var lineHint = sourceLink.StartLine.HasValue
            ? sourceLink.EndLine.HasValue ? $":{sourceLink.StartLine}-{sourceLink.EndLine}" : $":{sourceLink.StartLine}"
            : string.Empty;

        return resolved == sourceLink.Uri
            ? $"{label}{lineHint}"
            : $"{label}{lineHint} ({resolved}{lineHint})";
    }

    private static string FormatLinks(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);
}
