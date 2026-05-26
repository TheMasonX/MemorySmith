using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public class MemorySmithRequestGuardMiddleware
{
    public const string ApiKeyHeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;

    public MemorySmithRequestGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IOptions<MemorySmithOptions> options)
    {
        var settings = options.Value;
        var isLoopback = IsLoopback(context.Connection.RemoteIpAddress);

        if (!settings.AllowRemoteApi && !isLoopback)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Remote requests are disabled. Set MemorySmith:AllowRemoteApi=true to allow non-localhost callers.");
            return;
        }

        if (settings.AllowRemoteApi && !isLoopback && RequiresApiKey(context.Request.Path) && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Remote API requests require MemorySmith:ApiKey when MemorySmith:AllowRemoteApi=true. Configure a shared API key before exposing /api or /mcp beyond localhost.");
            return;
        }

        if (RequiresApiKey(context.Request.Path) && !string.IsNullOrWhiteSpace(settings.ApiKey) && !HasValidApiKey(context, settings.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync($"Missing or invalid {ApiKeyHeaderName} header.");
            return;
        }

        await _next(context);
    }

    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }

    public static bool RequiresApiKey(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidApiKey(HttpContext context, string expectedApiKey)
    {
        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var values))
        {
            return false;
        }

        return values.Any(value => FixedTimeEquals(value, expectedApiKey));
    }

    private static bool FixedTimeEquals(string? actual, string expected)
    {
        if (actual is null)
        {
            return false;
        }

        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return actualBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}