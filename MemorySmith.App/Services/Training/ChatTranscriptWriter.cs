using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services.Training;

public interface IChatTranscriptWriter
{
    Task WriteAsync(ChatTurnRecord record, ChatTurnContent? content, CancellationToken cancellationToken);
}

public sealed class ChatTranscriptWriter : IChatTranscriptWriter
{
    private static readonly Regex BearerPattern = new(@"\bBearer\s+[A-Za-z0-9._\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SecretPattern = new(@"\b(api[_-]?key|token|secret|password|authorization)\b\s*[:=]\s*[^\s,;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly ILogger<ChatTranscriptWriter> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ChatTranscriptWriter(IOptionsMonitor<MemorySmithOptions> options, ILogger<ChatTranscriptWriter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task WriteAsync(ChatTurnRecord record, ChatTurnContent? content, CancellationToken cancellationToken)
    {
        var training = _options.CurrentValue.Training;
        if (!training.ChatTranscriptEnabled)
        {
            return;
        }

        var date = record.Timestamp.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var directory = Path.GetFullPath(training.TranscriptDirectory);
        Directory.CreateDirectory(directory);
        DeleteExpiredTranscripts(directory, training.TranscriptRetentionDays);

        var metadataPath = Path.Combine(directory, $"{date}.jsonl");
        var contentPath = Path.Combine(directory, $"{date}.content.jsonl");
        var metadataLine = JsonSerializer.Serialize(record, JsonOptions) + "\n";

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(metadataPath, metadataLine, Encoding.UTF8, cancellationToken);
            if (training.StoreChatContent && content is not null)
            {
                if (!string.Equals(record.Id, content.Id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Transcript id mismatch. metadata={record.Id}, content={content.Id}");
                }

                var contentToPersist = training.TranscriptRedactionEnabled
                    ? content with
                    {
                        UserMessage = Redact(content.UserMessage),
                        AssistantMessage = Redact(content.AssistantMessage),
                        SystemMessages = content.SystemMessages?.Select(Redact).ToList()
                    }
                    : content;

                var contentLine = JsonSerializer.Serialize(contentToPersist, JsonOptions) + "\n";
                await File.AppendAllTextAsync(contentPath, contentLine, Encoding.UTF8, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist chat transcript entry {TurnId}.", record.Id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public static string Sha256Hex(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Redact(string value)
    {
        var redacted = BearerPattern.Replace(value, "Bearer [REDACTED]");
        redacted = SecretPattern.Replace(redacted, "$1=[REDACTED]");
        return redacted;
    }

    private static void DeleteExpiredTranscripts(string directory, int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(directory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup. Never fail chat because retention cleanup failed.
            }
        }
    }
}
