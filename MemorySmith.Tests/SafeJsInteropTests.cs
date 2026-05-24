using MemorySmith.App.Components;
using Microsoft.JSInterop;

namespace MemorySmith.Tests;

[TestFixture]
public class SafeJsInteropTests
{
    [Test]
    public async Task TryInvokeVoidAsync_ReturnsTrue_WhenJsCallSucceeds()
    {
        var jsRuntime = new StubJsRuntime();

        var result = await SafeJsInterop.TryInvokeVoidAsync(jsRuntime, "memorySmith.markdown.renderEnhancements", "pane");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(jsRuntime.Calls, Has.Count.EqualTo(1));
            Assert.That(jsRuntime.Calls[0].Identifier, Is.EqualTo("memorySmith.markdown.renderEnhancements"));
        });
    }

    [Test]
    public async Task TryInvokeVoidAsync_ReturnsFalse_OnJsException()
    {
        var jsRuntime = new StubJsRuntime
        {
            ExceptionToThrow = new JSException("missing function")
        };

        var result = await SafeJsInterop.TryInvokeVoidAsync(jsRuntime, "memorySmith.markdown.renderEnhancements", "pane");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task TryInvokeVoidAsync_ReturnsFalse_OnJsDisconnectedException()
    {
        var jsRuntime = new StubJsRuntime
        {
            ExceptionToThrow = new JSDisconnectedException("circuit disconnected")
        };

        var result = await SafeJsInterop.TryInvokeVoidAsync(jsRuntime, "memorySmith.markdown.renderEnhancements", "pane");

        Assert.That(result, Is.False);
    }

    [Test]
    public void TryInvokeVoidAsync_Throws_OnMissingIdentifier()
    {
        var jsRuntime = new StubJsRuntime();

        Assert.That(
            async () => await SafeJsInterop.TryInvokeVoidAsync(jsRuntime, string.Empty),
            Throws.TypeOf<ArgumentException>());
    }

    private sealed class StubJsRuntime : IJSRuntime
    {
        public Exception? ExceptionToThrow { get; init; }

        public List<JsCall> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(new JsCall(identifier, args));
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return new ValueTask<TValue>(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed record JsCall(string Identifier, object?[]? Args);
}
