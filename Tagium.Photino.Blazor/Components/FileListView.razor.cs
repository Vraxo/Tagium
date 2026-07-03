using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileListView : IDisposable
{
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;

    private List<FileEntry> Files = [];

    protected override void OnInitialized()
    {
        LoadFiles();

        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Library.LibraryChanged += ReloadData;
        Settings.SettingsChanged += ReloadData;
    }

    private void ReloadData()
    {
        _ = InvokeAsync(() =>
        {
            LoadFiles();
            StateHasChanged();
        });
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
    }

    private void OnRowClick(MouseEventArgs e, Guid id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private void OnRowDoubleClick(MouseEventArgs e, FileEntry file)
    {
        if (file.IsMissing)
        {
            return;
        }

        Launcher.OpenFile(file.Path);
    }

    private static string FormatDate(long unixTimestamp)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        return dateTime.ToShortDateString();
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        Library.LibraryChanged -= ReloadData;
        Settings.SettingsChanged -= ReloadData;
    }
}