using System.Text.Json;

namespace MemorySmith.Storage;

/// <summary>
/// Stores path variable definitions as a flat JSON dictionary in a single file.
/// Variables are used to expand <c>%VariableName%</c> tokens in SourceLink URIs.
/// </summary>
public class FileVarStore : IVarStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FileVarStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public IReadOnlyDictionary<string, string> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IReadOnlyDictionary<string, string> vars)
    {
        var json = JsonSerializer.Serialize(vars, JsonOptions);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
