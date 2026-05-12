namespace MemorySmith.Storage;

public interface IVarStore
{
    /// <summary>Loads all defined path variables. Returns an empty dictionary if the store does not exist yet.</summary>
    IReadOnlyDictionary<string, string> Load();

    /// <summary>Persists the given variable dictionary, replacing any previous content.</summary>
    void Save(IReadOnlyDictionary<string, string> vars);
}
