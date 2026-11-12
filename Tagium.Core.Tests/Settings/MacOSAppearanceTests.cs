using AwesomeAssertions;
using Tagium.Core.Settings;
using AppSettings = Tagium.Core.Settings.Settings;
using System.Text.Json;

namespace Tagium.Core.Tests.Settings;

public class MacOSAppearanceTests
{
    [Fact]
    public void OlderSettings_DefaultToOriginalAppearance()
    {
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>("{ \"ThumbnailSize\": 160 }");
        settings.Should().NotBeNull();
        settings!.MacOSAppearance.Should().BeFalse();
        settings.ThumbnailSize.Should().Be(160);
    }

    [Fact]
    public void AppearancePreference_IsSavedAndRestored()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tagium-appearance-{Guid.NewGuid():N}.json");
        try
        {
            SettingsManager manager = new(path);
            manager.Update(s => s.MacOSAppearance = true);
            new SettingsManager(path).Current.MacOSAppearance.Should().BeTrue();
            manager.Update(s => s.MacOSAppearance = false);
            new SettingsManager(path).Current.MacOSAppearance.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
