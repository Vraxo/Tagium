using Tagium.Core.Files;

namespace Tagium.Photino.Blazor;

public partial class AppState
{
    public void SelectFile(Guid id, bool multiSelect, bool rangeSelect)
    {
        // Notify unconditionally: a Ctrl-click on an already-selected file changes nothing the
        // views render but does re-anchor, and the inspector mirrors this event.
        _selection.Select(id, multiSelect, rangeSelect, OrderedFileIds());
        NotifySelectionChanged();
    }

    /// <summary>The filtered result set in display order — the coordinate space shift-ranges use.</summary>
    private List<Guid> OrderedFileIds()
    {
        List<Guid> ids = new(_currentFiles.Count);
        foreach (FileEntry file in _currentFiles)
        {
            ids.Add(file.Id);
        }

        return ids;
    }

    public void SetSelection(IEnumerable<Guid> ids)
    {
        _selection.Set(ids);
        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        if (!_selection.Clear())
        {
            return;
        }

        NotifySelectionChanged();
    }
}
