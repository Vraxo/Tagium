using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Models;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class TagSelector
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired] public List<Tag> AvailableTags { get; set; } = [];
    [Parameter, EditorRequired] public EventCallback<Guid> OnTagSelected { get; set; }

    private List<Tag> _filteredTags = [];
    private int _activeIndex = 0;
    private bool _isOpen;

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                UpdateFilter();
            }
        }
    }

    protected override void OnParametersSet()
    {
        UpdateFilter();
    }

    private void UpdateFilter()
    {
        _filteredTags = string.IsNullOrWhiteSpace(_searchText)
            ? AvailableTags
            : AvailableTags.Where(t => t.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        _activeIndex = 0;
    }

    private void Open()
    {
        _isOpen = true;
        UpdateFilter();
    }

    private void Close()
    {
        _isOpen = false;
        _searchText = "";
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (!_isOpen || _filteredTags.Count == 0)
        {
            return;
        }

        if (e.Key == "ArrowDown")
        {
            _activeIndex++;
            if (_activeIndex >= _filteredTags.Count)
            {
                _activeIndex = _filteredTags.Count - 1;
            }
            await ScrollActiveItemIntoView();
        }
        else if (e.Key == "ArrowUp")
        {
            _activeIndex--;
            if (_activeIndex < 0)
            {
                _activeIndex = 0;
            }
            await ScrollActiveItemIntoView();
        }
        else if (e.Key == "Enter")
        {
            if (_activeIndex >= 0 && _activeIndex < _filteredTags.Count)
            {
                await SelectTag(_filteredTags[_activeIndex].Id);
            }
        }
        else if (e.Key == "Escape")
        {
            Close();
        }
    }

    private async Task ScrollActiveItemIntoView()
    {
        try
        {
            await JS.InvokeVoidAsync("eval", "var el = document.querySelector('.tag-dropdown-item.active'); if(el) el.scrollIntoView({block: 'nearest'});");
        }
        catch
        {
            // Gracefully catch evaluation context errors during rapid rendering transitions
        }
    }

    private async Task SelectTag(Guid id)
    {
        _searchText = "";
        _isOpen = false;
        await OnTagSelected.InvokeAsync(id);
    }
}