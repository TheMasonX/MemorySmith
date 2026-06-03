namespace MemorySmith.App.Services.Training;

/// <summary>
/// No-op transcript writer for use in tests or when transcript writing must be
/// explicitly suppressed. In normal operation, <see cref="ChatTranscriptWriter"/>
/// is registered as the default <see cref="IChatTranscriptWriter"/> implementation
/// (it already no-ops internally when <c>Training:ChatTranscriptEnabled=false</c>).
/// Override this registration in tests via
/// <c>services.AddSingleton&lt;IChatTranscriptWriter, NullChatTranscriptWriter&gt;()</c>
/// after the app's own registration to suppress transcript output.
/// </summary>
public sealed class NullChatTranscriptWriter : IChatTranscriptWriter
{
    public Task WriteAsync(ChatTurnRecord record, ChatTurnContent? content, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
