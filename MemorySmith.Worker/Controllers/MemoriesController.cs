using Microsoft.AspNetCore.Mvc;
using MemorySmith.Core.Models;
using MemorySmith.Storage;

namespace MemorySmith.Worker.Controllers;

[ApiController]
[Route("api/memories")]
public class MemoriesController : ControllerBase
{
    private readonly IMemoryStore _store;

    public MemoriesController(IMemoryStore store)
    {
        _store = store;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        var records = _store.LoadAll().Select(r => new
        {
            r.Id, r.Title, r.Status, r.Confidence, r.Tags, r.UsageCount, r.LastUpdated
        });
        return Ok(records);
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var record = _store.Load(id);
        if (record is null) return NotFound();
        return Ok(record);
    }

    [HttpPost]
    public IActionResult Create([FromBody] MemoryRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Id))
            record.Id = Guid.NewGuid().ToString();
        record.LastUpdated = DateTime.UtcNow;
        _store.Save(record);
        return CreatedAtAction(nameof(Get), new { id = record.Id }, record);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var existing = _store.Load(id);
        if (existing is null) return NotFound();
        _store.Delete(id);
        return NoContent();
    }

    [HttpPost("search")]
    public IActionResult Search([FromBody] SearchRequest request)
    {
        var records = _store.LoadAll();
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = request.Query.ToLowerInvariant();
            records = records.Where(r =>
                r.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }
        if (request.Status.HasValue)
            records = records.Where(r => r.Status == request.Status.Value);
        return Ok(records.ToList());
    }

    [HttpPost("{id}/usage")]
    public IActionResult IncrementUsage(string id)
    {
        var record = _store.Load(id);
        if (record is null) return NotFound();
        record.UsageCount++;
        record.LastUpdated = DateTime.UtcNow;
        _store.Save(record);
        return Ok(new { record.UsageCount });
    }
}

public class SearchRequest
{
    public string? Query { get; set; }
    public MemoryStatus? Status { get; set; }
}
