namespace Tagium.Photino.Blazor.Services;

public class AppState
{
    // State
    public string SearchText { get; private set; } = "";
    public bool IsMissingOnly { get; private set; }
    public HashSet<long> SelectedFileIds { get; private set; } = [];

    // Events
    public event Action? OnChange;
    public event Action? OnSelectionChange;

    public void SetSearch(string text)
    {
        if (SearchText != text)
        {
            SearchText = text;
            IsMissingOnly = false;
            SelectedFileIds.Clear();
            NotifyStateChanged();
        }
    }

    public void SetMissingOnly(bool isMissing)
    {
        if (IsMissingOnly != isMissing)
        {
            IsMissingOnly = isMissing;
            SearchText = "";
            SelectedFileIds.Clear();
            NotifyStateChanged();
        }
    }

    public void SelectFile(long id, bool multiSelect, bool rangeSelect)
    {
        // Simple single/multi logic for now. Range select requires list knowledge.
        if (!multiSelect)
        {
            SelectedFileIds.Clear();
        }

        _ = SelectedFileIds.Contains(id) && multiSelect ? SelectedFileIds.Remove(id) : SelectedFileIds.Add(id);

        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
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
        OnChange?.Invoke(); // Selection is part of state
    }
}