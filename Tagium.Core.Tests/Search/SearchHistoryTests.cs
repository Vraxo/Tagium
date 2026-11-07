using AwesomeAssertions;
using Tagium.Core.Search;

namespace Tagium.Core.Tests.Search;

public class SearchHistoryTests
{
    [Fact]
    public void Record_NewQuery_GoesToTheFront()
    {
        IReadOnlyList<string> history = SearchHistory.Record(["photos", "videos"], "rating:>3");

        history.Should().Equal("rating:>3", "photos", "videos");
    }

    [Fact]
    public void Record_EmptyHistory_StartsWithTheNewQuery()
    {
        SearchHistory.Record([], "tag:beach").Should().Equal("tag:beach");
    }

    [Fact]
    public void Record_RepeatedQuery_IsPromotedNotDuplicated()
    {
        SearchHistory.Record(["photos", "videos", "tag:beach"], "videos")
            .Should().Equal("videos", "photos", "tag:beach");
    }

    [Fact]
    public void Record_DuplicateIsMatchedCaseInsensitively()
    {
        SearchHistory.Record(["Photos", "videos"], "photos").Should().Equal("photos", "videos");
    }

    [Fact]
    public void Record_ShorterPrefixIsDroppedWhenTheLongerQueryArrives()
    {
        // Typing "pho" then stopping then finishing "photos" must not leave "pho" behind.
        SearchHistory.Record(["pho", "videos"], "photos").Should().Equal("photos", "videos");
    }

    [Fact]
    public void Record_LongerQueryIsKept_WhenTheUserNarrowsDown()
    {
        // "photos 2024" stays: it is a different query, not a prefix of the new one.
        SearchHistory.Record(["photos 2024"], "photos").Should().Equal("photos", "photos 2024");
    }

    [Fact]
    public void Record_TrimsTheQueryAndItsEntries()
    {
        SearchHistory.Record(["  videos  "], "  photos ").Should().Equal("photos", "videos");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_BlankQuery_LeavesTheHistoryAlone(string? query)
    {
        SearchHistory.Record(["photos"], query).Should().Equal("photos");
    }

    [Fact]
    public void Record_StopsAtTheLimit()
    {
        IReadOnlyList<string> history = SearchHistory.Record(["b", "c", "d"], "a", limit: 3);

        history.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Record_DoesNotMutateTheInputList()
    {
        List<string> existing = ["photos", "videos"];

        _ = SearchHistory.Record(existing, "tag:beach");

        existing.Should().Equal("photos", "videos");
    }

    [Fact]
    public void Suggestions_EmptyBox_OffersEverything()
    {
        SearchHistory.Suggestions(["photos", "videos"], "").Should().Equal("photos", "videos");
    }

    [Fact]
    public void Suggestions_FilterByContainedText()
    {
        SearchHistory.Suggestions(["photos 2024", "videos", "pho 2025"], "pho")
            .Should().Equal("photos 2024", "pho 2025");
    }

    [Fact]
    public void Suggestions_OmitTheTextAlreadyTyped()
    {
        SearchHistory.Suggestions(["photos", "photos 2024"], "photos").Should().Equal("photos 2024");
    }

    [Fact]
    public void Suggestions_NoMatch_ReturnsEmpty()
    {
        SearchHistory.Suggestions(["photos"], "zzz").Should().BeEmpty();
    }

    [Fact]
    public void Suggestions_StopAtTheLimit()
    {
        SearchHistory.Suggestions(["a", "b", "c"], "", limit: 2).Should().Equal("a", "b");
    }

    [Theory]
    [InlineData("  photos  ", "photos")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalize_TrimsOrRejects(string? input, string? expected)
    {
        SearchHistory.Normalize(input).Should().Be(expected);
    }
}
