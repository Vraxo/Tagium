namespace Tagium.Photino.Blazor.Services;

public enum ToastType { Info, Success, Error }

public class Toast
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Message { get; init; }
    public ToastType Type { get; init; }
}

public class ToastService
{
    public event Action? OnChange;
    public List<Toast> Toasts { get; } = [];

    public void Show(string message, ToastType type = ToastType.Info, int durationMs = 3000)
    {
        var toast = new Toast { Message = message, Type = type };
        Toasts.Add(toast);
        NotifyStateChanged();

        if (durationMs > 0)
        {
            _ = RemoveAfterDelay(toast, durationMs);
        }
    }

    public void Remove(Toast toast)
    {
        if (Toasts.Remove(toast))
        {
            NotifyStateChanged();
        }
    }

    private async Task RemoveAfterDelay(Toast toast, int delay)
    {
        await Task.Delay(delay);
        Remove(toast);
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }
}