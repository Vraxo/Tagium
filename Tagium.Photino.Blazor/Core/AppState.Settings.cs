using Tagium.Core.Files;
using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor;

public partial class AppState
{
    private void OnSettingsChanged()
    {
        bool changed = false;
        FileScrollMode newMode = _settings.Current.FileScrollMode;
        int newPageSize = _settings.Current.FilesPerPage;

        int modeAnchorIndex = -1;

        if (newMode != ScrollMode)
        {
            // Keep the user where they were. The anchor is the first selected file that is
            // rendered — the file they are looking at — or, with nothing selected there, the
            // top of the rendered viewport. Returning to pagination lands on the page holding
            // the anchor; switching to infinite scroll renders whole batches up to and
            // including the anchor's batch, so the page they came from is still filled below.
            modeAnchorIndex = CurrentAnchorIndex();

            ScrollMode = newMode;

            if (newMode == FileScrollMode.Infinite)
            {
                VisibleFileCount = FilePagination.VisibleCountContaining(
                    modeAnchorIndex, PageSize, TotalFilesCount);
            }
            else
            {
                CurrentPage = FilePagination.PageForIndex(modeAnchorIndex, PageSize, TotalFilesCount);
            }

            changed = true;
        }

        if (newPageSize != PageSize)
        {
            if (ScrollMode == FileScrollMode.Infinite)
            {
                VisibleFileCount = FilePagination.VisibleCountAfterBatchSizeChange(
                    VisibleFileCount, newPageSize, TotalFilesCount);
            }
            else
            {
                CurrentPage = FilePagination.PageAfterSizeChange(CurrentPage, PageSize, newPageSize, TotalFilesCount);
            }

            PageSize = newPageSize;
            changed = true;
        }

        // Do this last: a page-size change above can move the rendered window, and the
        // anchor has to be focusable in whatever the views are about to render.
        if (modeAnchorIndex >= 0)
        {
            ApplyAnchorAfterModeChange(modeAnchorIndex);
        }

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// The [start, end) slice of the filtered result set the views are currently rendering.
    /// </summary>
    private (int Start, int End) RenderedWindow() => FilePagination.RenderedWindow(
        IsInfiniteScroll, CurrentPage, PageSize, VisibleFileCount, TotalFilesCount);

    /// <summary>
    /// Index of the file the user is focused on inside the rendered window, computed in the
    /// same coordinate space <see cref="MovePage"/> and the anchors use.
    /// </summary>
    private int CurrentAnchorIndex() =>
        FilePagination.AnchorIndex(OrderedFileIds(), _selection.Ids, RenderedWindow());

    /// <summary>
    /// A mode change re-renders the list from scratch. When the user has no selection, put the
    /// anchor — the file they were looking at — back on the keyboard path so the roving tab
    /// stop and arrow keys resume there. An existing selection is left untouched: it already
    /// drives the tab stop, and silently replacing it would discard the user's work.
    /// </summary>
    private void ApplyAnchorAfterModeChange(int anchorIndex)
    {
        PendingFocusIndex = -1;

        if (SelectedFileIds.Count > 0 || _currentFiles.Count == 0)
        {
            return;
        }

        // A page-size change can move the window off the anchor; fall back to its first file.
        (int start, int end) = RenderedWindow();
        int targetIndex = Math.Clamp(anchorIndex, start, Math.Max(end - 1, start));

        SelectFile(_currentFiles[targetIndex].Id, multiSelect: false, rangeSelect: false);
        // PendingFocusIndex is relative to the rendered items, not the result set.
        PendingFocusIndex = targetIndex - start;
    }
}
