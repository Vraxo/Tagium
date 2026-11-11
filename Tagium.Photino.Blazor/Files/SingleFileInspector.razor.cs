using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Files;
using Tagium.Core.Settings;
using Tagium.Core.Thumbnails;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class SingleFileInspector : IAsyncDisposable
{
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;
    [Inject] private LocalVideoServer VideoServer { get; set; } = default!;
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private SettingsManager Settings { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired] public required FileEntry File { get; set; }

    private string VideoPreviewUrl => VideoServer.GetVideoUrl(File.Id);
    private ElementReference _videoElement;
    private ElementReference _boundVideoElement;
    private DotNetObjectReference<SingleFileInspector>? _dotNetReference;
    private bool _videoNeedsInitialization = true;
    private bool _hasBoundVideo;
    private Guid? _lastFileId;
    private bool SourceExists => System.IO.File.Exists(File.Path);

    private Guid[] _fileIds = [];

    private MediaKind Kind => SupportedFormats.GetKind(Path.GetExtension(File.Path));
    private bool IsImage => Kind == MediaKind.Image;
    private bool IsVideo => Kind == MediaKind.Video;

    private string TypeLabel
    {
        get
        {
            string ext = Path.GetExtension(File.Path);
            string kindWord = Kind switch
            {
                MediaKind.Image => "image",
                MediaKind.Video => "video",
                _ => "file"
            };
            return ext.Length == 0 ? kindWord : $"{ext.TrimStart('.').ToUpperInvariant()} {kindWord}";
        }
    }

    private static string ShortHash(string hash) => hash.Length <= 12 ? hash : hash[..12];

    protected override void OnParametersSet()
    {
        _fileIds = [File.Id];

        if (_lastFileId != File.Id)
        {
            _lastFileId = File.Id;
            _videoNeedsInitialization = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_videoNeedsInitialization)
        {
            return;
        }

        _videoNeedsInitialization = false;
        await UnbindVideoAsync();

        if (!IsVideo || !SourceExists)
        {
            return;
        }

        _dotNetReference ??= DotNetObjectReference.Create(this);

        try
        {
            await JS.InvokeVoidAsync("videoVolume.bind", _videoElement, Settings.Current.VideoVolume, _dotNetReference);
            _boundVideoElement = _videoElement;
            _hasBoundVideo = true;
        }
        catch (JSException)
        {
            // Keep native video controls usable if the persistence bridge is unavailable.
        }
    }

    [JSInvokable]
    public Task HandleVideoVolumeChanged(double volume)
    {
        Settings.Update(settings => settings.VideoVolume = volume);
        return Task.CompletedTask;
    }

    private async Task UnbindVideoAsync()
    {
        if (!_hasBoundVideo)
        {
            return;
        }

        _hasBoundVideo = false;

        try
        {
            await JS.InvokeVoidAsync("videoVolume.unbind", _boundVideoElement);
        }
        catch (JSException)
        {
            // The WebView may already be shutting down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnbindVideoAsync();
        _dotNetReference?.Dispose();
    }

    private void OpenFile()
    {
        Launcher.OpenFile(File.Path);
    }

    private void ShowInExplorer()
    {
        Launcher.ShowInExplorer(File.Path);
    }
}