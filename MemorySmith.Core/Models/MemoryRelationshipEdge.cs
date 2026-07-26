using System.Text.Json.Serialization;

namespace MemorySmith.Core.Models;

/// <summary>
/// Closed vocabulary of supported memory relationship types.
/// Extends the semantics already parsed by <c>MaintenanceTopicMapService.ExtractTagRelationships</c>.
/// </summary>
public enum RelationType
{
    /// <summary>General cross-reference — the source cites or mentions the target.</summary>
    References,

    /// <summary>Source supersedes (replaces / is a newer version of) the target.</summary>
    Supersedes,

    /// <summary>Source is superseded by (replaced by) the target.</summary>
    SupersededBy,

    /// <summary>Source depends on the target (prerequisite or dependency).</summary>
    DependsOn,

    /// <summary>Source conflicts with or contradicts the target.</summary>
    ConflictsWith,

    /// <summary>Source mentions the target in its content.</summary>
    Mentions,

    /// <summary>Source links to the target page.</summary>
    LinksTo,

    /// <summary>Source is semantically equivalent to or same-as the target.</summary>
    SameAs
}

/// <summary>Origin of a relationship edge — how it was created.</summary>
public enum EdgeOrigin
{
    /// <summary>Manually authored by a user.</summary>
    Manual,

    /// <summary>Inferred by a maintenance agent or automated analysis.</summary>
    Inferred,

    /// <summary>Imported from an external system.</summary>
    Imported
}

/// <summary>
/// A typed, directed edge between two memory records.
/// Additive alongside <see cref="MemoryRecord.References"/> and <see cref="MemoryRecord.Conflicts"/>
/// — those arrays remain the source of truth for the RRF scorer (via <c>referenceTokens</c>).
/// New code should prefer <see cref="MemoryRelationshipEdge"/> for richer semantics.
/// </summary>
public sealed record MemoryRelationshipEdge(
    [property: JsonPropertyName("sourceId")]
    string SourceId,
    [property: JsonPropertyName("targetId")]
    string TargetId,
    [property: JsonPropertyName("relationType")]
    RelationType RelationType,
    [property: JsonPropertyName("origin")]
    EdgeOrigin Origin = EdgeOrigin.Manual,
    [property: JsonPropertyName("createdAtUtc")]
    DateTimeOffset CreatedAtUtc = default)
{
    /// <summary>Convenience: creates a Manual edge with the current timestamp.</summary>
    public static MemoryRelationshipEdge Create(string sourceId, string targetId, RelationType relationType) =>
        new(sourceId, targetId, relationType, EdgeOrigin.Manual, DateTimeOffset.UtcNow);
}
