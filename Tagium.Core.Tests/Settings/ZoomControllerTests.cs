using AwesomeAssertions;
using Tagium.Core.Settings;

namespace Tagium.Core.Tests.Settings;

public class ZoomControllerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tagium-zoom-").FullName;

    private SettingsManager CreateSettings() => new(Path.Combine(_directory, "settings.json"));

    [Fact]
    public void Adjust_UsesClosestStepWithLowerTieBreakAndUpdatesNativeWindow()
    {
        SettingsManager settings = CreateSettings();
        settings.Current.Zoom = 115;
        int? nativeZoom = null;
        ZoomController controller = new(settings, zoom => nativeZoom = zoom);

        controller.Adjust(1);

        settings.Current.Zoom.Should().Be(120);
        nativeZoom.Should().Be(120);
    }

    [Fact]
    public void Adjust_AtBoundaries_DoesNotUpdateSettingsOrNativeWindow()
    {
        SettingsManager settings = CreateSettings();
        int nativeCalls = 0;
        int changed = 0;
        settings.SettingsChanged += () => changed++;
        ZoomController controller = new(settings, _ => nativeCalls++);

        settings.Current.Zoom = 50;
        controller.Adjust(-1);
        settings.Current.Zoom = 200;
        controller.Adjust(1);

        settings.Current.Zoom.Should().Be(200);
        nativeCalls.Should().Be(0);
        changed.Should().Be(0);
    }

    [Fact]
    public void NativeZoomChanged_OnlyUpdatesSettingsWhenSnappedValueChanges()
    {
        SettingsManager settings = CreateSettings();
        settings.Current.Zoom = 100;
        int nativeCalls = 0;
        int changed = 0;
        settings.SettingsChanged += () => changed++;
        ZoomController controller = new(settings, _ => nativeCalls++);

        controller.OnNativeZoomChanged(102);
        controller.OnNativeZoomChanged(118);

        settings.Current.Zoom.Should().Be(120);
        changed.Should().Be(1);
        nativeCalls.Should().Be(0);
    }

    [Fact]
    public void Reset_UpdatesSettingsAndNativeWindow()
    {
        SettingsManager settings = CreateSettings();
        settings.Current.Zoom = 150;
        int? nativeZoom = null;
        ZoomController controller = new(settings, zoom => nativeZoom = zoom);

        controller.Reset();

        settings.Current.Zoom.Should().Be(100);
        nativeZoom.Should().Be(100);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
