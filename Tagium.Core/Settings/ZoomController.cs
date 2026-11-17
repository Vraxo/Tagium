namespace Tagium.Core.Settings;

/// <summary>Applies the application's discrete zoom policy to settings and the native window.</summary>
public sealed class ZoomController
{
    private static readonly int[] ZoomSteps = [50, 67, 75, 90, 100, 110, 120, 133, 150, 175, 200];
    private readonly SettingsManager _settings;
    private readonly Action<int> _setNativeZoom;

    public ZoomController(SettingsManager settings, Action<int> setNativeZoom)
    {
        _settings = settings;
        _setNativeZoom = setNativeZoom;
    }

    public void OnNativeZoomChanged(int rawZoom)
    {
        int closestZoom = ZoomSteps[ClosestIndex(rawZoom)];
        if (_settings.Current.Zoom != closestZoom)
        {
            _settings.Update(s => s.Zoom = closestZoom);
        }
    }

    public void Adjust(int direction)
    {
        int currentIndex = ClosestIndex(_settings.Current.Zoom);
        int newIndex = currentIndex + direction;
        if (newIndex >= 0 && newIndex < ZoomSteps.Length)
        {
            int targetZoom = ZoomSteps[newIndex];
            _settings.Update(s => s.Zoom = targetZoom);
            _setNativeZoom(targetZoom);
        }
    }

    public void Reset()
    {
        _settings.Update(s => s.Zoom = 100);
        _setNativeZoom(100);
    }

    private static int ClosestIndex(int zoom)
    {
        int closestIndex = 0;
        long minDiff = long.MaxValue;
        for (int i = 0; i < ZoomSteps.Length; i++)
        {
            long diff = Math.Abs((long)ZoomSteps[i] - zoom);
            if (diff < minDiff)
            {
                minDiff = diff;
                closestIndex = i;
            }
        }
        return closestIndex;
    }
}
