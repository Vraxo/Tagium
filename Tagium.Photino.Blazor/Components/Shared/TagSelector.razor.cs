using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class TagSelector
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TagRepository TagRepo { get; set; } = default!;

    [Parameter, EditorRequired] public List<Tag> AvailableTags { get; set; } = [];
    [Parameter, EditorRequired] public EventCallback<Guid> OnTagSelected { get; set; }
    [Parameter] public EventCallback<string> OnTagCreated { get; set; }

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

    private bool ShowCreateOption => OnTagCreated.HasDelegate &&
                                     !string.IsNullOrWhiteSpace(_searchText) &&
                                     TagRepo.ResolveTagId(_searchText) == null;

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
        if (!_isOpen)
        {
            return;
        }

        int maxCount = ShowCreateOption ? _filteredTags.Count + 1 : _filteredTags.Count;
        if (maxCount == 0)
        {
            return;
        }

        if (e.Key is "ArrowDown" or "Down")
        {
            _activeIndex++;
            if (_activeIndex >= maxCount)
            {
                _activeIndex = maxCount - 1;
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
            else if (ShowCreateOption && _activeIndex == _filteredTags.Count)
            {
                await CreateAndSelectTag();
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
        await OnTagSelected.InvokeAsync(id);
    }

    private async Task CreateAndSelectTag()
    {
        string textToCreate = _searchText.Trim();
        _searchText = "";
        await OnTagCreated.InvokeAsync(textToCreate);
    }
}