using AwesomeAssertions;
using Tagium.Core.Settings;

namespace Tagium.Core.Tests.Settings;

public class SettingsManagerVideoVolumeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tagium-video-volume-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsPath;

    public SettingsManagerVideoVolumeTests()
    {
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    [Fact]
    public void VideoVolume_DefaultsToFullVolume()
    {
        new SettingsManager(_settingsPath).Current.VideoVolume.Should().Be(1.0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.35)]
    [InlineData(1.0)]
    public void Update_PersistsVideoVolumeForANewManager(double volume)
    {
        SettingsManager manager = new(_settingsPath);

        manager.Update(settings => settings.VideoVolume = volume);

        SettingsManager restored = new(_settingsPath);
        restored.Current.VideoVolume.Should().Be(volume);
    }

    [Theory]
    [InlineData(-0.1, 0.0)]
    [InlineData(1.1, 1.0)]
    public void VideoVolume_ClampsValuesToSupportedRange(double input, double expected)
    {
        Settings settings = new() { VideoVolume = input };

        settings.VideoVolume.Should().Be(expected);
    }

    [Fact]
    public void Load_OutOfRangeVideoVolume_ClampsWithoutLosingOtherSettings()
    {
        File.WriteAllText(_settingsPath, """
            { "videoVolume": 1.5, "thumbnailSize": 160 }
            """);

        Settings settings = new SettingsManager(_settingsPath).Current;

        settings.VideoVolume.Should().Be(1.0);
        settings.ThumbnailSize.Should().Be(160);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
