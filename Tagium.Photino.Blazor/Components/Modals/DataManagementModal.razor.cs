using Tagium.Core.Services.DataMerge;
using Tagium.Core.Services.LibraryIntegrity;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class DataManagementModal
{
    private IntegrityScanResult? ScanResult;
    private MergeResult? MergeResult;

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
        string source = DbManager.ConnectionString.Replace("Data Source=", "");
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"tagium_backup_{DateTime.Now:yyyyMMdd}.db");
        File.Copy(source, dest, true);
        Toast.Show($"Backup saved to {dest}", ToastType.Success);
    }

    private void MergeDb()
    {
        string[]? files = Services.FileDialogService.OpenFiles();

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
        string[]? files = Services.FileDialogService.OpenFiles();

        if (files is null || files.Length == 0)
        {
            return;
        }

        string source = files[0];
        string dest = DbManager.ConnectionString.Replace("Data Source=", "");

        try
        {
            File.Copy(source, dest, true);
            Toast.Show("Database restored. Please restart Tagium.", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show($"Restore failed: {ex.Message}", ToastType.Error);
        }
    }
}