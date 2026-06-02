namespace MemorySmith.App.Services;

/// <summary>
/// Represents a parsed chunk from a source file, produced by one of the
/// parser strategies (Roslyn, tree-sitter, heuristic, or fixed-window).
/// </summary>
public sealed record ParsedChunk(
    int StartLine,
    int EndLine,
    string ChunkText);
