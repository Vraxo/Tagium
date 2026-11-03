using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Tagium.Core.Helpers;

public readonly struct LogTiming : IDisposable
{
    private readonly ILogger _logger;
    private readonly LogLevel _level;
    private readonly string _messageTemplate;
    private readonly object?[]? _args;
    private readonly long _startTimestamp;

    private LogTiming(ILogger logger, LogLevel level, string messageTemplate, object?[]? args)
    {
        _logger = logger;
        _level = level;
        _messageTemplate = messageTemplate;
        _args = args;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    public static LogTiming Start(ILogger logger, string messageTemplate, params object?[]? args)
    {
        return new LogTiming(logger, LogLevel.Information, messageTemplate, args);
    }

    public static LogTiming StartDebug(ILogger logger, string messageTemplate, params object?[]? args)
    {
        return new LogTiming(logger, LogLevel.Debug, messageTemplate, args);
    }

    public void Dispose()
    {
        long elapsed = Stopwatch.GetTimestamp() - _startTimestamp;
        double ms = (double)elapsed * 1000 / Stopwatch.Frequency;

        string format = $"{_messageTemplate} completed in {{ElapsedMs:F2}} ms";

        object?[] finalArgs;
        if (_args == null || _args.Length == 0)
        {
            finalArgs = [ms];
        }
        else
        {
            finalArgs = new object?[_args.Length + 1];
            Array.Copy(_args, finalArgs, _args.Length);
            finalArgs[_args.Length] = ms;
        }

        _logger.Log(_level, format, finalArgs);
    }
}