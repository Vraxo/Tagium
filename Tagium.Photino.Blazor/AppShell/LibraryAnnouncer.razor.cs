using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;

namespace Tagium.Photino.Blazor.AppShell;

/// <summary>
/// The app's single polite live region for library state. Selection and result counts are
/// already visible, but nothing reads them out: arrow keys move the selection silently and a
/// filter can empty the grid without a word. This speaks the state the user settles on.
/// </summary>
public partial class LibraryAnnouncer : IDisposable
{
    // Long enough to swallow arrow-key repeats, short enough to feel immediate.
    private const int AnnounceDelayMs = 400;

    [Inject] public AppState AppState { get; set; } = default!;

    private string _message = "";
    private string? _pendingMessage;
    private int _announceVersion;
    private int _lastSelectedCount;
    private int _lastTotalCount;
    private bool _disposed;

    protected override void OnInitialized()
    {
        _lastSelectedCount = AppState.SelectedFileIds.Count;
        _lastTotalCount = AppState.TotalFilesCount;

        AppState.OnChange += HandleLibraryChanged;
        AppState.OnSelectionChange += HandleSelectionChanged;
    }

    private void HandleLibraryChanged()
    {
        int total = AppState.TotalFilesCount;

        if (total == _lastTotalCount)
        {
            // Paging, view switches and plain re-renders do not resize the library.
            return;
        }

        _lastTotalCount = total;

        // Search and filters clear the selection without raising OnSelectionChange; absorbing
        // the new count here keeps one filter change to one announcement instead of two.
        _lastSelectedCount = AppState.SelectedFileIds.Count;

        Queue(LibraryAnnouncement.Library(total));
    }

    private void HandleSelectionChanged()
    {
        int selected = AppState.SelectedFileIds.Count;

        if (selected == _lastSelectedCount)
        {
            return;
        }

        _lastSelectedCount = selected;
        Queue(LibraryAnnouncement.Selection(selected, AppState.TotalFilesCount));
    }

    /// <summary>
    /// Arrow keys re-select on every keypress, so announcements are coalesced: only the state
    /// the user stops on is spoken, never each row passed through on the way.
    /// </summary>
    private void Queue(string message)
    {
        _pendingMessage = message;
        int version = Interlocked.Increment(ref _announceVersion);
        _ = AnnounceAsync(version);
    }

    private async Task AnnounceAsync(int version)
    {
        await Task.Delay(AnnounceDelayMs);

        if (_disposed || version != Volatile.Read(ref _announceVersion) || _pendingMessage is not { } message)
        {
            return;   // superseded by a newer state, already published, or the app is closing
        }

        _pendingMessage = null;

        await ApplyAsync(message);
    }

    private Task ApplyAsync(string message)
    {
        return InvokeAsync(() =>
        {
            _message = message;
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        AppState.OnChange -= HandleLibraryChanged;
        AppState.OnSelectionChange -= HandleSelectionChanged;
        _ = Interlocked.Increment(ref _announceVersion);
    }
}
