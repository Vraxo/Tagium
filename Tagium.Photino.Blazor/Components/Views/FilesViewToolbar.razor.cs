using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Shared;

namespace Tagium.Photino.Blazor.Components.Views;

public partial class FilesViewToolbar
{
    private bool IsMenuOpen = false;

    public async Task FocusSearchAsync()
    {
        await SearchInput.FocusAsync();
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
}