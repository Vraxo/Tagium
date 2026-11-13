using Microsoft.AspNetCore.Components;
using Tagium.Core;
using Tagium.Core.Library;
using Tagium.Core.Maintenance;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor;
using Tagium.Photino.Blazor.AppShell;
using Tagium.Photino.Blazor.Notifications;
using Tagium.Photino.Blazor.Theming;

namespace Tagium.Photino.Blazor.Settings;

public partial class SettingsModal : IDisposable
{
    [Inject] private LibraryService Library { get; set; } = default!;
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private SettingsManager Settings { get; set; } = default!;
    [Inject] private LibraryIntegrityService IntegrityService { get; set; } = default!;
    [Inject] private DataExportService ExportService { get; set; } = default!;
    [Inject] private DatabaseManager DbManager { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private ThemeService ThemeService { get; set; } = default!;
    [Inject] private AppearanceService Appearance { get; set; } = default!;

    private enum SettingsTab { Folders, Appearance, Data }
    private SettingsTab ActiveTab { get; set; } = SettingsTab.Folders;

    private List<LibraryRoot> Roots = [];
    private IntegrityScanResult? ScanResult;
    private bool IsMissingCountLoading;
    private int MissingFileCount;
    private bool IsIntegrityScanning;
    private bool IsPurgingMissing;
    private bool IsExporting;
    private bool IsBackingUp;
    private bool IsRestoring;
    private bool _disposed;
    private int _missingCountLoadVersion;

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
        if (tab == SettingsTab.Data)
        {
            _ = RefreshMissingCountAsync();
        }
    }

    private async Task RefreshMissingCountAsync()
    {
        int requestVersion = ++_missingCountLoadVersion;
        IsMissingCountLoading = true;
        try
        {
            int count = await Task.Run(Library.GetMissingFileCount);
            if (_disposed || requestVersion != _missingCountLoadVersion)
            {
                return;
            }

            MissingFileCount = count;
        }
        catch (Exception ex)
        {
            if (!_disposed && requestVersion == _missingCountLoadVersion)
            {
                Toast.Show($"Could not read missing-file count: {ex.Message}", ToastType.Error);
            }
        }
        finally
        {
            if (!_disposed && requestVersion == _missingCountLoadVersion)
            {
                IsMissingCountLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void OnSettingsChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

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

        AddRootResult result = Library.AddRootWithValidation(folder);

        if (result.Status == AddRootStatus.Success)
        {
            Toast.Show(result.Message, ToastType.Success);
        }
        else if (result.Status == AddRootStatus.ConsolidatedChildren)
        {
            Toast.Show(result.Message, ToastType.Success);
        }
        else
        {
            Toast.Show(result.Message, ToastType.Error);
        }

        LoadRoots();

        if (result.Status is AddRootStatus.Success or AddRootStatus.ConsolidatedChildren)
        {
            _ = Task.Run(() => Library.ScanAsync());
        }
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
            if (ActiveTab == SettingsTab.Data)
            {
                _ = RefreshMissingCountAsync();
            }
        });
    }

    private static string FormatDate(long unixTimestamp)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        return dateTime.ToString("g");
    }

    private void UpdateTagSidebarStyle(TagSidebarDisplayMode mode)
    {
        Settings.Update(s => s.TagSidebarStyle = mode);
    }

    private void UpdateMacOSAppearance(ChangeEventArgs args)
    {
        bool enabled = args.Value is bool value && value;
        Settings.Update(s => s.MacOSAppearance = enabled);
        Appearance.NotifyChanged();
    }

    private void UpdateCornerStyle(InterfaceCornerStyle style)
    {
        Settings.Update(s => s.CornerStyle = style);
        Appearance.NotifyChanged();
    }

    private static string GetCornerStylePreviewClass(InterfaceCornerStyle style)
    {
        return $"corner-preview-{style.ToString().ToLowerInvariant()}";
    }

    private static string GetCornerStyleLabel(InterfaceCornerStyle style)
    {
        return style switch
        {
            InterfaceCornerStyle.Sharp => "Sharp",
            InterfaceCornerStyle.Subtle => "Subtle",
            InterfaceCornerStyle.Default => "Default",
            InterfaceCornerStyle.Rounded => "Rounded",
            _ => "Default"
        };
    }

    private void UpdateEmojiSet(EmojiSet set)
    {
        Settings.Update(s => s.EmojiPreference = set);
    }

    private void OnThemeChanged(ChangeEventArgs e)
    {
        string? themeName = e.Value?.ToString();
        if (string.IsNullOrEmpty(themeName))
        {
            return;
        }

        Settings.Update(s => s.SelectedTheme = themeName);
        Appearance.NotifyChanged();
    }

    private void OnZoomChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int zoom))
        {
            Settings.Update(s => s.Zoom = zoom);
            Program.MainWindow?.SetZoom(zoom);
            Program.SettingsWindowNative?.SetZoom(zoom);
        }
    }

    private async Task ScanIntegrity()
    {
        if (IsIntegrityScanning)
        {
            return;
        }

        IsIntegrityScanning = true;
        ScanResult = null;
        try
        {
            ScanResult = await IntegrityService.ScanAsync();
            Library.Refresh();
        }
        catch (Exception ex)
        {
            Toast.Show($"Integrity scan failed: {ex.Message}", ToastType.Error);
        }
        finally
        {
            IsIntegrityScanning = false;
        }

        await RefreshMissingCountAsync();
    }

    private async Task PurgeMissing()
    {
        if (IsPurgingMissing || MissingFileCount == 0)
        {
            return;
        }

        IsPurgingMissing = true;
        try
        {
            await IntegrityService.PurgeMissingAsync();
            MissingFileCount = 0;
            ScanResult = null;
            Library.Refresh();
            Toast.Show("Missing files purged.", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show($"Purge failed: {ex.Message}", ToastType.Error);
        }
        finally
        {
            IsPurgingMissing = false;
        }
    }

    private async Task ExportJson()
    {
        await ExportCatalogAsync("tagium_export.json", ExportService.ExportJsonAsync);
    }

    private async Task ExportCsv()
    {
        await ExportCatalogAsync("tagium_export.csv", ExportService.ExportCsvAsync);
    }

    private async Task ExportCatalogAsync(string fileName, Func<string, Task> export)
    {
        if (IsExporting)
        {
            return;
        }

        IsExporting = true;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
        try
        {
            // Export currently builds a complete catalog and performs per-file tag lookups.
            await Task.Run(() => export(path));
            Toast.Show($"Exported to {path}", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show($"Export failed: {ex.Message}", ToastType.Error);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private async Task BackupDb()
    {
        if (IsBackingUp)
        {
            return;
        }

        IsBackingUp = true;
        string source = DbManager.LibraryDbPath;
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"tagium_backup_{DateTime.Now:yyyyMMdd}.db");
        try
        {
            await Task.Run(() => File.Copy(source, dest, true));
            Toast.Show($"Library configuration backup saved to {dest}", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show($"Backup failed: {ex.Message}", ToastType.Error);
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    private async Task RestoreDb()
    {
        string[]? files = FileDialogService.OpenFiles();
        if (files is null || files.Length == 0 || IsRestoring)
        {
            return;
        }

        IsRestoring = true;
        string source = files[0];
        string dest = DbManager.LibraryDbPath;

        try
        {
            await Task.Run(() => File.Copy(source, dest, true));
            Toast.Show("Library database restored. Please restart Tagium.", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show($"Restore failed: {ex.Message}", ToastType.Error);
        }
        finally
        {
            IsRestoring = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _missingCountLoadVersion++;
        ScanService.ScanProgress -= OnScanProgress;
        ScanService.ScanCompleted -= OnScanCompleted;
        Settings.SettingsChanged -= OnSettingsChanged;
    }
}