using AwesomeAssertions;
using Tagium.Core.Settings;
using AppSettings = Tagium.Core.Settings.Settings;
using System.Text.Json;

namespace Tagium.Core.Tests.Settings;

public class InterfaceStyleTests
{
    [Fact]
    public void OlderSettings_DefaultToClassicInterface()
    {
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>("{ \"ThumbnailSize\": 160 }");

        settings.Should().NotBeNull();
        settings!.InterfaceStyle.Should().Be(InterfaceStyle.Classic);
        settings.ThumbnailSize.Should().Be(160);
    }

    [Fact]
    public void LegacyMacOSPreference_MigratesToInterfaceStyle()
    {
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>("{ \"MacOSAppearance\": true }");

        settings.Should().NotBeNull();
        settings!.InterfaceStyle.Should().Be(InterfaceStyle.MacOS);
    }

    [Fact]
    public void LegacyFalsePreference_DoesNotOverrideNewInterfaceStyle()
    {
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(
            "{ \"InterfaceStyle\": \"Windows11\", \"MacOSAppearance\": false }");

        settings.Should().NotBeNull();
        settings!.InterfaceStyle.Should().Be(InterfaceStyle.Windows11);
    }

    [Fact]
    public void InterfaceStyle_IsSavedAndRestored()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tagium-interface-style-{Guid.NewGuid():N}.json");
        try
        {
            SettingsManager manager = new(path);
            manager.Update(s => s.InterfaceStyle = InterfaceStyle.Windows11);

            new SettingsManager(path).Current.InterfaceStyle.Should().Be(InterfaceStyle.Windows11);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
