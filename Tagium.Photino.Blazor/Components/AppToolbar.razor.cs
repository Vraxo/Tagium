using Microsoft.AspNetCore.Components.Web;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    private async void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.CtrlKey || e.MetaKey)
        {
            switch (e.Key.ToLower())
            {
                case "f":
                    await SearchInput.FocusAsync();
                    break;
                case "t":
                    ToggleTagView();
                    break;
                case "r":
                    Library.Refresh();
                    break;
                case "z":
                    AppState.Undo();
                    break;
            }
        }
    }

    private async Task ImportFiles()
    {
        var paths = FileDialogService.OpenFiles();
        if (paths is { Length: > 0 })
        {
            await Library.ImportBatchAsync(paths);
        }
    }

    private void ToggleTagView()
    {
        AppState.SwitchView(
            AppState.CurrentView == AppView.Tags ? AppView.Files : AppView.Tags
        );
    }

    public void Dispose() { }
}