namespace Tagium.Core.Files;

/// <summary>Which of the three top-level workspaces is active.</summary>
public enum AppView { Files, Folders, Tags }

/// <summary>
/// What the workspace is showing: which of the three views is active, which filter the file
/// views are narrowed by, and whether the tag view is in graph mode.
///
/// Nothing here knows about pages, batches or selection — those belong to
/// <c>FileBrowser</c>. The two meet in <c>AppState</c>, which is the only place
/// that knows a filter change has to reload the file list and send the views back to the top.
/// </summary>
public sealed class NavigationState
{
    public AppView CurrentView { get; private set; } = AppView.Files;

    public bool IsTagGraphView { get; private set; }

    public string SearchText { get; private set; } = "";

    public bool IsMissingOnly { get; private set; }

    public string? CurrentFolderPath { get; private set; }

    /// <summary>Raised when a filter or view change alters what is displayed.</summary>
    public event Action? OnChange;

    /// <summary>Raised when the filter is narrowed to a specific folder.</summary>
    public event Action? OnFolderChanged;

    /// <summary>Raised when the filter is narrowed to a search or the missing-files view.</summary>
    public event Action? OnFilterChanged;

    public bool IsFiltered => !string.IsNullOrEmpty(SearchText) || IsMissingOnly || !string.IsNullOrEmpty(CurrentFolderPath);

    public void SwitchView(AppView view)
    {
        if (CurrentView == view)
        {
            return;
        }

        CurrentView = view;
        NotifyChanged();
    }

    public void SetTagGraphView(bool isGraphView)
    {
        if (IsTagGraphView == isGraphView)
        {
            return;
        }

        IsTagGraphView = isGraphView;
        NotifyChanged();
    }

    public void ReturnToTagGraph()
    {
        bool hasChanged = CurrentView != AppView.Tags || !IsTagGraphView;
        CurrentView = AppView.Tags;
        IsTagGraphView = true;

        if (hasChanged)
        {
            NotifyChanged();
        }
    }

    public void SetSearch(string text)
    {
        SearchText = text;
        IsMissingOnly = false;
        CurrentFolderPath = null;
        CurrentView = AppView.Files;

        OnFilterChanged?.Invoke();
        NotifyChanged();
    }

    public void SetMissingOnly(bool isMissing)
    {
        IsMissingOnly = isMissing;
        SearchText = "";
        CurrentFolderPath = null;
        CurrentView = AppView.Files;

        OnFilterChanged?.Invoke();
        NotifyChanged();
    }

    public void SetFolder(string? path)
    {
        CurrentFolderPath = path;
        SearchText = "";
        IsMissingOnly = false;
        CurrentView = AppView.Folders;

        OnFolderChanged?.Invoke();
        NotifyChanged();
    }

    private void NotifyChanged() => OnChange?.Invoke();
}
