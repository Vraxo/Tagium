using Tagium.Core.Files;
using Tagium.Photino.Blazor.Files;

namespace Tagium.Photino.Blazor;

public partial class AppState
{
    private void OnLibraryChanged()
    {
        _ = RefreshFilesAsync();
    }

    private void OnDuplicatesDetected(IReadOnlyList<FileEntry> duplicates)
    {
        if (duplicates.Count == 1)
        {
            _modalService.Show("Duplicate File", builder =>
            {
                builder.OpenComponent<DuplicateFileModal>(0);
                builder.AddAttribute(1, nameof(DuplicateFileModal.ExistingFile), duplicates[0]);
                builder.CloseComponent();
            });
        }
        else
        {
            _modalService.Show("Duplicate Files", builder =>
            {
                builder.OpenComponent<DuplicatesModal>(0);
                builder.CloseComponent();
            });
        }
    }

    private async Task LoadInitialFilesAsync()
    {
        // Offload the initial library query to a background thread so the first render is not blocked.
        List<FileEntry> files = await Task.Run(() => _library.GetFiles(SearchText, IsMissingOnly, CurrentFolderPath));
        InitialFiles = files;
        ApplyLoadedFiles(files);
    }

    public async Task RefreshFilesAsync()
    {
        // Offload heavy query rendering to a background thread to prevent UI freezes.
        List<FileEntry> files = await Task.Run(() => _library.GetFiles(SearchText, IsMissingOnly, CurrentFolderPath));
        ApplyLoadedFiles(files);
    }

    private void ApplyLoadedFiles(List<FileEntry> files)
    {
        _currentFiles = files;

        // Adjust page if it goes out of bounds after a filter or library change.
        CurrentPage = Math.Clamp(CurrentPage, 1, Math.Max(TotalPages, 1));

        // Keep the loaded batches across refreshes of the same result set (thumbnail work,
        // ratings and tags reload the list); only clamp to the new total and guarantee at
        // least one batch. A filter change resets the count through ResetScrollPosition.
        VisibleFileCount = FilePagination.VisibleCountAfterBatchSizeChange(
            VisibleFileCount, PageSize, TotalFilesCount);

        NotifyStateChanged();
    }

    public IEnumerable<FileEntry> GetPagedFiles()
    {
        return _currentFiles
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize);
    }

    /// <summary>
    /// The files the current view should render: the current page when paginated, or
    /// the loaded batches from the top when infinite scroll is on.
    /// </summary>
    public IEnumerable<FileEntry> GetVisibleFiles()
    {
        if (IsInfiniteScroll)
        {
            return _currentFiles.Take(Math.Max(VisibleFileCount, 0));
        }

        return GetPagedFiles();
    }

    /// <summary>
    /// Reads and clears the index the view should focus after a cross-batch page move.
    /// Returns <c>-1</c> when the visible viewport already contains the target.
    /// </summary>
    public int ConsumePendingFocusIndex()
    {
        int index = PendingFocusIndex;
        PendingFocusIndex = -1;
        return index;
    }
}