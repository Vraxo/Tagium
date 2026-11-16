using AwesomeAssertions;
using Tagium.Core.Settings;
using AppSettings = Tagium.Core.Settings.Settings;

namespace Tagium.Core.Tests.Settings;

/// <summary>
/// A child window's <see cref="SettingsManager"/> may be a different instance from the one
/// the main window watches (and the two windows can even be separate processes). These tests
/// cover the main window picking up a save made elsewhere without restarting.
/// </summary>
public class SettingsManagerReloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tagium-settings-reload-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsPath;

    public SettingsManagerReloadTests()
    {
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    [Fact]
    public void Reload_PicksUpAScrollModeWrittenByAnotherWindow()
    {
        SettingsManager main = new(_settingsPath);
        int notifications = 0;
        main.SettingsChanged += () => notifications++;
        AppSettings live = main.Current;

        // What the Settings window does with its own SettingsManager instance.
        SettingsManager settingsWindow = new(_settingsPath);
        settingsWindow.Update(s => s.FileScrollMode = FileScrollMode.Infinite);

        main.Reload();

        notifications.Should().Be(1);
        main.Current.FileScrollMode.Should().Be(FileScrollMode.Infinite);
        main.Current.Should().BeSameAs(live);
    }

    [Fact]
    public void Reload_UpdatesTheLiveInstanceInPlace()
    {
        SettingsManager manager = new(_settingsPath);
        AppSettings live = manager.Current;

        SettingsManager other = new(_settingsPath);
        other.Update(s =>
        {
            s.FilesPerPage = 250;
            s.FileScrollMode = FileScrollMode.Infinite;
            s.ThumbnailSize = 180;
        });

        manager.Reload();

        // Components capture Settings.Current; reload must not swap the object out.
        manager.Current.Should().BeSameAs(live);
        live.FilesPerPage.Should().Be(250);
        live.FileScrollMode.Should().Be(FileScrollMode.Infinite);
        live.ThumbnailSize.Should().Be(180);
    }

    [Fact]
    public void Reload_WithAMalformedFile_KeepsTheCurrentValues()
    {
        SettingsManager manager = new(_settingsPath);
        manager.Update(s => s.FilesPerPage = 50);
        int notifications = 0;
        manager.SettingsChanged += () => notifications++;

        File.WriteAllText(_settingsPath, "{ not json ");

        manager.Reload();

        notifications.Should().Be(0);
        manager.Current.FilesPerPage.Should().Be(50);
        manager.Current.FileScrollMode.Should().Be(FileScrollMode.Paginated);
    }

    [Fact]
    public void Reload_WithoutAFile_KeepsTheCurrentValues()
    {
        SettingsManager manager = new(_settingsPath);
        int notifications = 0;
        manager.SettingsChanged += () => notifications++;

        manager.Reload();

        notifications.Should().Be(0);
        manager.Current.FileScrollMode.Should().Be(FileScrollMode.Paginated);
    }

    [Fact]
    public void CopyFrom_CopiesEveryPersistedPreference()
    {
        AppSettings target = new();
        AppSettings source = new()
        {
            FilesPerPage = 250,
            FileScrollMode = FileScrollMode.Infinite,
            ThumbnailSize = 200,
            FileViewMode = FileViewMode.List,
            SelectedTheme = "Nord",
            LeftSidebarWidth = 300,
            RightSidebarWidth = 400,
            Zoom = 110,
            VideoVolume = 0.25,
            RecentSearches = ["tag:wallpaper"],
            RecentTagIds = [Guid.NewGuid()]
        };

        target.CopyFrom(source);

        target.FilesPerPage.Should().Be(250);
        target.FileScrollMode.Should().Be(FileScrollMode.Infinite);
        target.ThumbnailSize.Should().Be(200);
        target.FileViewMode.Should().Be(FileViewMode.List);
        target.SelectedTheme.Should().Be("Nord");
        target.LeftSidebarWidth.Should().Be(300);
        target.RightSidebarWidth.Should().Be(400);
        target.Zoom.Should().Be(110);
        target.VideoVolume.Should().Be(0.25);
        target.RecentSearches.Should().Equal("tag:wallpaper");
        target.RecentTagIds.Should().BeEquivalentTo(source.RecentTagIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
