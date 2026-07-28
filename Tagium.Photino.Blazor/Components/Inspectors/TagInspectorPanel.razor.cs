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
        List<Guid> ids = _fileIdsList.ToList();
        if (!ids.Any())
        {
            _activeTags = [];
            _availableTags = [];
            return;
        }

        _ = Task.Run(async () =>
        {
            List<Tag> allTags = Library.GetTags();
            List<Tag> active;

            if (ids.Count == 1)
            {
                FileEntry? file = Library.GetFileWithTagsById(ids[0]);
                active = file?.Tags ?? [];
            }
            else
            {
                active = Library.GetCommonTags(ids);
            }

            HashSet<Guid> activeIds = active.Select(t => t.Id).ToHashSet();
            List<Tag> available = allTags.Where(t => !activeIds.Contains(t.Id)).ToList();

            await InvokeAsync(() =>
            {
                _activeTags = active;
                _availableTags = available;
                StateHasChanged();
            });
        });
    }

    private void OnLibraryChanged()
    {
        LoadTags();
    }

    private void OnTagSelected(Guid tagId)
    {
        List<Guid> ids = _fileIdsList.ToList();
        _ = Task.Run(() =>
        {
            if (ids.Count == 1)
            {
                Library.AddTagToFile(ids[0], tagId);
            }
            else
            {
                Library.AddTagToFiles(ids, tagId);
            }
        });
    }

    private void OnTagCreated(string tagName)
    {
        List<Guid> ids = _fileIdsList.ToList();
        _ = Task.Run(() =>
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
                if (ids.Count == 1)
                {
                    Library.AddTagToFile(ids[0], id.Value);
                }
                else
                {
                    Library.AddTagToFiles(ids, id.Value);
                }
            }
        });
    }

    private void RemoveTag(Guid tagId)
    {
        List<Guid> ids = _fileIdsList.ToList();
        _ = Task.Run(() =>
        {
            if (ids.Count == 1)
            {
                Library.RemoveTagFromFile(ids[0], tagId);
            }
            else
            {
                Library.RemoveTagFromFiles(ids, tagId);
            }
        });
    }

    public void Dispose()
    {
        Library.LibraryChanged -= OnLibraryChanged;
    }
}