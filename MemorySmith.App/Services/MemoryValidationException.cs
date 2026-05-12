namespace MemorySmith.App.Services;

public class MemoryValidationException : Exception
{
    public MemoryValidationException(Dictionary<string, string[]> errors)
        : base("Memory validation failed.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}