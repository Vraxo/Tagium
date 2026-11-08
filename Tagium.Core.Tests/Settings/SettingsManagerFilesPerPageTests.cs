using AwesomeAssertions;
using Tagium.Core.Settings;

namespace Tagium.Core.Tests.Settings;

public class SettingsManagerFilesPerPageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tagium-settings-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsPath;

    public SettingsManagerFilesPerPageTests()
    {
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(250)]
    public void Update_SavesPageSizeForANewManager(int pageSize)
    {
        SettingsManager manager = new(_settingsPath);
        int notifications = 0;
        manager.SettingsChanged += () => notifications++;

        manager.Update(settings =>
        {
            settings.FilesPerPage = pageSize;
            settings.ThumbnailSize = 160;
        });

        File.Exists(_settingsPath).Should().BeTrue();
        notifications.Should().Be(1);
        SettingsManager restored = new(_settingsPath);
        restored.Current.FilesPerPage.Should().Be(pageSize);
        restored.Current.ThumbnailSize.Should().Be(160);
    }

    [Fact]
    public void Load_OldSettingsWithoutPageSize_UsesDefault()
    {
        File.WriteAllText(_settingsPath, """
            { "ThumbnailSize": 160, "FileViewMode": "List" }
            """);

        SettingsManager manager = new(_settingsPath);

        manager.Current.FilesPerPage.Should().Be(100);
        manager.Current.ThumbnailSize.Should().Be(160);
        manager.Current.FileViewMode.Should().Be(FileViewMode.List);
    }

    [Fact]
    public void Load_UnsupportedPageSize_PreservesOtherSettings()
    {
        File.WriteAllText(_settingsPath, """
            { "filesPerPage": 120, "thumbnailSize": 160 }
            """);

        SettingsManager manager = new(_settingsPath);

        manager.Current.FilesPerPage.Should().Be(100);
        manager.Current.ThumbnailSize.Should().Be(160);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
