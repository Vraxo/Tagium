using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tagium.Core.Files;

namespace Tagium.Photino.Blazor;

/// <summary>
/// Serves local videos over loopback HTTP so the WebView can use standard byte-range
/// requests for buffering and seeking.
/// </summary>
public sealed class LocalVideoServer : IDisposable
{
    private const int MaximumRequestLineLength = 8 * 1024;
    private const int MaximumHeaderCount = 64;
    private const int CopyBufferSize = 64 * 1024;

    private readonly IServiceProvider _services;
    private readonly ILogger<LocalVideoServer> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _requestToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private TcpListener? _listener;
    private Uri? _baseUri;
    private int _disposed;

    public LocalVideoServer(IServiceProvider services, ILogger<LocalVideoServer> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _baseUri = new Uri($"http://127.0.0.1:{port}/{_requestToken}/", UriKind.Absolute);
        _listener = listener;
        CancellationToken cancellationToken = _shutdown.Token;
        _ = Task.Run(() => AcceptConnectionsAsync(listener, cancellationToken));

        _logger.LogInformation("Local video streaming endpoint started on loopback port {Port}", port);
    }

    public string GetVideoUrl(Guid fileId)
    {
        Uri baseUri = _baseUri ?? throw new InvalidOperationException("The local video server has not been started.");
        return new Uri(baseUri, $"video/{fileId:N}").AbsoluteUri;
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the application closes.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the listener interrupts AcceptTcpClientAsync.
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the listener interrupts AcceptTcpClientAsync.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local video streaming listener stopped unexpectedly");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                string? requestLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > MaximumRequestLineLength)
                {
                    await WriteEmptyResponseAsync(stream, 400, "Bad Request", cancellationToken: cancellationToken);
                    return;
                }

                string[] requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (requestParts.Length != 3)
                {
                    await WriteEmptyResponseAsync(stream, 400, "Bad Request", cancellationToken: cancellationToken);
                    return;
                }

                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                bool headersComplete = false;
                for (int i = 0; i < MaximumHeaderCount; i++)
                {
                    string? headerLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    if (string.IsNullOrEmpty(headerLine))
                    {
                        headersComplete = headerLine is not null;
                        break;
                    }

                    int separator = headerLine.IndexOf(':');
                    if (separator > 0)
                    {
                        headers[headerLine[..separator].Trim()] = headerLine[(separator + 1)..].Trim();
                    }
                }

                if (!headersComplete)
                {
                    await WriteEmptyResponseAsync(stream, 400, "Bad Request", cancellationToken: cancellationToken);
                    return;
                }

                string method = requestParts[0];
                if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteEmptyResponseAsync(stream, 204, "No Content", allowMethods: true, cancellationToken: cancellationToken);
                    return;
                }

                if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteEmptyResponseAsync(stream, 405, "Method Not Allowed", extraHeaders: "Allow: GET, HEAD, OPTIONS\r\n", cancellationToken: cancellationToken);
                    return;
                }

                if (!TryGetFileId(requestParts[1], out Guid fileId))
                {
                    await WriteEmptyResponseAsync(stream, 404, "Not Found", cancellationToken: cancellationToken);
                    return;
                }

                FileEntry? entry = _services.GetRequiredService<FileRepository>().GetById(fileId);
                if (entry is null || SupportedFormats.GetKind(Path.GetExtension(entry.Path)) != MediaKind.Video || !File.Exists(entry.Path))
                {
                    await WriteEmptyResponseAsync(stream, 404, "Not Found", cancellationToken: cancellationToken);
                    return;
                }

                await ServeVideoAsync(stream, entry.Path, method, headers, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during application shutdown.
            }
            catch (TimeoutException)
            {
                // Discard incomplete or abandoned local requests.
            }
            catch (IOException)
            {
                // The WebView may close a connection after changing selections or seeking.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serve a local video request");
            }
        }
    }

    private bool TryGetFileId(string requestTarget, out Guid fileId)
    {
        fileId = Guid.Empty;
        int queryIndex = requestTarget.IndexOf('?');
        string path = queryIndex >= 0 ? requestTarget[..queryIndex] : requestTarget;
        string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 3
            && string.Equals(segments[0], _requestToken, StringComparison.Ordinal)
            && string.Equals(segments[1], "video", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(segments[2], "N", out fileId);
    }

    private static async Task ServeVideoAsync(
        NetworkStream responseStream,
        string filePath,
        string method,
        IReadOnlyDictionary<string, string> requestHeaders,
        CancellationToken cancellationToken)
    {
        await using FileStream file = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long fileLength = file.Length;
        bool isPartial = requestHeaders.TryGetValue("Range", out string? rangeHeader);
        long start = 0;
        long end = fileLength - 1;

        if (isPartial && !TryParseByteRange(rangeHeader, fileLength, out start, out end))
        {
            await WriteEmptyResponseAsync(
                responseStream,
                416,
                "Range Not Satisfiable",
                extraHeaders: $"Accept-Ranges: bytes\r\nContent-Range: bytes */{fileLength.ToString(CultureInfo.InvariantCulture)}\r\n",
                cancellationToken: cancellationToken);
            return;
        }

        long contentLength = fileLength == 0 ? 0 : end - start + 1;
        int statusCode = isPartial ? 206 : 200;
        string reason = isPartial ? "Partial Content" : "OK";
        StringBuilder response = new()
        {
            Capacity = 512
        };
        response.Append("HTTP/1.1 ")
            .Append(statusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(GetMimeType(filePath)).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n")
            .Append("Access-Control-Allow-Headers: Range\r\n")
            .Append("Access-Control-Allow-Private-Network: true\r\n")
            .Append("Access-Control-Expose-Headers: Accept-Ranges, Content-Length, Content-Range\r\n")
            .Append("Connection: close\r\n");

        if (isPartial)
        {
            response.Append("Content-Range: bytes ")
                .Append(start.ToString(CultureInfo.InvariantCulture)).Append('-')
                .Append(end.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(fileLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        response.Append("\r\n");
        await responseStream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), cancellationToken);

        if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || contentLength == 0)
        {
            return;
        }

        file.Position = start;
        byte[] buffer = new byte[CopyBufferSize];
        long remaining = contentLength;
        while (remaining > 0)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, remaining);
            int bytesRead = await file.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await responseStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            remaining -= bytesRead;
        }
    }

    private static bool TryParseByteRange(string? value, long fileLength, out long start, out long end)
    {
        start = 0;
        end = 0;

        if (fileLength <= 0 || string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string range = value[6..].Trim();
        if (range.Contains(','))
        {
            return false;
        }

        int dashIndex = range.IndexOf('-');
        if (dashIndex < 0)
        {
            return false;
        }

        string first = range[..dashIndex].Trim();
        string last = range[(dashIndex + 1)..].Trim();

        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out long suffixLength) || suffixLength <= 0)
            {
                return false;
            }

            start = Math.Max(fileLength - suffixLength, 0);
            end = fileLength - 1;
            return true;
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            || start < 0 || start >= fileLength)
        {
            return false;
        }

        if (last.Length == 0)
        {
            end = fileLength - 1;
            return true;
        }

        if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, fileLength - 1);
        return true;
    }

    private static async Task WriteEmptyResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string? extraHeaders = null,
        bool allowMethods = false,
        CancellationToken cancellationToken = default)
    {
        StringBuilder response = new();
        response.Append("HTTP/1.1 ")
            .Append(statusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Length: 0\r\n")
            .Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Access-Control-Allow-Headers: Range\r\n");

        if (allowMethods)
        {
            response.Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n")
                .Append("Access-Control-Allow-Private-Network: true\r\n");
        }

        if (extraHeaders is not null)
        {
            response.Append(extraHeaders);
        }

        response.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), cancellationToken);
    }

    private static string GetMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".wmv" => "video/x-ms-wmv",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _listener?.Stop();
    }
}
