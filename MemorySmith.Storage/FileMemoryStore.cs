using System.Text.Json;
using System.Text.RegularExpressions;
using MemorySmith.Core.Models;

namespace MemorySmith.Storage;

/// <summary>
/// Provides a file system–based implementation of the <see cref="IMemoryStore"/> interface for persisting, retrieving, and managing memory records as JSON files organized by status.
/// </summary>
/// <remarks>
/// This class organizes memory records into subdirectories under a specified base path, with each
/// subdirectory corresponding to a memory status.
/// Records are stored as individual JSON files named after their unique identifiers.
/// The implementation supports loading, saving, deleting, and enumerating memory records, and is suitable
/// for scenarios where a lightweight, file-based persistence mechanism is required.
/// Ensure that the application has appropriate file system permissions for the base directory and its subfolders.
/// </remarks>
public partial class FileMemoryStore : IMemoryStore
{
    private readonly string _basePath;
    private readonly StorageDiagnostics? _diagnostics;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Initializes a new instance of the FileMemoryStore class using the specified base directory path.
    /// </summary>
    /// <remarks>
    /// This constructor creates subdirectories for each memory status under the specified base path if they do not already exist.
    /// Ensure the application has sufficient permissions to create directories at the given location.
    /// </remarks>
    /// <param name="basePath">
    /// The root directory path where memory store data will be organized and persisted.
    /// Must be a valid, accessible file system path.
    /// </param>
    public FileMemoryStore(string basePath)
        : this(basePath, null)
    {
    }

    public FileMemoryStore(string basePath, StorageDiagnostics? diagnostics)
    {
        _basePath = basePath;
        _diagnostics = diagnostics;
        foreach (var status in Enum.GetValues<MemoryStatus>())
            Directory.CreateDirectory(Path.Combine(_basePath, status.ToString()));
    }

    /// <summary>
    /// Loads a memory record by its unique identifier, searching across all status folders. Returns <see langword="null"/> if no matching record is found.
    /// </summary>
    /// <param name="id">The unique identifier of the memory record to load. Cannot be <see langword="null"/>.</param>
    /// <returns>The memory record with the specified identifier, or <see langword="null"/> if not found.</returns>
    public MemoryRecord? Load(string id)
    {
        lock (_lock)
        {
            var sanitizedId = SanitizeId(id);
            var path = FindFile(sanitizedId);
            if (path is null) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MemoryRecord>(json);
        }
    }

    /// <summary>
    /// Saves the specified memory record to persistent storage, updating or replacing any existing record with the same identifier.
    /// Uses atomic write pattern (temp file + move) to ensure data integrity.
    /// </summary>
    /// <remarks>
    /// If a record with the same identifier exists in a different status folder, it is removed before saving the new record.
    /// The record is serialized to JSON and stored in a file named after its identifier within a folder corresponding to its status.
    /// Writes are atomic: the file is written to a temp file and then moved (renamed) to the final location, ensuring
    /// that either the entire operation succeeds or the original file remains unchanged.
    /// </remarks>
    /// <param name="record">
    /// The memory record to save. Must not be <see langword="null"/>.
    /// The record's status determines the storage location.
    /// </param>
    public void Save(MemoryRecord record)
    {
        lock (_lock)
        {
            // Sanitize the ID to prevent path traversal attacks
            record.Id = SanitizeId(record.Id);

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

            // ATOMIC: write to temp file, then move (rename is atomic on most filesystems)
            var tempPath = Path.Combine(dir, $".{record.Id}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(record, JsonOptions);
                File.WriteAllText(tempPath, json);

                // Move is atomic on NTFS/ext4/etc.
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                // Cleanup temp file if move failed
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        /* ignore cleanup errors */
                    }
                }
            }
        }
    }

    /// <summary>
    /// Deletes the file associated with the specified identifier, if it exists.
    /// </summary>
    /// <param name="id">The unique identifier of the file to delete. Cannot be <see langword="null"/>.</param>
    public void Delete(string id)
    {
        lock (_lock)
        {
            var sanitizedId = SanitizeId(id);
            var path = FindFile(sanitizedId);
            if (path is not null) File.Delete(path);
        }
    }

    /// <summary>
    /// Retrieves all memory records by loading and deserializing JSON files from the base directory and its subdirectories.
    /// </summary>
    /// <remarks>Only files with a <c>".json"</c> extension are considered. If a file cannot be read or deserialized,
    /// it is ignored and not included in the results. The enumeration is performed lazily as the collection is iterated.</remarks>
    /// <returns>
    /// An enumerable collection of <see cref="MemoryRecord"/> objects loaded from valid JSON files.
    /// Corrupt or unreadable files are skipped.
    /// </returns>
    public IEnumerable<MemoryRecord> LoadAll()
    {
        lock (_lock)
        {
            var records = new List<MemoryRecord>();

            foreach (var file in Directory.EnumerateFiles(_basePath, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var record = JsonSerializer.Deserialize<MemoryRecord>(json);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (Exception ex)
                {
                    _diagnostics?.RecordCorruptFile(file, ex.Message);
                }
            }

            return records;
        }
    }

    /// <summary>
    /// Sanitizes a memory record ID by removing or replacing unsafe characters that could enable path traversal attacks.
    /// </summary>
    /// <param name="id">The ID to sanitize.</param>
    /// <returns>The sanitized ID with unsafe characters replaced with underscores.</returns>
    private static string SanitizeId(string id)
    {
        // Replace path separators and other unsafe characters with underscore
        return UnsafeIdCharacters().Replace(id, "_");
    }

    [GeneratedRegex(@"[/\\:?*]")]
    private static partial Regex UnsafeIdCharacters();

    /// <summary>
    /// Searches all status folders for a file matching the given ID and returns its path, or <see langword="null"/> if not found.
    /// </summary>
    /// <param name="id">The ID of the memory record to find.</param>
    /// <returns>The path to the file if found; otherwise, <see langword="null"/>.</returns>
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
