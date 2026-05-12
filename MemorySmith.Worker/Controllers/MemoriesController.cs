using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using MemorySmith.Worker.Hubs;

namespace MemorySmith.Worker.Controllers;

[ApiController]
[Route("api/memories")]
public class MemoriesController : ControllerBase
{
    private readonly IMemoryStore _store;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hub;

    public MemoriesController(IMemoryStore store, IHubContext<DashboardHub, IDashboardClient> hub)
    {
        _store = store;
        _hub = hub;
    }

    [HttpGet]
    public IActionResult GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] MemoryStatus? status = null,
        [FromQuery] string? tags = null)
    {
        var records = _store.LoadAll();

        if (status.HasValue)
            records = records.Where(r => r.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(tags))
        {
            var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            records = records.Where(r => tagList.Any(t => r.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        }

        var all = records.ToList();
        var data = all
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new MemoryMetadata
            {
                Id = r.Id,
                Title = r.Title,
                Status = r.Status,
                Confidence = r.Confidence,
                Tags = r.Tags,
                UsageCount = r.UsageCount,
                LastUpdated = r.LastUpdated
            })
            .ToList();

        return Ok(new PagedResult<MemoryMetadata>
        {
            TotalCount = all.Count,
            Page = page,
            PageSize = pageSize,
            Data = data
        });
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var record = _store.Load(id);
        if (record is null) return NotFound();
        return Ok(record);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] MemoryRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Id))
            record.Id = Guid.NewGuid().ToString();
        record.LastUpdated = DateTime.UtcNow;
        _store.Save(record);
        await _hub.Clients.All.ReceiveMemoryUpdate(new MemoryUpdateEvent { Id = record.Id, Action = "Created" });
        await BroadcastStatsAsync();
        return CreatedAtAction(nameof(Get), new { id = record.Id }, record);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] MemoryRecord record)
    {
        if (_store.Load(id) is null) return NotFound();
        record.Id = id;
        record.LastUpdated = DateTime.UtcNow;
        _store.Save(record);
        await _hub.Clients.All.ReceiveMemoryUpdate(new MemoryUpdateEvent { Id = id, Action = "Updated" });
        await BroadcastStatsAsync();
        return Ok(record);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (_store.Load(id) is null) return NotFound();
        _store.Delete(id);
        await _hub.Clients.All.ReceiveMemoryUpdate(new MemoryUpdateEvent { Id = id, Action = "Deleted" });
        await BroadcastStatsAsync();
        return NoContent();
    }

    [HttpPost("search")]
    public IActionResult Search([FromBody] SearchRequest request)
    {
        var records = _store.LoadAll();

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = request.Query;
            records = records.Where(r =>
                r.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        if (request.Status.HasValue)
            records = records.Where(r => r.Status == request.Status.Value);

        var limit = request.Limit > 0 ? request.Limit : 20;
        return Ok(records.Take(limit).ToList());
    }

    [HttpPost("{id}/usage")]
    public async Task<IActionResult> IncrementUsage(string id)
    {
        var record = _store.Load(id);
        if (record is null) return NotFound();
        record.UsageCount++;
        record.LastUpdated = DateTime.UtcNow;
        _store.Save(record);
        await _hub.Clients.All.ReceiveMemoryUpdate(new MemoryUpdateEvent { Id = id, Action = "UsageIncremented" });
        await BroadcastStatsAsync();
        return Ok(new { record.UsageCount });
    }

    private async Task BroadcastStatsAsync()
    {
        var stats = StatsSnapshotFactory.Build(_store.LoadAll());
        await _hub.Clients.All.ReceiveStats(stats);
    }
}

public class SearchRequest
{
    public string? Query { get; set; }
    public MemoryStatus? Status { get; set; }
    public int Limit { get; set; } = 20;
}
