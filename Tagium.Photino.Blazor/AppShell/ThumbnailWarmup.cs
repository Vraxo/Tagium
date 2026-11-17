using Microsoft.Extensions.Logging;
using Tagium.Core;
using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Core.Thumbnails;

namespace Tagium.Photino.Blazor.AppShell;

/// <summary>Queues thumbnail pre-generation after initial load and completed scans.</summary>
internal sealed class ThumbnailWarmup : IDisposable
{
    private readonly AppState _appState;
    private readonly LibraryService _library;
    private readonly ThumbnailService _thumbnails;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private bool _disposed;

    public ThumbnailWarmup(AppState appState, LibraryService library, ThumbnailService thumbnails, ILogger logger)
    {
        _appState = appState;
        _library = library;
        _thumbnails = thumbnails;
        _logger = logger;
    }

    public void Queue(bool refreshLibrary = false)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            token = _shutdown.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string timingMessage = refreshLibrary
                    ? "Loading library files for thumbnail pre-generation"
                    : "Waiting for initial library load before thumbnail pre-generation";
                using LogTiming timer = LogTiming.Start(_logger, timingMessage);

                IReadOnlyList<FileEntry> allFiles;
                if (refreshLibrary)
                {
                    allFiles = _library.GetFiles(null, false);
                }
                else
                {
                    await _appState.InitialLoadTask.WaitAsync(token).ConfigureAwait(false);
                    allFiles = _appState.InitialFiles;
                }

                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _logger.LogInformation("Loaded {Count} files for background thumbnail pre-generation", allFiles.Count);
                    _thumbnails.QueueBackgroundGeneration(allFiles);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The layout was disposed while the initial library load was pending.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to queue background thumbnail pre-generation");
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _shutdown.Cancel();
            _thumbnails.CancelBackgroundGeneration();
        }
        _shutdown.Dispose();
    }
}
