using MemorySmith.Core.Models;

namespace MemorySmith.Core.Indexing;

public class MemoryIndex
{
    private readonly ReaderWriterLockSlim _lock = new();

    public Dictionary<string, MemoryRecord> ById { get; } = new();
    public Dictionary<string, HashSet<string>> ByTag { get; } = new();
    public Dictionary<string, HashSet<string>> ByReference { get; } = new();

    public void Add(MemoryRecord record)
    {
        _lock.EnterWriteLock();
        try
        {
            AddCore(record);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Remove(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            RemoveCore(id);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Rebuild(IEnumerable<MemoryRecord> records)
    {
        _lock.EnterWriteLock();
        try
        {
            ById.Clear();
            ByTag.Clear();
            ByReference.Clear();
            foreach (var r in records)
            {
                AddCore(r);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void AddCore(MemoryRecord record)
    {
        ById[record.Id] = record;
        foreach (var tag in record.Tags)
        {
            if (!ByTag.TryGetValue(tag, out var set))
                ByTag[tag] = set = new HashSet<string>();
            set.Add(record.Id);
        }
        foreach (var reference in record.References)
        {
            if (!ByReference.TryGetValue(reference, out var set))
                ByReference[reference] = set = new HashSet<string>();
            set.Add(record.Id);
        }
    }

    private void RemoveCore(string id)
    {
        if (!ById.TryGetValue(id, out var record)) return;
        ById.Remove(id);
        foreach (var tag in record.Tags)
            if (ByTag.TryGetValue(tag, out var set)) set.Remove(id);
        foreach (var reference in record.References)
            if (ByReference.TryGetValue(reference, out var set)) set.Remove(id);
    }
}
