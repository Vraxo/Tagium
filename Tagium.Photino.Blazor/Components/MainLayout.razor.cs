using Microsoft.JSInterop;
using Tagium.Photino.Blazor.Components.Views;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class MainLayout : IDisposable
{
    private bool _importInProgress = false;
    private int _importProcessed = 0;
    private int _importTotal = 0;
    private double _importProgress = 0;
    private DotNetObjectReference<MainLayout>? _objRef;
    private FilesViewToolbar? _filesViewToolbar;

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
        ImportService.ImportCompleted += ShowImportStatus;
        ImportService.ImportProgress += UpdateImportProgress;
        AppState.OnChange += StateHasChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _objRef = DotNetObjectReference.Create(this);

        await JS.InvokeVoidAsync(
            "layoutResizer.init",
            "sidebar-panel",
            "splitter-left",
            "inspector-panel",
            "splitter-right");

        await JS.InvokeVoidAsync("globalShortcuts.init", _objRef);
    }

    [JSInvokable]
    public async Task HandleGlobalKeyDown(string key, bool ctrlOrMeta, bool shiftKey)
    {
        if (!ctrlOrMeta)
        {
            return;
        }

        switch (key.ToLower())
        {
            case "z":
                AppState.Undo();
                break;
            case "f":
                if (_filesViewToolbar != null)
                {
                    await _filesViewToolbar.FocusSearchAsync();
                }
                break;
            case "t":
                AppState.SwitchView(AppState.CurrentView == AppView.Tags
                    ? AppView.Files
                    : AppView.Tags);
                break;
            case "r":
                Library.Refresh();
                break;
        }
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
            Library.Refresh();
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
        ImportService.ImportCompleted -= ShowImportStatus;
        ImportService.ImportProgress -= UpdateImportProgress;
        AppState.OnChange -= StateHasChanged;

        _objRef?.Dispose();
    }
}