using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class FileInspector
{
    private InspectorMode SelectionMode = InspectorMode.None;
    private FileEntry? SelectedFile;
    private bool HasRenderError = false;
    private List<Tag> AssignedTags = [];
    private List<Tag> CommonTags = [];
    private List<Tag> AvailableTags = [];

    protected override void OnInitialized()
    {
        AppState.OnSelectionChange += OnSelectionChanged;
        Library.LibraryChanged += OnLibraryChanged;
        UpdateView();
    }

    private void OnSelectionChanged()
    {
        _ = InvokeAsync(UpdateView);
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(UpdateView);
    }

    private void UpdateView()
    {
        HasRenderError = false;
        try
        {
            int count = AppState.SelectedFileIds.Count;

            if (count == 0)
            {
                SelectionMode = InspectorMode.None;
                SelectedFile = null;
            }
            else if (count == 1)
            {
                Guid id = AppState.SelectedFileIds.First();
                SelectedFile = Library.GetFileById(id);

                if (SelectedFile is not null)
                {
                    SelectionMode = InspectorMode.Single;
                    AssignedTags = Library.GetTagsForFile(id);
                    AvailableTags = GetAvailableTags(AssignedTags);
                }
                else
                {
                    SelectionMode = InspectorMode.None;
                }
            }
            else
            {
                SelectionMode = InspectorMode.Multi;
                SelectedFile = null;
                CommonTags = Library.GetCommonTags(AppState.SelectedFileIds);
                AvailableTags = GetAvailableTags(CommonTags);
            }

            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Inspector] Critical Error: {ex}");
            HasRenderError = true;
            StateHasChanged();
        }
    }

    private List<Tag> GetAvailableTags(List<Tag> existingTags)
    {
        List<Tag> allTags = Library.GetTags();
        HashSet<Guid> existingIds = [.. existingTags.Select(t => t.Id)];
        return [.. allTags.Where(t => !existingIds.Contains(t.Id))];
    }

    public void Dispose()
    {
        AppState.OnSelectionChange -= OnSelectionChanged;
        Library.LibraryChanged -= OnLibraryChanged;
    }
}