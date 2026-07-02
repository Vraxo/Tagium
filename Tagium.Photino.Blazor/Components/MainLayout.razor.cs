using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Components.Views;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class MainLayout : IDisposable
{
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;

    private bool _scanInProgress = false;
    private int _scanProcessed = 0;
    private int _scanTotal = 0;
    private double _scanProgress = 0;
    private DotNetObjectReference<MainLayout>? _objRef;

    private FilesViewToolbar? FilesViewToolbarRef { get; set; }

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
        ScanService.ScanProgress += UpdateScanProgress;
        ScanService.ScanCompleted += ShowScanStatus;
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += OnLibraryChanged;
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
                if (FilesViewToolbarRef != null)
                {
                    await FilesViewToolbarRef.FocusSearchAsync();
                }
                break;
            case "t":
                AppState.SwitchView(AppState.CurrentView == AppView.Tags
                    ? AppView.Files
                    : AppView.Tags);
                break;
            case "r":
                await ScanService.ScanAllAsync();
                break;
        }
    }

    private void UpdateScanProgress(int current, int total)
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = true;
            _scanProcessed = current;
            _scanTotal = total;
            _scanProgress = total > 0 ? (double)current / total * 100 : 0;
            StateHasChanged();
        });
    }

    private void ShowScanStatus()
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = false;
            Toast.Show("Library scan complete.", ToastType.Success);
            Library.Refresh();
            StateHasChanged();
        });
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(StateHasChanged);
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
        ScanService.ScanProgress -= UpdateScanProgress;
        ScanService.ScanCompleted -= ShowScanStatus;
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= OnLibraryChanged;

        _objRef?.Dispose();
    }
}