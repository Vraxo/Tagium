using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Settings;
using Tagium.Core.Tags;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Tags;

public partial class TagSidebar : IDisposable
{
    private List<Tag> Tags = [];
    private bool IsTagSearchOpen { get; set; }
    private string TagFilter { get; set; } = string.Empty;

    private ElementReference _tagSearchInput;
    private ElementReference _tagSearchToggle;
    private bool _focusTagSearchInputAfterRender;
    private bool _focusTagSearchToggleAfterRender;

    private IEnumerable<Tag> FilteredTags => string.IsNullOrWhiteSpace(TagFilter)
        ? Tags
        : Tags.Where(tag => tag.Name.Contains(TagFilter.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool HasNoTagSearchResults => !string.IsNullOrWhiteSpace(TagFilter)
        && !FilteredTags.Any();

    private bool IsAllFilesActive => string.IsNullOrEmpty(AppState.SearchText)
        && !AppState.IsMissingOnly
        && AppState.CurrentView == AppView.Files;

    private bool IsMissingActive => AppState.IsMissingOnly
        && AppState.CurrentView == AppView.Files;

    private string GetDisplayModeClass()
    {
        return Settings.Current.TagSidebarStyle.ToString().ToLowerInvariant();
    }

    private string GetTagSearchButtonLabel()
    {
        return IsTagSearchOpen ? "Close tag search" : "Search tags";
    }

    /// <summary>
    /// Sidebar filters are rendered as div rows, so Enter/Space have to be wired up by hand
    /// to match the click behaviour.
    /// </summary>
    private static void ActivateOnKey(KeyboardEventArgs e, Action action)
    {
        if (e.Key == "Enter" || e.Key == " ")
        {
            action();
        }
    }

    protected override void OnInitialized()
    {
        RefreshData();
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += RefreshData;
        Settings.SettingsChanged += StateHasChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusTagSearchInputAfterRender)
        {
            _focusTagSearchInputAfterRender = false;
            await _tagSearchInput.FocusAsync();
        }

        if (_focusTagSearchToggleAfterRender)
        {
            _focusTagSearchToggleAfterRender = false;
            await _tagSearchToggle.FocusAsync();
        }
    }

    private void RefreshData()
    {
        _ = Task.Run(async () =>
        {
            List<Tag> tags = Library.GetTags();
            await InvokeAsync(() =>
            {
                Tags = tags;
                StateHasChanged();
            });
        });
    }

    private void ToggleTagSearch()
    {
        if (IsTagSearchOpen)
        {
            IsTagSearchOpen = false;
            TagFilter = string.Empty;
            _focusTagSearchInputAfterRender = false;
            return;
        }

        IsTagSearchOpen = true;
        _focusTagSearchInputAfterRender = true;
        _focusTagSearchToggleAfterRender = false;
    }

    private void HandleTagSearchKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            IsTagSearchOpen = false;
            TagFilter = string.Empty;
            _focusTagSearchInputAfterRender = false;
            _focusTagSearchToggleAfterRender = true;
        }
    }

    private void ClearTagFilter()
    {
        TagFilter = string.Empty;
        _focusTagSearchInputAfterRender = true;
    }

    private void SetSearch(string text)
    {
        AppState.SetSearch(text);
    }

    public void Dispose()
    {
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= RefreshData;
        Settings.SettingsChanged -= StateHasChanged;
    }
}
