namespace MemorySmith.Core.Models;

public class SourceLink
{
    /// <summary>Display label. When empty, the resolved URI is used as the label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// URI or file path. May contain %VariableName% tokens defined in the wiki variable store,
    /// e.g. "%MemorySmithRepo%MemorySmith.App/Program.cs" or "https://github.com/owner/repo".
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>Optional 1-based start line for local file references. Null means the whole file.</summary>
    public int? StartLine { get; set; }

    /// <summary>
    /// Optional 1-based end line (inclusive) for local file references.
    /// When null and <see cref="StartLine"/> is set, defaults to StartLine + 49 (50 lines).
    /// </summary>
    public int? EndLine { get; set; }
}
