using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Core.Settings;
using Tagium.Core.Tags;
using Tagium.Photino.Blazor.Notifications;

namespace Tagium.Photino.Blazor.Tags;

public partial class TagInspectorPanel : IDisposable
{
    private sealed record PartialTagAssignment(Tag Tag, int AssignedCount, int SelectedCount);

    [Inject] private LibraryService Library { get; set; } = default!;
    [Inject] private TagRepository TagRepo { get; set; } = default!;
    [Inject] private SettingsManager Settings { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;

    [Parameter, EditorRequired] public required IEnumerable<Guid> FileIds { get; set; }

    private List<Tag> _activeTags = [];
    private List<PartialTagAssignment> _partialTags = [];
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
        List<Guid> ids = _fileIdsList.Distinct().ToList();
        if (ids.Count == 0)
        {
            _activeTags = [];
            _partialTags = [];
            _availableTags = [];
            return;
        }

        _ = Task.Run(async () =>
        {
            List<Tag> allTags = Library.GetTags();
            List<Tag> active;
            List<PartialTagAssignment> partial;

            if (ids.Count == 1)
            {
                FileEntry? file = Library.GetFileWithTagsById(ids[0]);
                active = file?.Tags ?? [];
                partial = [];
            }
            else
            {
                Dictionary<Guid, int> assignmentCounts = Library.GetTagAssignmentCounts(ids);
                active = TagOrdering.ByName(allTags.Where(tag => assignmentCounts.GetValueOrDefault(tag.Id) == ids.Count));

                List<Tag> partialTags = TagOrdering.ByName(allTags.Where(tag =>
                    assignmentCounts.TryGetValue(tag.Id, out int count) && count > 0 && count < ids.Count));
                partial = partialTags
                    .Select(tag => new PartialTagAssignment(tag, assignmentCounts[tag.Id], ids.Count))
                    .ToList();
            }

            HashSet<Guid> activeIds = active.Select(tag => tag.Id).ToHashSet();
            HashSet<Guid> partialIds = partial.Select(assignment => assignment.Tag.Id).ToHashSet();
            List<Tag> available = allTags
                .Where(tag => !activeIds.Contains(tag.Id) && !partialIds.Contains(tag.Id))
                .ToList();

            await InvokeAsync(() =>
            {
                _activeTags = TagOrdering.ByName(active);
                _partialTags = partial;
                _availableTags = available;
                StateHasChanged();
            });
        });
    }

    private void OnLibraryChanged()
    {
        LoadTags();
    }

    private async Task OnTagSelected(Guid tagId)
    {
        List<Guid> ids = _fileIdsList.ToList();
        await Task.Run(() =>
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

    private static string GetPartialTagDescription(PartialTagAssignment assignment)
    {
        return $"{assignment.AssignedCount} of {assignment.SelectedCount} selected files have this tag";
    }

    private async Task ApplyPartialTagToAll(Guid tagId)
    {
        await OnTagSelected(tagId);
        Settings.RecordRecentTag(tagId);
    }

    private async Task OnTagCreated(string tagName)
    {
        List<Guid> ids = _fileIdsList.ToList();
        string normalized = tagName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        await Task.Run(() =>
        {
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

    private async Task RemoveTag(Guid tagId)
    {
        List<Guid> ids = _fileIdsList.ToList();
        string tagName = _activeTags.FirstOrDefault(tag => tag.Id == tagId)?.Name
            ?? _partialTags.FirstOrDefault(assignment => assignment.Tag.Id == tagId)?.Tag.Name
            ?? "tag";

        await Task.Run(() =>
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

        Toast.Show(
            ids.Count == 1
                ? $"Removed “{tagName}” from this file."
                : $"Removed “{tagName}” from selected files.",
            ToastType.Success);
    }

    public void Dispose()
    {
        Library.LibraryChanged -= OnLibraryChanged;
    }
}