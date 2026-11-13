using Windows.Foundation;
using Windows.UI.ViewManagement;
using Serilog;

namespace Tagium.Photino.Blazor.AppShell;

/// <summary>
/// Tracks the Windows system accent color for the Windows 11 interface.
/// </summary>
internal sealed class WindowsAccentColorService : IDisposable
{
    private UISettings? _settings;
    private TypedEventHandler<UISettings, object>? _colorValuesChangedHandler;
    private bool _initialized;

    public string CurrentColor { get; private set; } = "#42a5f5";

    public event Action<string>? ColorChanged;

    public void Initialize()
    {
        if (_initialized
            || !Photino.NET.PhotinoWindow.IsWindowsPlatform
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return;
        }

        try
        {
            _settings = new UISettings();
            _colorValuesChangedHandler = (_, _) => RefreshAccentColor();
            _settings.ColorValuesChanged += _colorValuesChangedHandler;
            _initialized = true;
            RefreshAccentColor();

            Log.Information("Windows system accent color tracking initialized with {AccentColor}.", CurrentColor);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not initialize Windows system accent color tracking. Using the default accent.");
            Dispose();
        }
    }

    private void RefreshAccentColor()
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            Windows.UI.Color color = _settings.GetColorValue(UIColorType.Accent);
            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (string.Equals(CurrentColor, hex, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentColor = hex;
            Log.Information("Windows system accent color changed to {AccentColor}.", hex);
            ColorChanged?.Invoke(hex);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the current Windows system accent color.");
        }
    }

    public void Dispose()
    {
        if (_settings is not null && _colorValuesChangedHandler is not null)
        {
            _settings.ColorValuesChanged -= _colorValuesChangedHandler;
        }

        _settings = null;
        _colorValuesChangedHandler = null;
        _initialized = false;
    }
}
