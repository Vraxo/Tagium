using Microsoft.AspNetCore.Components;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class TagInspectorPanel : IDisposable
{
    [Inject] private LibraryService Library { get; set; } = default!;
    [Inject] private TagRepository TagRepo { get; set; } = default!;

    [Parameter, EditorRequired] public required IEnumerable<Guid> FileIds { get; set; }

    private List<Tag> _activeTags = [];
    private List<Tag> _availableTags = [];
    private List<Guid> _fileIdsList = [];

    protected override void OnInitialized()
    {
        Library.LibraryChanged += OnLibraryChanged;
    }

    protected override void OnParametersSet()
    {
        _fileIdsList = FileIds.ToList();
        LoadTags();
    }

    private void LoadTags()
    {
        if (!_fileIdsList.Any())
        {
            _activeTags = [];
            _availableTags = [];
            return;
        }

        List<Tag> allTags = Library.GetTags();

        if (_fileIdsList.Count == 1)
        {
            FileEntry? file = Library.GetFileWithTagsById(_fileIdsList[0]);
            _activeTags = file?.Tags ?? [];
        }
        else
        {
            _activeTags = Library.GetCommonTags(_fileIdsList);
        }

        var activeIds = _activeTags.Select(t => t.Id).ToHashSet();
        _availableTags = allTags.Where(t => !activeIds.Contains(t.Id)).ToList();
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(() =>
        {
            LoadTags();
            StateHasChanged();
        });
    }

    private void OnTagSelected(Guid tagId)
    {
        if (_fileIdsList.Count == 1)
        {
            Library.AddTagToFile(_fileIdsList[0], tagId);
        }
        else
        {
            Library.AddTagToFiles(_fileIdsList, tagId);
        }
    }

    private void OnTagCreated(string tagName)
    {
        string normalized = tagName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        Library.AddTag(normalized);
        Guid? id = TagRepo.ResolveTagId(normalized);
        if (id.HasValue)
        {
            if (_fileIdsList.Count == 1)
            {
                Library.AddTagToFile(_fileIdsList[0], id.Value);
            }
            else
            {
                Library.AddTagToFiles(_fileIdsList, id.Value);
            }
        }
    }

    private void RemoveTag(Guid tagId)
    {
        if (_fileIdsList.Count == 1)
        {
            Library.RemoveTagFromFile(_fileIdsList[0], tagId);
        }
        else
        {
            Library.RemoveTagFromFiles(_fileIdsList, tagId);
        }
    }

    public void Dispose()
    {
        Library.LibraryChanged -= OnLibraryChanged;
    }
}