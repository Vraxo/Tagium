namespace Tagium.Photino.Blazor.Theming;

public sealed class AppearanceService
{
    public event Action? AppearanceChanged;

    public void NotifyChanged()
    {
        AppearanceChanged?.Invoke();
    }
}
