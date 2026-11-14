using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Settings;
using Tagium.Core.Tags;

namespace Tagium.Photino.Blazor.Tags;

public partial class TagSelector
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TagRepository TagRepo { get; set; } = default!;
    [Inject] private SettingsManager Settings { get; set; } = default!;

    [Parameter, EditorRequired] public List<Tag> AvailableTags { get; set; } = [];
    [Parameter] public Dictionary<Guid, int> AssignmentCounts { get; set; } = [];
    [Parameter] public int SelectedCount { get; set; } = 1;
    [Parameter, EditorRequired] public EventCallback<Guid> OnTagSelected { get; set; }
    [Parameter] public EventCallback<string> OnTagCreated { get; set; }

    private List<Tag> _filteredTags = [];
    private List<Tag> _oldAvailableTags = [];
    private int _recentTagCount;
    private int _activeIndex = 0;
    private bool _isOpen;
    private bool _isApplying;
    private bool _scrollActiveAfterRender;

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

    private string? GetActiveOptionId()
    {
        if (!_isOpen || _activeIndex < 0)
        {
            return null;
        }

        if (_activeIndex < _filteredTags.Count)
        {
            return $"tag-option-{_filteredTags[_activeIndex].Id}";
        }

        return ShowCreateOption && _activeIndex == _filteredTags.Count
            ? "tag-option-create"
            : null;
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
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            _filteredTags = AvailableTags
                .Where(t => t.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _recentTagCount = 0;
        }
        else
        {
            Dictionary<Guid, Tag> availableById = AvailableTags.ToDictionary(tag => tag.Id);
            List<Guid> recentIds = (Settings.Current.RecentTagIds ?? [])
                .Where(availableById.ContainsKey)
                .Distinct()
                .Take(SettingsManager.MaxRecentTags)
                .ToList();
            List<Tag> recentTags = recentIds.Select(id => availableById[id]).ToList();
            HashSet<Guid> recentIdSet = recentIds.ToHashSet();

            _recentTagCount = recentTags.Count;
            _filteredTags = recentTags
                .Concat(AvailableTags.Where(tag => !recentIdSet.Contains(tag.Id)))
                .ToList();
        }

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

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_scrollActiveAfterRender)
        {
            return;
        }

        _scrollActiveAfterRender = false;
        try
        {
            await JS.InvokeVoidAsync("libraryNav.scrollActiveTag");
        }
        catch (JSException)
        {
            // Scrolling is a convenience; it must not interrupt tag selection.
        }
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            Close();
            await JS.InvokeVoidAsync("libraryNav.focusLastFile");
            return;
        }

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
            _scrollActiveAfterRender = true;
        }
        else if (e.Key is "ArrowUp" or "Up")
        {
            _activeIndex--;
            if (_activeIndex < 0)
            {
                _activeIndex = 0;
            }
            _scrollActiveAfterRender = true;
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
    }

    private async Task SelectTag(Guid id)
    {
        if (_isApplying)
        {
            return;
        }

        _isApplying = true;
        try
        {
            await OnTagSelected.InvokeAsync(id);
            Settings.RecordRecentTag(id);
            SearchText = "";
            UpdateFilter();
        }
        finally
        {
            _isApplying = false;
        }
    }

    private async Task CreateAndSelectTag()
    {
        string textToCreate = _searchText.Trim();
        SearchText = "";
        await OnTagCreated.InvokeAsync(textToCreate);

        Guid? createdTagId = TagRepo.ResolveTagId(textToCreate);
        if (createdTagId.HasValue)
        {
            Settings.RecordRecentTag(createdTagId.Value);
            UpdateFilter();
        }
    }

}