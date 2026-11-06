using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Settings;
using Tagium.Core.Tags;

namespace Tagium.Photino.Blazor.Tags;

public partial class TagManagerView
{
    private List<Tag> Tags = [];
    private string NewTagName = "";
    private string TagSearch = "";
    private bool UnusedOnly;
    private bool _isLoading = true;
    private bool _isDisposed;
    private Guid? ActiveColorPickerId;

    private static readonly List<string> Palette =
    [
        "#e53935",
        "#d81b60",
        "#8e24aa",
        "#5e35b1",
        "#3949ab",
        "#1e88e5",
        "#039be5",
        "#00acc1",
        "#00897b",
        "#43a047",
        "#7cb342",
        "#c0ca33",
        "#fdd835",
        "#ffb300",
        "#fb8c00",
        "#f4511e",
        "#6d4c41",
        "#757575",
        "#546e7a",
        "#FFFFFF",
        "#000000"
    ];

    private bool CanAddTag => !string.IsNullOrWhiteSpace(NewTagName);

    private IEnumerable<Tag> FilteredTags
    {
        get
        {
            string filter = TagSearch.Trim();
            return Tags.Where(tag =>
                (!UnusedOnly || tag.FileCount == 0)
                && (filter.Length == 0
                    || tag.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (tag.Emoji?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)));
        }
    }

    private int FilteredTagCount => FilteredTags.Count();

    protected override void OnInitialized()
    {
        RefreshTags();
        Library.LibraryChanged += RefreshTags;
        Settings.SettingsChanged += StateHasChanged;
    }

    private void RefreshTags()
    {
        _ = Task.Run(async () =>
        {
            List<Tag> tags = Library.GetTags(Settings.Current.TagSortBy, Settings.Current.TagSortDirection);
            await InvokeAsync(() =>
            {
                if (_isDisposed)
                {
                    return;
                }

                Tags = tags;
                _isLoading = false;
                StateHasChanged();
            });
        });
    }

    private void HandleKeyUp(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            AddTag();
        }
    }

    private void AddTag()
    {
        string name = NewTagName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        NewTagName = "";
        TagSearch = "";
        Library.AddTag(name);
    }

    private void DeleteTag(Guid id)
    {
        if (ActiveColorPickerId == id)
        {
            ActiveColorPickerId = null;
        }

        Library.DeleteTag(id);
    }

    private void UpdateTagName(Tag tag, string? newName)
    {
        string name = newName?.Trim() ?? "";
        if (name.Length == 0 || string.Equals(name, tag.Name, StringComparison.Ordinal))
        {
            return;
        }

        tag.Name = name;
        Library.UpdateTag(tag);
    }

    private void UpdateTagEmoji(Tag tag, string? newEmoji)
    {
        string? emoji = string.IsNullOrWhiteSpace(newEmoji) ? null : newEmoji.Trim();
        if (string.Equals(tag.Emoji, emoji, StringComparison.Ordinal))
        {
            return;
        }

        tag.Emoji = emoji;
        Library.UpdateTag(tag);
    }

    private void ToggleColorPicker(Guid id)
    {
        ActiveColorPickerId = ActiveColorPickerId == id ? null : id;
    }

    private void UpdateTagColor(Tag tag, string color)
    {
        if (string.Equals(tag.ColorHex, color, StringComparison.OrdinalIgnoreCase))
        {
            ActiveColorPickerId = null;
            return;
        }

        tag.ColorHex = color;
        Library.UpdateTag(tag);
        ActiveColorPickerId = null;
    }

    private void ToggleSort(TagSortBy sortBy)
    {
        Settings.Update(settings =>
        {
            if (settings.TagSortBy == sortBy)
            {
                settings.TagSortDirection = settings.TagSortDirection == SortDirection.Asc
                    ? SortDirection.Desc
                    : SortDirection.Asc;
            }
            else
            {
                settings.TagSortBy = sortBy;
                settings.TagSortDirection = SortDirection.Asc;
            }
        });

        RefreshTags();
    }

    private string GetSortArrow(TagSortBy sortBy)
    {
        if (Settings.Current.TagSortBy != sortBy)
        {
            return "↕";
        }

        return Settings.Current.TagSortDirection == SortDirection.Asc ? "↑" : "↓";
    }

    private string GetAriaSort(TagSortBy sortBy)
    {
        if (Settings.Current.TagSortBy != sortBy)
        {
            return "none";
        }

        return Settings.Current.TagSortDirection == SortDirection.Asc ? "ascending" : "descending";
    }

    private void BrowseTag(Tag tag)
    {
        AppState.SetSearch(tag.Name);
    }

    private void ClearSearch()
    {
        TagSearch = "";
    }

    private void ClearFilters()
    {
        TagSearch = "";
        UnusedOnly = false;
    }

    public void Dispose()
    {
        _isDisposed = true;
        Library.LibraryChanged -= RefreshTags;
        Settings.SettingsChanged -= StateHasChanged;
    }
}
