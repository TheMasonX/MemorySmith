using MemorySmith.Core.Models;

namespace MemorySmith.Storage;

/// <summary>
/// Defines a contract for persisting and retrieving memory state transition events.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Appends an event to the event store (append-only).
    /// </summary>
    /// <param name="event">The event to persist.</param>
    void AppendEvent(MemoryEvent @event);

    /// <summary>
    /// Retrieves events with optional filtering by memory ID and date range.
    /// </summary>
    /// <param name="memoryId">Optional: filter by memory record ID.</param>
    /// <param name="since">Optional: return only events after this timestamp.</param>
    /// <returns>Enumerable of matching events.</returns>
    IEnumerable<MemoryEvent> GetEvents(string? memoryId = null, DateTime? since = null);
}
