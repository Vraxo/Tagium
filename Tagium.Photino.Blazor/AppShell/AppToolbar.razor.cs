using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor;
using Tagium.Photino.Blazor.Files;
using Tagium.Photino.Blazor.Search;

namespace Tagium.Photino.Blazor.AppShell;

public partial class AppToolbar : IDisposable
{
    [Inject] private ModalService ModalService { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private SearchInput? SearchInput { get; set; }

    private ElementReference ViewOptionsButton { get; set; }

    private bool IsMenuOpen = false;

    protected override void OnInitialized()
    {
        Program.ViewOptionsPopupClosed += OnViewOptionsPopupClosed;
    }

    public async Task FocusSearchAsync()
    {
        if (SearchInput != null)
        {
            await SearchInput.FocusAsync();
        }
    }

    private async Task ToggleMenu()
    {
        if (Program.UsesNativeMica)
        {
            if (Program.IsViewOptionsWindowOpen)
            {
                Program.CloseViewOptionsWindow();
                return;
            }

            ViewOptionsAnchor? anchor = await JS.InvokeAsync<ViewOptionsAnchor?>(
                "themeManager.getPopupAnchor",
                ViewOptionsButton);

            if (anchor is not null)
            {
                IsMenuOpen = Program.OpenViewOptionsWindow(anchor);
            }

            return;
        }

        IsMenuOpen = !IsMenuOpen;
    }

    private void OnViewOptionsPopupClosed()
    {
        _ = InvokeAsync(() =>
        {
            IsMenuOpen = false;
            StateHasChanged();
        });
    }

    private void CloseMenu()
    {
        IsMenuOpen = false;
    }

    private void SetViewMode(FileViewMode mode)
    {
        Settings.Update(s => s.FileViewMode = mode);
    }

    private void OpenSettings()
    {
        Program.OpenSettingsWindow();
    }

    private void ShowDuplicates()
    {
        CloseMenu();
        ModalService.Show("Duplicate Files", builder =>
        {
            builder.OpenComponent<DuplicatesModal>(0);
            builder.CloseComponent();
        });
    }

    private void ShowStats()
    {
        CloseMenu();
        ModalService.Show("Library Statistics", builder =>
        {
            builder.OpenComponent<StatsModal>(0);
            builder.CloseComponent();
        });
    }

    public void Dispose()
    {
        Program.ViewOptionsPopupClosed -= OnViewOptionsPopupClosed;
    }
}
