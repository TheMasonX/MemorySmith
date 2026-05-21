namespace MemorySmith.Core.Models;

public sealed record MemoryDiagnostic(
    string Code,
    string Severity,
    string Category,
    string Message,
    string? Target = null);