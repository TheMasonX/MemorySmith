using System.Text.Json.Serialization;

namespace MemorySmith.Core.Models;

public sealed record MemoryDiagnostic(
    [property: JsonPropertyName("code")]
    string Code,
    [property: JsonPropertyName("severity")]
    string Severity,
    [property: JsonPropertyName("category")]
    string Category,
    [property: JsonPropertyName("message")]
    string Message,
    [property: JsonPropertyName("target")]
    string? Target = null);