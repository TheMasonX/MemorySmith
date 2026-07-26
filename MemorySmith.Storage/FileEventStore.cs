using System.Text.Json;
using MemorySmith.Core.Models;

namespace MemorySmith.Storage;

/// <summary>
/// File system–based implementation of <see cref="IEventStore"/> for persisting memory lifecycle events to JSON files.
/// Events are stored in a single append-only log file with one JSON object per line for efficient streaming and recovery.
/// </summary>
public class FileEventStore : IEventStore
{
    private readonly string _logPath;
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly object _lock = new();
    private readonly StorageDiagnostics? _diagnostics;

    /// <summary>
    /// Initializes a new instance of FileEventStore with the specified log file path.
    /// </summary>
    /// <param name="logPath">Path to the append-only event log file.</param>
    public FileEventStore(string logPath, StorageDiagnostics? diagnostics = null)
    {
        _logPath = logPath;
        _diagnostics = diagnostics;
        // Ensure parent directory exists
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Appends an event to the log file in a thread-safe manner.
    /// Events are stored as JSON objects, one per line.
    /// </summary>
    /// <param name="event">The event to persist.</param>
    public void AppendEvent(MemoryEvent @event)
    {
        if (@event.Timestamp == default)
            @event.Timestamp = DateTime.UtcNow;

        lock (_lock)
        {
            var json = JsonSerializer.Serialize(@event, JsonOptions);
            File.AppendAllText(_logPath, json + Environment.NewLine);
        }
    }

    /// <summary>
    /// Retrieves events with optional filtering by memory ID and date range.
    /// </summary>
    /// <param name="memoryId">Optional: filter by memory record ID.</param>
    /// <param name="since">Optional: return only events after this timestamp.</param>
    /// <returns>Enumerable of matching events.</returns>
    public IEnumerable<MemoryEvent> GetEvents(string? memoryId = null, DateTime? since = null)
    {
        if (!File.Exists(_logPath))
            return Enumerable.Empty<MemoryEvent>();

        lock (_lock)
        {
            var events = new List<MemoryEvent>();
            try
            {
                foreach (var line in File.ReadLines(_logPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var @event = JsonSerializer.Deserialize<MemoryEvent>(line);
                        if (@event == null)
                            continue;

                        // Apply filters
                        if (!string.IsNullOrEmpty(memoryId) && @event.MemoryId != memoryId)
                            continue;

                        if (since.HasValue && @event.Timestamp < since.Value)
                            continue;

                        events.Add(@event);
                    }
                    catch (Exception ex)
                    {
                        _diagnostics?.RecordCorruptFile(_logPath, ex.Message);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                _diagnostics?.RecordCorruptFile(_logPath, ex.Message);
                return Enumerable.Empty<MemoryEvent>();
            }

            return events;
        }
    }
}
