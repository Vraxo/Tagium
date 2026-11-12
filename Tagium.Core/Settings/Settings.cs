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

    public TagDisplayMode TagDisplay { get; set; } = TagDisplayMode.ColorCircle;
    public EmojiSet EmojiPreference { get; set; } = EmojiSet.SystemDefault;
    public bool MacOSAppearance { get; set; } = false;
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
}
