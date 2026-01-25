namespace Tagium.Photino.Blazor.Services;

public enum AppView { Files, Tags }

public class AppState
{
    // State
    public AppView CurrentView { get; private set; } = AppView.Files;
    public string SearchText { get; private set; } = "";
    public bool IsMissingOnly { get; private set; }
    public HashSet<long> SelectedFileIds { get; private set; } = [];

    // Events
    public event Action? OnChange;
    public event Action? OnSelectionChange;

    public void SwitchView(AppView view)
    {
        if (CurrentView == view)
        {
            return;
        }

        CurrentView = view;
        NotifyStateChanged();
    }

    public void SetSearch(string text)
    {
        SearchText = text;
        IsMissingOnly = false;
        SelectedFileIds.Clear();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        NotifyStateChanged();
    }

    public void SetMissingOnly(bool isMissing)
    {
        IsMissingOnly = isMissing;
        SearchText = "";
        SelectedFileIds.Clear();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        NotifyStateChanged();
    }

    public void SelectFile(long id, bool multiSelect, bool rangeSelect)
    {
        if (!multiSelect)
        {
            SelectedFileIds.Clear();
            _ = SelectedFileIds.Add(id);
        }
        else
        {
            _ = SelectedFileIds.Contains(id)
                ? SelectedFileIds.Remove(id)
                : SelectedFileIds.Add(id);
        }

        NotifySelectionChanged();
    }

    public void SetSelection(IEnumerable<long> ids)
    {
        SelectedFileIds.Clear();

        foreach (long id in ids)
        {
            _ = SelectedFileIds.Add(id);
        }

        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        if (SelectedFileIds.Count <= 0)
        {
            return;
        }

        SelectedFileIds.Clear();
        NotifySelectionChanged();
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    private void NotifySelectionChanged()
    {
        OnSelectionChange?.Invoke();
    }
}