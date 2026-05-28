using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MemorySmith.App.Services;

public sealed record RequestMetadataSnapshot(string? RequestId, string? CorrelationId, string? IpHash, string? UserAgentHash);

public static class RequestMetadata
{
    private const string HmacKeyFileName = "request-metadata-hmac.key";
    private static readonly ConcurrentDictionary<string, Lazy<byte[]>> HmacKeys = new(StringComparer.OrdinalIgnoreCase);

    public static RequestMetadataSnapshot Capture(HttpContext? httpContext, MemorySmithOptions options)
    {
        if (httpContext is null)
        {
            return new RequestMetadataSnapshot(null, null, null, null);
        }

        return new RequestMetadataSnapshot(
            Normalize(httpContext.TraceIdentifier),
            ResolveCorrelationId(httpContext),
            HashRemoteIp(httpContext, options),
            HashUserAgent(httpContext, options));
    }

    public static string? ResolveCorrelationId(HttpContext? httpContext) =>
        Normalize(Activity.Current?.TraceId.ToString()) ?? Normalize(httpContext?.TraceIdentifier);

    private static string? HashRemoteIp(HttpContext httpContext, MemorySmithOptions options)
    {
        var address = httpContext.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return HashMetadata("ip", address.ToString(), options);
    }

    private static string? HashUserAgent(HttpContext httpContext, MemorySmithOptions options)
    {
        var userAgent = Normalize(httpContext.Request.Headers.UserAgent.ToString());
        return userAgent is null ? null : HashMetadata("user-agent", userAgent, options);
    }

    private static string HashMetadata(string purpose, string value, MemorySmithOptions options)
    {
        using var hmac = new HMACSHA256(GetHmacKey(options));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{purpose}\n{value}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static byte[] GetHmacKey(MemorySmithOptions options)
    {
        var directory = Path.GetFullPath(options.DataProtectionKeysPath);
        var path = Path.Combine(directory, HmacKeyFileName);
        return HmacKeys.GetOrAdd(path, keyPath => new Lazy<byte[]>(() => LoadOrCreateHmacKey(keyPath), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static byte[] LoadOrCreateHmacKey(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(path))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (existing.Length >= 32)
                {
                    return existing;
                }
            }

            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(path, Convert.ToBase64String(key));
            return key;
        }
        catch
        {
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}