using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Photino.Blazor;
using Tagium.Photino.Blazor.Search;

namespace Tagium.Photino.Blazor.AppShell;

public class KeyboardShortcutService
{
    private readonly NavigationState _navigation;
    private readonly LibraryService _library;
    private readonly SearchInput _searchInput;

    public KeyboardShortcutService(NavigationState navigation, LibraryService library, SearchInput searchInput)
    {
        _navigation = navigation;
        _library = library;
        _searchInput = searchInput;
    }

    public async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (!e.CtrlKey && !e.MetaKey)
        {
            return;
        }

        switch (e.Key.ToLower())
        {
            case "f":
                await _searchInput.FocusAsync();
                break;
            case "t":
                ToggleTagView();
                break;
            case "r":
                _library.Refresh();
                break;
        }
    }

    private void ToggleTagView()
    {
        _navigation.SwitchView(
            _navigation.CurrentView == AppView.Tags ? AppView.Files : AppView.Tags
        );
    }
}