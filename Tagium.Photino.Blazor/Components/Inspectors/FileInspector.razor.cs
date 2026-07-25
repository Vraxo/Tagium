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
    private List<Tag> _allTagsCache = [];
    private CancellationTokenSource? _cts;
    private string SelectionKey = Guid.NewGuid().ToString();

    protected override void OnInitialized()
    {
        LoadAllTags();
        AppState.OnSelectionChange += OnSelectionChanged;
        Library.LibraryChanged += OnLibraryChanged;
        _ = LoadInspectorDataAsync();
    }

    private void LoadAllTags()
    {
        _allTagsCache = Library.GetTags();
    }

    private void OnSelectionChanged()
    {
        SelectionKey = Guid.NewGuid().ToString();
        _ = LoadInspectorDataAsync();
    }

    private void OnLibraryChanged()
    {
        LoadAllTags(); // Refresh tag cache
        _ = LoadInspectorDataAsync();
    }

    private async Task LoadInspectorDataAsync()
    {
        // Cancel any previous ongoing operation
        _cts?.Cancel();
        using CancellationTokenSource cts = new();
        _cts = cts;
        var token = cts.Token;

        try
        {
            // Capture selected IDs on UI thread
            List<Guid> selectedIds = AppState.SelectedFileIds.ToList();
            int count = selectedIds.Count;

            if (count == 0)
            {
                await InvokeAsync(() =>
                {
                    SelectionMode = InspectorMode.None;
                    SelectedFile = null;
                    StateHasChanged();
                });
                return;
            }

            // Perform database operations in background
            await Task.Run(async () =>
            {
                if (count == 1)
                {
                    var id = selectedIds[0];
                    var file = Library.GetFileWithTagsById(id);
                    if (file is null)
                    {
                        return;
                    }

                    var assigned = file.Tags ?? [];
                    var available = GetAvailableTags(assigned);

                    // Update UI on UI thread
                    await InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        SelectionMode = InspectorMode.Single;
                        SelectedFile = file;
                        AssignedTags = assigned;
                        AvailableTags = available;
                        StateHasChanged();
                    });
                }
                else
                {
                    var common = Library.GetCommonTags(selectedIds);
                    var available = GetAvailableTags(common);

                    await InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        SelectionMode = InspectorMode.Multi;
                        SelectedFile = null;
                        CommonTags = common;
                        AvailableTags = available;
                        StateHasChanged();
                    });
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            // Ignore, expected when new selection occurs
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Inspector] Error: {ex}");
            await InvokeAsync(() =>
            {
                HasRenderError = true;
                StateHasChanged();
            });
        }
        finally
        {
            if (_cts == cts)
            {
                _cts = null;
            }
        }
    }

    private List<Tag> GetAvailableTags(List<Tag> existingTags)
    {
        HashSet<Guid> existingIds = [.. existingTags.Select(t => t.Id)];
        return [.. _allTagsCache.Where(t => !existingIds.Contains(t.Id))];
    }

    public void Dispose()
    {
        _cts?.Cancel();
        AppState.OnSelectionChange -= OnSelectionChanged;
        Library.LibraryChanged -= OnLibraryChanged;
    }
}