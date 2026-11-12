using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Library;
using Tagium.Core.Tags;

namespace Tagium.Photino.Blazor.Tags;

public partial class TagGraphView
{
    private TagGraphData _data = new([], []);
    private ElementReference _canvas;
    private DotNetObjectReference<TagGraphView>? _objectReference;
    private bool _isLoading = true;
    private bool _graphDirty = true;
    private bool _forcesDirty;
    private bool _disposed;
    private string SearchText { get; set; } = "";
    private int MinSharedFiles { get; set; } = 2;
    private int RepulsionStrength { get; set; } = 45;
    private int LinkStrength { get; set; } = 30;
    private int LinkDistance { get; set; } = 105;

    private string GraphSummary => $"{_data.Nodes.Count} tags (up to 120 most-used) · {_data.Edges.Count(edge => edge.SharedFileCount >= MinSharedFiles || edge.IsHierarchy)} relationships";

    protected override void OnInitialized()
    {
        Library.LibraryChanged += OnLibraryChanged;
        _ = LoadGraphAsync();
    }

    private void OnLibraryChanged()
    {
        _ = LoadGraphAsync();
    }

    private async Task LoadGraphAsync()
    {
        TagGraphData data = await Task.Run(() => Library.GetTagGraphData());
        await InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            _data = data;
            _isLoading = false;
            _graphDirty = true;
            StateHasChanged();
        });
    }

    private void MarkGraphDirty()
    {
        _graphDirty = true;
    }

    private void MarkForcesDirty()
    {
        _forcesDirty = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _objectReference = DotNetObjectReference.Create(this);
        }

        if (_graphDirty && !_isLoading && _data.Nodes.Count > 0)
        {
            _graphDirty = false;
            _forcesDirty = false;
            await JS.InvokeVoidAsync("tagGraph.render", _canvas, ToClientData(), MinSharedFiles, SearchText, ForceSettings(), _objectReference);
        }
        else if (_forcesDirty)
        {
            _forcesDirty = false;
            await JS.InvokeVoidAsync("tagGraph.setForces", _canvas, ForceSettings());
        }
    }

    private object ForceSettings()
    {
        return new
        {
            repulsion = RepulsionStrength / 100.0,
            linkStrength = LinkStrength / 100.0,
            linkDistance = LinkDistance
        };
    }

    private object ToClientData()
    {
        return new
        {
            nodes = _data.Nodes.Select(tag => new
            {
                id = tag.Id.ToString(),
                name = tag.Name,
                color = tag.ColorHex,
                emoji = tag.Emoji,
                fileCount = tag.FileCount
            }),
            edges = _data.Edges.Select(edge => new
            {
                source = edge.SourceTagId.ToString(),
                target = edge.TargetTagId.ToString(),
                sharedFileCount = edge.SharedFileCount,
                isHierarchy = edge.IsHierarchy
            })
        };
    }

    [JSInvokable]
    public Task BrowseTag(string tagId)
    {
        Tag? tag = _data.Nodes.FirstOrDefault(candidate => candidate.Id.ToString() == tagId);
        if (tag != null)
        {
            AppState.SetSearch($"tag:{tag.Name}");
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Library.LibraryChanged -= OnLibraryChanged;
        try
        {
            await JS.InvokeVoidAsync("tagGraph.destroy", _canvas);
        }
        catch (JSDisconnectedException)
        {
            // The webview is already shutting down.
        }

        _objectReference?.Dispose();
    }
}
