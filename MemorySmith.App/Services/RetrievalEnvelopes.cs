namespace MemorySmith.App.Services;

public sealed record RetrievalProviderMetadata(
    string Kind,
    string Mode,
    bool Available,
    string Reason,
    string? ModelPath = null,
    string? VocabularyPath = null,
    int? Dimension = null);

public sealed record RetrievalResultEnvelope<T>(
    string SchemaVersion,
    string Mode,
    RetrievalProviderMetadata Provider,
    IReadOnlyList<T> Results,
    IReadOnlyList<string> Warnings);