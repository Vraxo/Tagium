using Microsoft.JSInterop;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class MainLayout
{
    private bool _importInProgress = false;
    private int _importProcessed = 0;
    private int _importTotal = 0;
    private double _importProgress = 0;

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
        Library.ImportCompleted += ShowImportStatus;
        Library.ImportProgress += UpdateImportProgress;
        AppState.OnChange += StateHasChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        await JS.InvokeVoidAsync(
            "layoutResizer.init",
            "sidebar-panel",
            "splitter-left",
            "inspector-panel",
            "splitter-right");
    }

    private void UpdateImportProgress(int processed, int total)
    {
        _importInProgress = processed < total;
        _importProcessed = processed;
        _importTotal = total;
        _importProgress = (double)processed / total * 100;
        _ = InvokeAsync(StateHasChanged);
    }

    private void ShowImportStatus(int added, int duplicates, int errors)
    {
        _importInProgress = false;
        int total = added + duplicates + errors;
        if (total == 0)
        {
            return;
        }

        List<string> parts = [];

        if (added > 0)
        {
            parts.Add($"{added} added");
        }

        if (duplicates > 0)
        {
            parts.Add($"{duplicates} duplicate{(duplicates == 1 ? "" : "s")}");
        }

        if (errors > 0)
        {
            parts.Add($"{errors} failed");
        }

        string message = $"Import: {string.Join(", ", parts)}";

        ToastType type = errors > 0 ? ToastType.Error : added > 0 ? ToastType.Success : ToastType.Info;
        Toast.Show(message, type);
    }

    private void ReloadCurrentView()
    {
        AppState.SetSearch("");
        AppState.ClearSelection();
        Library.Refresh();
    }

    public void Dispose()
    {
        Settings.SettingsChanged -= StateHasChanged;
        Library.ImportCompleted -= ShowImportStatus;
        Library.ImportProgress -= UpdateImportProgress;
        AppState.OnChange -= StateHasChanged;
    }
}