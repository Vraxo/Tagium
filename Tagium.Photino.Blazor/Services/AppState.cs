using Tagium.Core.Models;
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Services;

public enum AppView { Files, Folders, Tags }

public class AppState : IDisposable
{
    private readonly LibraryService _library;
    private List<FileEntry> _currentFiles = [];

    public AppView CurrentView { get; private set; } = AppView.Files;
    public string SearchText { get; private set; } = "";
    public bool IsMissingOnly { get; private set; }
    public string? CurrentFolderPath { get; private set; }
    public HashSet<Guid> SelectedFileIds { get; private set; } = [];

    // Pagination State
    public int CurrentPage { get; private set; } = 1;
    public int PageSize { get; private set; } = 120;
    public int TotalFilesCount => _currentFiles.Count;
    public int TotalPages => (int)Math.Ceiling((double)TotalFilesCount / PageSize);

    public event Action? OnChange;
    public event Action? OnSelectionChange;

    public AppState(LibraryService library)
    {
        _library = library;
        _library.LibraryChanged += OnLibraryChanged;
        _ = RefreshFilesAsync();
    }

    private void OnLibraryChanged()
    {
        _ = RefreshFilesAsync();
    }

    public async Task RefreshFilesAsync()
    {
        // Offload heavy query rendering to a background thread to prevent UI freezes
        List<FileEntry> files = await Task.Run(() => _library.GetFiles(SearchText, IsMissingOnly, CurrentFolderPath));
        _currentFiles = files;

        // Adjust page if it goes out of bounds after a filter or library change
        int total = TotalPages;
        if (CurrentPage < 1)
        {
            CurrentPage = 1;
        }
        else if (CurrentPage > total && total > 0)
        {
            CurrentPage = total;
        }

        NotifyStateChanged();
    }

    public IEnumerable<FileEntry> GetPagedFiles()
    {
        return _currentFiles
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize);
    }

    public void SetPage(int page)
    {
        if (page < 1 || page > TotalPages)
        {
            return;
        }

        CurrentPage = page;
        NotifyStateChanged();
    }

    public void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            NotifyStateChanged();
        }
    }

    public void PrevPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            NotifyStateChanged();
        }
    }

    public void SwitchView(AppView view)
    {
        if (CurrentView == view)
        {
            return;
        }

        CurrentView = view;
        NotifyStateChanged();
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
        SelectedFileIds.Clear();
        CurrentPage = 1;

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
        SelectedFileIds.Clear();
        CurrentPage = 1;

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
        SelectedFileIds.Clear();
        CurrentPage = 1;

        if (CurrentView != AppView.Folders)
        {
            CurrentView = AppView.Folders;
        }

        _ = RefreshFilesAsync();
    }

    public void SelectFile(Guid id, bool multiSelect, bool rangeSelect)
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

    public void SetSelection(IEnumerable<Guid> ids)
    {
        SelectedFileIds.Clear();

        foreach (Guid id in ids)
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

    public void Dispose()
    {
        _library.LibraryChanged -= OnLibraryChanged;
    }
}