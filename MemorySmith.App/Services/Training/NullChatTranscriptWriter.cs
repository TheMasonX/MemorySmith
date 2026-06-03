namespace MemorySmith.App.Services.Training;

/// <summary>
/// No-op transcript writer used when training is not configured.
/// Registered as the default <see cref="IChatTranscriptWriter"/> implementation in Program.cs.
/// The real <see cref="ChatTranscriptWriter"/> is registered instead when
/// <c>Training:ChatTranscriptEnabled=true</c> is configured in appsettings.
/// </summary>
public sealed class NullChatTranscriptWriter : IChatTranscriptWriter
{
    public Task WriteAsync(ChatTurnRecord record, ChatTurnContent? content, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
