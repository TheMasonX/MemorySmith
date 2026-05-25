using MudBlazor;

namespace MemorySmith.App.Services;

public static class DestructiveActionDialogExtensions
{
    public static async Task<bool> ConfirmDestructiveActionAsync(
        this IDialogService dialogService,
        string title,
        string message,
        string confirmText)
    {
        ArgumentNullException.ThrowIfNull(dialogService);

        var result = await dialogService.ShowMessageBoxAsync(
            title,
            $"{message}\n\nChoose Cancel to keep the current state.",
            confirmText,
            "Cancel",
            null,
            CreateDialogOptions());

        return result == true;
    }

    private static DialogOptions CreateDialogOptions() => new()
    {
        CloseButton = true,
        CloseOnEscapeKey = true,
        FullWidth = true,
        MaxWidth = MaxWidth.Small
    };
}