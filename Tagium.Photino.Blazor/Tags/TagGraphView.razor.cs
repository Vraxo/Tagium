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
    private bool _settingsDirty;
    private bool _disposed;
    private string SearchText { get; set; } = "";
    private int MinSharedFiles { get; set; } = 2;
    private int NodeSize { get; set; } = 100;
    private int LinkThickness { get; set; } = 2;
    private int TextFadeThreshold { get; set; } = 35;
    private bool ShowArrows { get; set; } = true;
    private int CenterForce { get; set; } = 30;
    private int RepelForce { get; set; } = 45;
    private int LinkForce { get; set; } = 30;
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

    private void MarkSettingsDirty()
    {
        _settingsDirty = true;
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
            _settingsDirty = false;
            await JS.InvokeVoidAsync("tagGraph.render", _canvas, ToClientData(), MinSharedFiles, SearchText, GraphSettings(), _objectReference);
        }
        else if (_settingsDirty && !_isLoading && _data.Nodes.Count > 0)
        {
            _settingsDirty = false;
            await JS.InvokeVoidAsync("tagGraph.setSettings", _canvas, GraphSettings());
        }
    }

    private object GraphSettings()
    {
        return new
        {
            nodeSize = NodeSize / 100.0,
            linkThickness = LinkThickness,
            textFadeThreshold = TextFadeThreshold / 100.0,
            showArrows = ShowArrows,
            centerForce = CenterForce / 100.0,
            repelForce = RepelForce / 100.0,
            linkForce = LinkForce / 100.0,
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
    public async Task BrowseTag(string tagId)
    {
        Tag? tag = _data.Nodes.FirstOrDefault(candidate => candidate.Id.ToString() == tagId);
        if (tag != null)
        {
            string query = $"tag:{tag.Name}";
            await JS.InvokeVoidAsync("tagiumNavigation.openGraphBrowse", query);
            AppState.SetSearch(query);
        }
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
