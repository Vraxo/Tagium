using Tagium.Core.Files;

namespace Tagium.Core.Settings;

public class Settings
{
    private int _filesPerPage = FilePagination.DefaultPageSize;

    public int FilesPerPage
    {
        get => _filesPerPage;
        set => _filesPerPage = FilePagination.NormalizePageSize(value);
    }

    /// <summary>
    /// How the file views move through a result set. <see cref="FileScrollMode.Paginated"/>
    /// shows one page at a time with page controls; <see cref="FileScrollMode.Infinite"/>
    /// appends batches of <see cref="FilesPerPage"/> files as the user scrolls.
    /// </summary>
    public FileScrollMode FileScrollMode { get; set; } = FileScrollMode.Paginated;

    public TagDisplayMode TagDisplay { get; set; } = TagDisplayMode.ColorCircle;
    public EmojiSet EmojiPreference { get; set; } = EmojiSet.SystemDefault;
    public InterfaceStyle InterfaceStyle { get; set; } = Tagium.Core.Settings.InterfaceStyle.Classic;

    // Reads legacy settings files written before InterfaceStyle was introduced.
    // False is intentionally a no-op so it cannot overwrite a newer non-macOS style.
    public bool MacOSAppearance
    {
        get => InterfaceStyle == Tagium.Core.Settings.InterfaceStyle.MacOS;
        set
        {
            if (value)
            {
                InterfaceStyle = Tagium.Core.Settings.InterfaceStyle.MacOS;
            }
        }
    }

    public InterfaceCornerStyle CornerStyle { get; set; } = InterfaceCornerStyle.Default;
    public int ThumbnailSize { get; set; } = 120;
    public SortBy FileSortBy { get; set; } = SortBy.DateAdded;
    public SortDirection FileSortDirection { get; set; } = SortDirection.Desc;
    public TagSidebarDisplayMode TagSidebarStyle { get; set; } = TagSidebarDisplayMode.Pill;
    public TagSortBy TagSortBy { get; set; } = TagSortBy.Name;
    public SortDirection TagSortDirection { get; set; } = SortDirection.Asc;
    public List<Guid> RecentTagIds { get; set; } = [];
    public List<string> RecentSearches { get; set; } = [];
    public FileViewMode FileViewMode { get; set; } = FileViewMode.Grid;
    public string SelectedTheme { get; set; } = "Default";
    public int LeftSidebarWidth { get; set; } = 250;
    public int RightSidebarWidth { get; set; } = 320;
    public int Zoom { get; set; } = 100;

    private double _videoVolume = 1.0;

    /// <summary>The last selected video-player volume, from 0 (silent) to 1 (full volume).</summary>
    public double VideoVolume
    {
        get => _videoVolume;
        set => _videoVolume = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
    }

    /// <summary>
    /// Copies every persisted preference from a freshly loaded instance into this one.
    /// <see cref="SettingsManager.Reload"/> uses it so components that hold
    /// <see cref="SettingsManager.Current"/> keep reading the same object.
    /// </summary>
    public void CopyFrom(Settings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        FilesPerPage = other.FilesPerPage;
        FileScrollMode = other.FileScrollMode;
        TagDisplay = other.TagDisplay;
        EmojiPreference = other.EmojiPreference;
        InterfaceStyle = other.InterfaceStyle;
        CornerStyle = other.CornerStyle;
        ThumbnailSize = other.ThumbnailSize;
        FileSortBy = other.FileSortBy;
        FileSortDirection = other.FileSortDirection;
        TagSidebarStyle = other.TagSidebarStyle;
        TagSortBy = other.TagSortBy;
        TagSortDirection = other.TagSortDirection;
        RecentTagIds = other.RecentTagIds ?? [];
        RecentSearches = other.RecentSearches ?? [];
        FileViewMode = other.FileViewMode;
        SelectedTheme = other.SelectedTheme;
        LeftSidebarWidth = other.LeftSidebarWidth;
        RightSidebarWidth = other.RightSidebarWidth;
        Zoom = other.Zoom;
        VideoVolume = other.VideoVolume;
    }
}
