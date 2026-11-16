namespace Tagium.Photino.Blazor;

public partial class AppState
{
    /// <summary>
    /// Drops the selection without raising <see cref="OnSelectionChange"/>: a filter change
    /// already re-renders the whole list through <see cref="OnChange"/>, and firing both would
    /// repaint every row twice per keystroke while the user types a search.
    /// </summary>
    private void ClearSelectionCore() => _ = _selection.Clear();

    public void SwitchView(AppView view)
    {
        if (CurrentView == view)
        {
            return;
        }

        CurrentView = view;
        NotifyStateChanged();
    }

    public void SetTagGraphView(bool isGraphView)
    {
        if (IsTagGraphView == isGraphView)
        {
            return;
        }

        IsTagGraphView = isGraphView;
        NotifyStateChanged();
    }

    public void ReturnToTagGraph()
    {
        bool hasChanged = CurrentView != AppView.Tags || !IsTagGraphView;
        CurrentView = AppView.Tags;
        IsTagGraphView = true;

        if (hasChanged)
        {
            NotifyStateChanged();
        }
    }

    public void Undo()
    {
        _library.Undo();
    }

    public void SetSearch(string text)
    {
        SearchText = text;
        IsMissingOnly = false;
        CurrentFolderPath = null;
        ClearSelectionCore();
        ResetScrollPosition();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        _ = RefreshFilesAsync();
    }

    public void SetMissingOnly(bool isMissing)
    {
        IsMissingOnly = isMissing;
        SearchText = "";
        CurrentFolderPath = null;
        ClearSelectionCore();
        ResetScrollPosition();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        _ = RefreshFilesAsync();
    }

    public void SetFolder(string? path)
    {
        CurrentFolderPath = path;
        SearchText = "";
        IsMissingOnly = false;
        ClearSelectionCore();
        ResetScrollPosition();

        if (CurrentView != AppView.Folders)
        {
            CurrentView = AppView.Folders;
        }

        _ = RefreshFilesAsync();
    }
}
