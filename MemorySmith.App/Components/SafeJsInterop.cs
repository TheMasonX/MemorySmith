using Microsoft.JSInterop;

namespace MemorySmith.App.Components;

public static class SafeJsInterop
{
    public static async ValueTask<bool> TryInvokeVoidAsync(IJSRuntime jsRuntime, string identifier, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);

        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("JS interop identifier is required.", nameof(identifier));
        }

        try
        {
            await jsRuntime.InvokeVoidAsync(identifier, args);
            return true;
        }
        catch (JSException)
        {
            return false;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
    }
}
