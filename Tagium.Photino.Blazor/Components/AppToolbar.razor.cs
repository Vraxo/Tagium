using Microsoft.AspNetCore.Components;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Modals;
using Tagium.Photino.Blazor.Components.Shared;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    [Inject] private ModalService ModalService { get; set; } = default!;

    private SearchInput? SearchInput { get; set; }
    private bool IsMenuOpen = false;

    public async Task FocusSearchAsync()
    {
        if (SearchInput != null)
        {
            await SearchInput.FocusAsync();
        }
    }

    private void ToggleMenu()
    {
        IsMenuOpen = !IsMenuOpen;
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
        ModalService.Show("Settings", builder =>
        {
            builder.OpenComponent<SettingsModal>(0);
            builder.CloseComponent();
        });
    }
}