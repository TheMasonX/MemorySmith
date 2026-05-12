using System.Net.Http.Json;
using MemorySmith.Core.Models;

namespace MemorySmith.Dashboard.Services;

public class MemoryApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<MemoryApiClient> _logger;

    public MemoryApiClient(HttpClient http, ILogger<MemoryApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<PagedResult<MemoryMetadata>?> GetMemoriesAsync(
        int page = 1, int pageSize = 20,
        MemoryStatus? status = null, string? tags = null)
    {
        var query = $"api/memories?page={page}&pageSize={pageSize}";
        if (status.HasValue) query += $"&status={status.Value}";
        if (!string.IsNullOrWhiteSpace(tags)) query += $"&tags={Uri.EscapeDataString(tags)}";
        return await _http.GetFromJsonAsync<PagedResult<MemoryMetadata>>(query);
    }

    public async Task<MemoryRecord?> GetMemoryAsync(string id) =>
        await _http.GetFromJsonAsync<MemoryRecord>($"api/memories/{id}");

    public async Task<MemoryRecord?> CreateMemoryAsync(MemoryRecord record)
    {
        try
        {
            _logger.LogInformation("Creating memory with ID: {Id}, Title: {Title}", record.Id, record.Title);
            var response = await _http.PostAsJsonAsync("api/memories", record);
            _logger.LogInformation("Create response status: {StatusCode}", response.StatusCode);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<MemoryRecord>();
                _logger.LogInformation("Memory created successfully with ID: {Id}", result?.Id);
                return result;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Create failed with status {StatusCode}: {Content}", response.StatusCode, errorContent);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating memory");
            return null;
        }
    }

    public async Task<MemoryRecord?> UpdateMemoryAsync(string id, MemoryRecord record)
    {
        var response = await _http.PutAsJsonAsync($"api/memories/{id}", record);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<MemoryRecord>()
            : null;
    }

    public async Task<bool> DeleteMemoryAsync(string id)
    {
        var response = await _http.DeleteAsync($"api/memories/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<List<MemoryRecord>?> SearchMemoriesAsync(string query, MemoryStatus? status = null, int limit = 20)
    {
        var request = new { Query = query, Status = status, Limit = limit };
        var response = await _http.PostAsJsonAsync("api/memories/search", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<List<MemoryRecord>>()
            : null;
    }

    public async Task<StatsSnapshot?> GetStatsAsync() =>
        await _http.GetFromJsonAsync<StatsSnapshot>("api/stats");

    public async Task<List<BackgroundServiceTelemetry>?> GetServiceTelemetryAsync() =>
        await _http.GetFromJsonAsync<List<BackgroundServiceTelemetry>>("api/stats/services");


    public async Task<string> GetHealthAsync()
    {
        try
        {
            var response = await _http.GetAsync("api/health/live");
            return response.IsSuccessStatusCode ? "Healthy" : "Unhealthy";
        }
        catch
        {
            return "Unavailable";
        }
    }
}

public class SearchRequest
{
    public string? Query { get; set; }
    public MemoryStatus? Status { get; set; }
    public int Limit { get; set; }
}
