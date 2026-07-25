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
    private List<Tag> _oldAvailableTags = [];
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
        // Only reset the active index and filter if the actual tag list content has changed from the parent
        bool hasChanged = _oldAvailableTags.Count != AvailableTags.Count ||
                          !_oldAvailableTags.Select(t => t.Id).SequenceEqual(AvailableTags.Select(t => t.Id));

        if (hasChanged)
        {
            _oldAvailableTags = AvailableTags.ToList();
            UpdateFilter();
        }
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

        if (e.Key is "ArrowDown" or "Down")
        {
            _activeIndex++;
            if (_activeIndex >= _filteredTags.Count)
            {
                _activeIndex = _filteredTags.Count - 1;
            }
            await ScrollActiveItemIntoView();
        }
        else if (e.Key is "ArrowUp" or "Up")
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