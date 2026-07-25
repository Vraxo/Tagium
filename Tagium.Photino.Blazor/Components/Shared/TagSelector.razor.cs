using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class TagSelector
{
    [Parameter, EditorRequired] public List<Tag> AvailableTags { get; set; } = [];
    [Parameter, EditorRequired] public EventCallback<Guid> OnTagSelected { get; set; }

    private string _searchText = "";
    private bool _isOpen;

    private IEnumerable<Tag> FilteredTags => string.IsNullOrWhiteSpace(_searchText)
        ? AvailableTags
        : AvailableTags.Where(t => t.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

    private void Open()
    {
        _isOpen = true;
    }

    private void Close()
    {
        _isOpen = false;
        _searchText = "";
    }

    private async Task SelectTag(Guid id)
    {
        _searchText = "";
        _isOpen = false;
        await OnTagSelected.InvokeAsync(id);
    }
}