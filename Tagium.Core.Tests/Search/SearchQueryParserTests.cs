using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tagium.Core.Search;

namespace Tagium.Core.Tests.Search;

public class SearchQueryParserTests
{
    private static IReadOnlyList<SearchFilter> Parse(string text) =>
        SearchQueryParser.Parse(text, NullLogger.Instance);

    [Fact]
    public void Parse_ProducesTypedFiltersInInputOrder()
    {
        IReadOnlyList<SearchFilter> filters = Parse(
            "Beach -TAG:Draft -Rating:>=4 size:<=2mb EXT:.JPG Year:2024 missing:YES -has:Title untagged");

        filters.Should().Equal(new SearchFilter[]
        {
            new TextFilter("Beach", false),
            new TagFilter("Draft", true),
            new RatingFilter(SearchComparison.GreaterThanOrEqual, 4, true),
            new SizeFilter(SearchComparison.LessThanOrEqual, 2097152L, false),
            new ExtensionFilter("jpg", false),
            new CategoryFilter("Year", "2024", false),
            new MissingFilter(true, false),
            new HasFilter(SearchPresenceField.Title, true),
            new UntaggedFilter(false)
        });
    }

    [Theory]
    [InlineData("rating:5", "Equal")]
    [InlineData("rating:=5", "Equal")]
    [InlineData("rating:>5", "GreaterThan")]
    [InlineData("rating:>=5", "GreaterThanOrEqual")]
    [InlineData("rating:<5", "LessThan")]
    [InlineData("rating:<=5", "LessThanOrEqual")]
    public void Parse_RecognizesComparisonOperators(string text, string comparison)
    {
        SearchComparison expected = Enum.Parse<SearchComparison>(comparison);
        Parse(text).Should().Equal(new RatingFilter(expected, 5, false));
    }

    [Theory]
    [InlineData("size:512", 512L)]
    [InlineData("size:2k", 2048L)]
    [InlineData("size:2GB", 2147483648L)]
    [InlineData("size:512unknown", 512L)]
    public void Parse_SizeUsesBinaryUnitsAndTreatsUnknownSuffixAsBytes(string text, long bytes)
    {
        Parse(text).Should().Equal(new SizeFilter(SearchComparison.Equal, bytes, false));
    }

    [Fact]
    public void Parse_DatesUseUtcAndTreatEqualsAsOnOrAfter()
    {
        IReadOnlyList<SearchFilter> filters = Parse("added:=2026-01-01 -modified:<2025-12-31");

        filters.Should().Equal(new SearchFilter[]
        {
            new DateFilter(SearchDateField.Added, SearchComparison.GreaterThanOrEqual,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), false),
            new DateFilter(SearchDateField.Modified, SearchComparison.LessThan,
                new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), true)
        });
    }

    [Fact]
    public void Parse_UnknownOrIncompleteFiltersRemainRawText()
    {
        IReadOnlyList<SearchFilter> filters = Parse("-foo:bar -has:unknown tag: :foo - --tag:art");

        filters.Should().Equal(new SearchFilter[]
        {
            new TextFilter("foo:bar", true),
            new TextFilter("has:unknown", true),
            new TextFilter("tag:", false),
            new TextFilter(":foo", false),
            new TextFilter("-", false),
            new TextFilter("-tag:art", true)
        });
    }

    [Fact]
    public void Parse_InvalidValuesAreSkippedAndWarnedWithoutDroppingOtherFilters()
    {
        RecordingLogger logger = new();

        IReadOnlyList<SearchFilter> filters = SearchQueryParser.Parse(
            "first rating:no size:huge missing:maybe ADDED:bad last", logger);

        filters.Should().Equal(new SearchFilter[] { new TextFilter("first", false), new TextFilter("last", false) });
        logger.Messages.Should().Equal(
            "Skipped invalid rating search filter value: no",
            "Skipped invalid size search filter value: huge",
            "Skipped invalid missing search filter value: maybe",
            "Skipped invalid ADDED search filter value: bad");
    }

    [Fact]
    public void Parse_SplitsOnSpacesAndTrimsTokenEdges()
    {
        IReadOnlyList<SearchFilter> filters = Parse("  \tfirst\tsecond\t  third\n ");

        filters.Should().Equal(new SearchFilter[]
        {
            new TextFilter("first\tsecond", false),
            new TextFilter("third", false)
        });
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
