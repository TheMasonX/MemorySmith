namespace MemorySmith.Storage;

public sealed record CorruptStorageFile(string Path, string Error, DateTime ObservedAtUtc);

public sealed record StorageDiagnosticsSnapshot(IReadOnlyList<CorruptStorageFile> CorruptFiles);

public class StorageDiagnostics
{
    private readonly object _lock = new();
    private readonly List<CorruptStorageFile> _corruptFiles = [];

    public void RecordCorruptFile(string path, string error)
    {
        lock (_lock)
        {
            _corruptFiles.Add(new CorruptStorageFile(path, error, DateTime.UtcNow));
        }
    }

    public StorageDiagnosticsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new StorageDiagnosticsSnapshot(_corruptFiles.ToList());
        }
    }
}