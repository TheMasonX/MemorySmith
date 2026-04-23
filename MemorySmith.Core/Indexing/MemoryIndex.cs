using MemorySmith.Core.Models;

namespace MemorySmith.Core.Indexing;

public class MemoryIndex
{
    public Dictionary<string, MemoryRecord> ById { get; } = new();
    public Dictionary<string, HashSet<string>> ByTag { get; } = new();
    public Dictionary<string, HashSet<string>> ByReference { get; } = new();

    public void Add(MemoryRecord record)
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

    public void Remove(string id)
    {
        if (!ById.TryGetValue(id, out var record)) return;
        ById.Remove(id);
        foreach (var tag in record.Tags)
            if (ByTag.TryGetValue(tag, out var set)) set.Remove(id);
        foreach (var reference in record.References)
            if (ByReference.TryGetValue(reference, out var set)) set.Remove(id);
    }

    public void Rebuild(IEnumerable<MemoryRecord> records)
    {
        ById.Clear();
        ByTag.Clear();
        ByReference.Clear();
        foreach (var r in records) Add(r);
    }
}
