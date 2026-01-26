using Microsoft.AspNetCore.Components;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class SearchInput
{
    private CancellationTokenSource? _debounceCts;
    private string _value = "";
    private ElementReference _inputElement;

    private const int DebounceMilliseconds = 300;

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
            AppState.SetSearch(_value);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
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
    }
}