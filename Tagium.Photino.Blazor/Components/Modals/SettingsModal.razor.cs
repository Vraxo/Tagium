using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Core.Services.DataExport;
using Tagium.Core.Services.DataMerge;
using Tagium.Core.Services.LibraryIntegrity;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class SettingsModal : IDisposable
{
    [Inject] private LibraryService Library { get; set; } = default!;
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private SettingsManager Settings { get; set; } = default!;
    [Inject] private LibraryIntegrityService IntegrityService { get; set; } = default!;
    [Inject] private DataExportService ExportService { get; set; } = default!;
    [Inject] private DatabaseMergeService MergeService { get; set; } = default!;
    [Inject] private DatabaseManager DbManager { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private ThemeService ThemeService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private enum SettingsTab { Folders, Appearance, Data }
    private SettingsTab ActiveTab { get; set; } = SettingsTab.Folders;

    private List<LibraryRoot> Roots = [];
    private IntegrityScanResult? ScanResult;
    private MergeResult? MergeResult;

    protected override void OnInitialized()
    {
        LoadRoots();
        ScanService.ScanProgress += OnScanProgress;
        ScanService.ScanCompleted += OnScanCompleted;
        Settings.SettingsChanged += OnSettingsChanged;
    }

    private void SetTab(SettingsTab tab)
    {
        ActiveTab = tab;
    }

    private void OnSettingsChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    // --- Library Folders Methods ---
    private void LoadRoots()
    {
        Roots = Library.GetRoots();
    }

    private void AddFolder()
    {
        string? folder = FileDialogService.PickFolder();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Library.AddRoot(folder);
        LoadRoots();
        _ = Task.Run(() => Library.ScanAsync());
    }

    private void ToggleRoot(LibraryRoot root)
    {
        root.IsEnabled = !root.IsEnabled;
        Library.UpdateRoot(root);
        LoadRoots();
    }

    private void RemoveRoot(LibraryRoot root)
    {
        Library.DeleteRoot(root.Id, root.Path);
        LoadRoots();
    }

    private async Task TriggerRescan()
    {
        await Task.Run(() => Library.ScanAsync());
    }

    private void OnScanProgress(int current, int total)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnScanCompleted()
    {
        _ = InvokeAsync(() =>
        {
            LoadRoots();
            StateHasChanged();
        });
    }

    private static string FormatDate(long unixTimestamp)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        return dateTime.ToString("g");
    }

    // --- Appearance Methods ---
    private void UpdateTagSidebarStyle(TagSidebarDisplayMode mode)
    {
        Settings.Update(s => s.TagSidebarStyle = mode);
    }

    private void UpdateEmojiSet(EmojiSet set)
    {
        Settings.Update(s => s.EmojiPreference = set);
    }

    private async Task OnThemeChanged(ChangeEventArgs e)
    {
        string? themeName = e.Value?.ToString();
        if (string.IsNullOrEmpty(themeName))
        {
            return;
        }

        Settings.Update(s => s.SelectedTheme = themeName);
        await ThemeService.ApplyThemeAsync(JS, themeName);
    }

    private void OnZoomChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int zoom))
        {
            Settings.Update(s => s.Zoom = zoom);
            Program.MainWindow?.SetZoom(zoom);
        }
    }

    // --- Maintenance (Data Management) Methods ---
    private async Task ScanIntegrity()
    {
        ScanResult = null;
        ScanResult = await IntegrityService.ScanAsync();
        Library.Refresh();
    }

    private void PurgeMissing()
    {
        IntegrityService.PurgeMissing();
        Library.Refresh();
        ScanResult = null;
        Toast.Show("Missing files purged.", ToastType.Success);
    }

    private async Task ExportJson()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "tagium_export.json");
        await ExportService.ExportJsonAsync(path);
        Toast.Show($"Exported to {path}", ToastType.Success);
    }

    private async Task ExportCsv()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "tagium_export.csv");
        await ExportService.ExportCsvAsync(path);
        Toast.Show($"Exported to {path}", ToastType.Success);
    }

    private void BackupDb()
    {
        string source = DbManager.LibraryDbPath;
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"tagium_backup_{DateTime.Now:yyyyMMdd}.db");
        File.Copy(source, dest, true);
        Toast.Show($"Library configuration backup saved to {dest}", ToastType.Success);
    }

    private void MergeDb()
    {
        string[]? files = FileDialogService.OpenFiles();
        if (files is null || files.Length == 0)
        {
            return;
        }

        MergeResult = MergeService.Merge(files[0]);
        Library.Refresh();
        Toast.Show("Merge complete.", ToastType.Success);
    }

    private void RestoreDb()
    {
        string[]? files = FileDialogService.OpenFiles();
        if (files is null || files.Length == 0)
        {
            return;
        }

        string source = files[0];
        string dest = DbManager.LibraryDbPath;

        try
        {
            File.Copy(source, dest, true);
            Toast.Show("Library database restored. Please restart Tagium.", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show($"Restore failed: {ex.Message}", ToastType.Error);
        }
    }

    public void Dispose()
    {
        ScanService.ScanProgress -= OnScanProgress;
        ScanService.ScanCompleted -= OnScanCompleted;
        Settings.SettingsChanged -= OnSettingsChanged;
    }
}