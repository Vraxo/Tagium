using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Search;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Search;

public partial class SearchInput
{
    private const int DebounceMilliseconds = 300;

    // Short enough to feel instant, long enough for a click on a suggestion to land before the
    // blur that follows it would otherwise tear the list out from under the pointer.
    private const int BlurCloseDelayMilliseconds = 200;

    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private SettingsManager Settings { get; set; } = default!;

    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _blurCts;
    private string _value = "";
    private bool _showHistory;
    private ElementReference _inputElement;

    private IReadOnlyList<string> Suggestions =>
        SearchHistory.Suggestions(Settings.Current.RecentSearches ?? [], _value);

    private bool ShowSuggestions => _showHistory && Suggestions.Count > 0;

    protected override void OnInitialized()
    {
        _value = AppState.SearchText;
    }

    private void OnInput(ChangeEventArgs e)
    {
        _value = e.Value?.ToString() ?? "";

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();

        _ = DebounceSearchAsync(_debounceCts.Token);
    }

    private async Task DebounceSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken);

            // Live search: applied as you type, but only committed to history when you stop
            // (Enter, leaving the box, or picking a suggestion).
            ApplySearch(_value, record: false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another keystroke.
        }
    }

    private void OnFocus()
    {
        _blurCts?.Cancel();
        _showHistory = true;
    }

    private void OnBlur()
    {
        // A query typed but never confirmed with Enter is still a search the user made.
        Commit();

        _blurCts?.Cancel();
        _blurCts = new CancellationTokenSource();

        _ = CloseHistoryAfterBlurAsync(_blurCts.Token);
    }

    private async Task CloseHistoryAfterBlurAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(BlurCloseDelayMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;   // a suggestion took the focus, so the list stays put
        }

        await InvokeAsync(() =>
        {
            _showHistory = false;
            StateHasChanged();
        });
    }

    private void OnKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Enter":
                Commit();
                _showHistory = false;
                break;

            case "Escape":
                // Escape closes the list and leaves the query alone.
                _showHistory = false;
                break;
        }
    }

    private void ApplySuggestion(string suggestion)
    {
        _blurCts?.Cancel();
        _value = suggestion;
        Commit();
        _showHistory = false;
    }

    private void Commit()
    {
        _debounceCts?.Cancel();
        ApplySearch(_value, record: true);
    }

    private void ApplySearch(string query, bool record)
    {
        if (!string.Equals(query, AppState.SearchText, StringComparison.Ordinal))
        {
            AppState.SetSearch(query);
        }

        if (record)
        {
            Settings.RecordRecentSearch(query);
        }
    }

    public async Task FocusAsync()
    {
        await _inputElement.FocusAsync();
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _blurCts?.Cancel();
        _blurCts?.Dispose();
    }
}
