using System.Text.Json;
using AwesomeAssertions;
using Tagium.Core.Settings;
using AppSettings = Tagium.Core.Settings.Settings;

namespace Tagium.Core.Tests.Settings;

public class FileScrollModeTests
{
    [Fact]
    public void Default_IsPagination()
    {
        new AppSettings().FileScrollMode.Should().Be(FileScrollMode.Paginated);
    }

    [Fact]
    public void OlderSettingsFile_WithoutTheProperty_KeepsPagination()
    {
        // Settings written before infinite scroll existed must not change behavior.
        AppSettings? existing = JsonSerializer.Deserialize<AppSettings>("""
            { "FilesPerPage": 250, "ThumbnailSize": 160 }
            """);

        existing.Should().NotBeNull();
        existing!.FileScrollMode.Should().Be(FileScrollMode.Paginated);
        existing.FilesPerPage.Should().Be(250);
        existing.ThumbnailSize.Should().Be(160);
    }

    [Theory]
    [InlineData(FileScrollMode.Paginated)]
    [InlineData(FileScrollMode.Infinite)]
    public void Setting_RoundTripsThroughJson(FileScrollMode mode)
    {
        AppSettings settings = new() { FileScrollMode = mode, FilesPerPage = 50 };

        string json = JsonSerializer.Serialize(settings);
        AppSettings? restored = JsonSerializer.Deserialize<AppSettings>(json);

        restored.Should().NotBeNull();
        restored!.FileScrollMode.Should().Be(mode);
        restored.FilesPerPage.Should().Be(50);
    }
}
