using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class TagSidebar
{
    private List<Tag> Tags = [];

    private bool IsAllFilesActive => string.IsNullOrEmpty(AppState.SearchText)
        && !AppState.IsMissingOnly
        && AppState.CurrentView == AppView.Files;

    private bool IsMissingActive => AppState.IsMissingOnly
        && AppState.CurrentView == AppView.Files;

    private string GetDisplayModeClass()
    {
        return Settings.Current.TagSidebarStyle.ToString().ToLowerInvariant();
    }

    protected override void OnInitialized()
    {
        Tags = Library.GetTags();
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += RefreshData;
        Settings.SettingsChanged += StateHasChanged;
    }

    private void RefreshData()
    {
        Tags = Library.GetTags();
        _ = InvokeAsync(StateHasChanged);
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