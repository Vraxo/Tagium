using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Tagium.Core;

namespace Tagium.Core.Tests.Core;

public class LogTimingTests
{
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public void Start_OnDispose_LogsCompletionMessageAtInformationLevel()
    {
        ListLogger logger = new();

        LogTiming timer = LogTiming.Start(logger, "Hello {Name}", "World");
        timer.Dispose();

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
        logger.Entries[0].Message.Should().Contain("Hello World");
        logger.Entries[0].Message.Should().Contain("completed in");
        logger.Entries[0].Message.Should().EndWith(" ms");
    }

    [Fact]
    public void StartDebug_OnDispose_LogsCompletionMessageAtDebugLevel()
    {
        ListLogger logger = new();

        LogTiming timer = LogTiming.StartDebug(logger, "Plain message");
        timer.Dispose();

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Debug);
        logger.Entries[0].Message.Should().StartWith("Plain message completed in ");
        logger.Entries[0].Message.Should().EndWith(" ms");
    }
}
