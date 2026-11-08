using System.Text.Json;
using AwesomeAssertions;
using AppSettings = Tagium.Core.Settings.Settings;

namespace Tagium.Core.Tests.Settings;

public class FilesPerPageTests
{
    [Fact]
    public void DefaultAndExistingSettings_UseOneHundredFilesPerPage()
    {
        new AppSettings().FilesPerPage.Should().Be(100);

        // Older settings files have no FilesPerPage property.
        AppSettings? existing = JsonSerializer.Deserialize<AppSettings>("""
            { "ThumbnailSize": 160 }
            """);
        existing.Should().NotBeNull();
        existing!.FilesPerPage.Should().Be(100);
        existing.ThumbnailSize.Should().Be(160);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(250)]
    public void Setting_RoundTripsThroughJson(int pageSize)
    {
        AppSettings settings = new() { FilesPerPage = pageSize };

        string json = JsonSerializer.Serialize(settings);
        AppSettings? restored = JsonSerializer.Deserialize<AppSettings>(json);

        restored.Should().NotBeNull();
        restored!.FilesPerPage.Should().Be(pageSize);
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(500)]
    public void InvalidSavedSetting_FallsBackWithoutDiscardingOtherPreferences(int pageSize)
    {
        string json = $$"""
            { "FilesPerPage": {{pageSize}}, "ThumbnailSize": 160 }
            """;

        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);

        settings.Should().NotBeNull();
        settings!.FilesPerPage.Should().Be(100);
        settings.ThumbnailSize.Should().Be(160);
    }
}
