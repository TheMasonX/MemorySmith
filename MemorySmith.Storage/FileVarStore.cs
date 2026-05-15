using System.Text.Json;

namespace MemorySmith.Storage;

/// <summary>
/// Stores path variable definitions as a flat JSON dictionary in a single file.
/// Variables are used to expand <c>%VariableName%</c> tokens in SourceLink URIs.
/// </summary>
public class FileVarStore : IVarStore
{
    private readonly string _path;
    private readonly StorageDiagnostics? _diagnostics;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FileVarStore(string path)
        : this(path, null)
    {
    }

    public FileVarStore(string path, StorageDiagnostics? diagnostics)
    {
        _path = path;
        _diagnostics = diagnostics;
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
            var vars = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return vars is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(vars, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _diagnostics?.RecordCorruptFile(_path, ex.Message);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IReadOnlyDictionary<string, string> vars)
    {
        var json = JsonSerializer.Serialize(vars, JsonOptions);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    /* ignore cleanup errors */
                }
            }
        }
    }
}
