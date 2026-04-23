using System.Text.Json;
using MemorySmith.Core.Models;

namespace MemorySmith.Storage;

public class FileMemoryStore : IMemoryStore
{
    private readonly string _basePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FileMemoryStore(string basePath)
    {
        _basePath = basePath;
        foreach (var status in Enum.GetValues<MemoryStatus>())
            Directory.CreateDirectory(Path.Combine(_basePath, status.ToString()));
    }

    public MemoryRecord? Load(string id)
    {
        var path = FindFile(id);
        if (path is null) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MemoryRecord>(json);
    }

    public void Save(MemoryRecord record)
    {
        // Remove any stale copy in another status folder
        var existing = FindFile(record.Id);
        if (existing is not null)
        {
            var existingStatus = Path.GetFileName(Path.GetDirectoryName(existing));
            if (!string.Equals(existingStatus, record.Status.ToString(), StringComparison.OrdinalIgnoreCase))
                File.Delete(existing);
        }

        var dir = Path.Combine(_basePath, record.Status.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{record.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
    }

    public void Delete(string id)
    {
        var path = FindFile(id);
        if (path is not null) File.Delete(path);
    }

    public IEnumerable<MemoryRecord> LoadAll()
    {
        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json", SearchOption.AllDirectories))
        {
            MemoryRecord? record = null;
            try
            {
                var json = File.ReadAllText(file);
                record = JsonSerializer.Deserialize<MemoryRecord>(json);
            }
            catch { /* skip corrupt files */ }
            if (record is not null) yield return record;
        }
    }

    private string? FindFile(string id)
    {
        foreach (var status in Enum.GetValues<MemoryStatus>())
        {
            var path = Path.Combine(_basePath, status.ToString(), $"{id}.json");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
