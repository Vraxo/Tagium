using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tagium.Core.Search;
using Tagium.Core.Settings;

namespace Tagium.Core.Tests.Search;

public class SearchQueryCompilerTests
{
    private readonly SearchQueryCompiler _compiler = new(NullLogger<SearchQueryCompiler>.Instance);

    private CompiledQuery Compile(string searchText, SortBy sortBy = SortBy.Path, SortDirection direction = SortDirection.Asc)
    {
        return _compiler.Compile(searchText, sortBy, direction);
    }

    [Fact]
    public void Compile_EmptySearch_ReturnsBaseQueryWithoutFilters()
    {
        CompiledQuery query = Compile("");

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().Contain("SELECT DISTINCT F.Id, F.Path, F.Name");
        query.Sql.Should().Contain("FROM cache.Files F");
        query.Sql.Should().Contain("WHERE EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1");
        query.Sql.Should().Contain("ORDER BY F.Path ASC, F.Path ASC");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public void Compile_WhitespaceOnly_AddsNoParameters(string searchText)
    {
        CompiledQuery query = Compile(searchText);

        query.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Compile_PlainTerm_MatchesFileNamePathAndTags()
    {
        CompiledQuery query = Compile("landscape");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Name.Should().Be("@p0");
        query.Parameters[0].Value.Should().Be("%landscape%");
        query.Sql.Should().Contain("AND (F.Name LIKE @p0 OR F.Path LIKE @p0 OR");
        query.Sql.Should().Contain("FROM cache.FileTags FT");
        query.Sql.Should().Contain("T.Name LIKE @p0 OR TA.Alias LIKE @p0");
    }

    [Fact]
    public void Compile_NegatedTerm_ExcludesMatches()
    {
        CompiledQuery query = Compile("-draft");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be("%draft%");
        query.Sql.Should().Contain("AND NOT (F.Name LIKE @p0 OR F.Path LIKE @p0 OR");
    }

    [Fact]
    public void Compile_MultipleTerms_AreAndCombinedWithSequentialParameters()
    {
        CompiledQuery query = Compile("foo   bar");

        query.Parameters.Select(p => p.Name).Should().Equal("@p0", "@p1");
        query.Parameters.Select(p => p.Value).Should().Equal("%foo%", "%bar%");
        query.Sql.Should().Contain("F.Name LIKE @p0");
        query.Sql.Should().Contain("F.Name LIKE @p1");
    }

    [Theory]
    [InlineData("ext:JPG", "%.jpg")]
    [InlineData("extension:.WebP", "%.webp")]
    [InlineData("ext:png", "%.png")]
    public void Compile_ExtensionFilter_BuildsLowercaseLikePattern(string searchText, string expectedPattern)
    {
        CompiledQuery query = Compile(searchText);

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be(expectedPattern);
        query.Sql.Should().Contain("AND F.Path LIKE @p0");
    }

    [Fact]
    public void Compile_NegatedExtensionFilter_UsesNotLike()
    {
        CompiledQuery query = Compile("-ext:gif");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be("%.gif");
        query.Sql.Should().Contain("AND F.Path NOT LIKE @p0");
    }

    [Theory]
    [InlineData("size:>10mb", ">", 10485760L)]
    [InlineData("size:<=2GB", "<=", 2147483648L)]
    [InlineData("size:=512b", "=", 512L)]
    [InlineData("size:1.5kb", "=", 1536L)]
    [InlineData("size:2048", "=", 2048L)]
    public void Compile_SizeFilter_ParsesOperatorsAndUnits(string searchText, string expectedOperator, long expectedBytes)
    {
        CompiledQuery query = Compile(searchText);

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be(expectedBytes);
        query.Sql.Should().Contain($"AND F.Size {expectedOperator} @p0");
    }

    [Fact]
    public void Compile_NegatedSizeFilter_WrapsComparisonInNot()
    {
        CompiledQuery query = Compile("-size:>=1kb");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be(1024L);
        query.Sql.Should().Contain("AND NOT (F.Size >= @p0)");
    }

    [Fact]
    public void Compile_InvalidSizeFilter_IsSkipped()
    {
        CompiledQuery query = Compile("size:huge");

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().NotContain("AND F.Size");
        query.Sql.Should().NotContain("NOT (F.Size");
    }

    [Theory]
    [InlineData("rating:>=4", ">=", 4)]
    [InlineData("rating:<3", "<", 3)]
    [InlineData("rating:5", "=", 5)]
    public void Compile_RatingFilter_RequiresNonNullRating(string searchText, string expectedOperator, int expectedRating)
    {
        CompiledQuery query = Compile(searchText);

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be(expectedRating);
        query.Sql.Should().Contain($"AND (F.Rating IS NOT NULL AND F.Rating {expectedOperator} @p0)");
    }

    [Fact]
    public void Compile_NegatedRatingFilter_AllowsNullRatings()
    {
        CompiledQuery query = Compile("-rating:>2");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be(2);
        query.Sql.Should().Contain("AND (F.Rating IS NULL OR NOT (F.Rating > @p0))");
    }

    [Fact]
    public void Compile_InvalidRatingFilter_IsSkipped()
    {
        CompiledQuery query = Compile("rating:high");

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().NotContain("F.Rating IS NOT NULL");
        query.Sql.Should().NotContain("F.Rating IS NULL");
    }

    [Theory]
    [InlineData("artist", "artist")]
    [InlineData("Year", "Year")]
    public void Compile_CategoryFilter_MatchesTagCategoryAndAliases(string key, string expectedCategoryParam)
    {
        CompiledQuery query = Compile($"{key}:moe");

        query.Parameters.Should().HaveCount(2);
        query.Parameters[0].Name.Should().Be("@p0");
        query.Parameters[0].Value.Should().Be(expectedCategoryParam);
        query.Parameters[1].Name.Should().Be("@p1");
        query.Parameters[1].Value.Should().Be("%moe%");
        query.Sql.Should().Contain("T.Category = @p0");
        query.Sql.Should().Contain("T.Name LIKE @p1 OR TA.Alias LIKE @p1");
    }

    [Fact]
    public void Compile_NegatedCategoryFilter_WrapsExistsInNot()
    {
        CompiledQuery query = Compile("-series:firefly");

        query.Parameters.Select(p => p.Value).Should().Equal("series", "%firefly%");
        query.Sql.Should().Contain("AND NOT (");
        query.Sql.Should().Contain("T.Category = @p0");
    }

    [Fact]
    public void Compile_UnknownPrefix_FallsBackToTagSearchOnRawToken()
    {
        CompiledQuery query = Compile("foo:bar");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be("%foo:bar%");
        query.Sql.Should().Contain("F.Name LIKE @p0");
        query.Sql.Should().NotContain("T.Category");
    }

    [Theory]
    [InlineData(SortBy.Path, SortDirection.Asc, "ORDER BY F.Path ASC, F.Path ASC")]
    [InlineData(SortBy.Path, SortDirection.Desc, "ORDER BY F.Path DESC, F.Path ASC")]
    [InlineData(SortBy.Name, SortDirection.Asc, "ORDER BY F.Name ASC, F.Path ASC")]
    [InlineData(SortBy.Name, SortDirection.Desc, "ORDER BY F.Name DESC, F.Path ASC")]
    [InlineData(SortBy.DateAdded, SortDirection.Asc, "ORDER BY F.DateAdded ASC, F.Path ASC")]
    [InlineData(SortBy.DateAdded, SortDirection.Desc, "ORDER BY F.DateAdded DESC, F.Path ASC")]
    [InlineData(SortBy.Size, SortDirection.Asc, "ORDER BY F.Size ASC, F.Path ASC")]
    [InlineData(SortBy.Size, SortDirection.Desc, "ORDER BY F.Size DESC, F.Path ASC")]
    [InlineData(SortBy.ModifiedTime, SortDirection.Asc, "ORDER BY F.ModifiedTime ASC, F.Path ASC")]
    [InlineData(SortBy.ModifiedTime, SortDirection.Desc, "ORDER BY F.ModifiedTime DESC, F.Path ASC")]
    [InlineData(SortBy.Rating, SortDirection.Asc, "ORDER BY F.Rating ASC, F.Path ASC")]
    [InlineData(SortBy.Rating, SortDirection.Desc, "ORDER BY F.Rating DESC, F.Path ASC")]
    public void Compile_AppliesRequestedSortOrdering(SortBy sortBy, SortDirection direction, string expectedOrderBy)
    {
        CompiledQuery query = _compiler.Compile("", sortBy, direction);

        query.Sql.Should().Contain(expectedOrderBy);
    }

    [Fact]
    public void Compile_CombinedQuery_NumbersParametersAcrossAllClauseTypes()
    {
        CompiledQuery query = Compile("landscape -draft ext:webp rating:>=4 size:>1mb artist:moe");

        query.Parameters.Select(p => p.Name).Should().Equal("@p0", "@p1", "@p2", "@p3", "@p4", "@p5", "@p6");
        query.Parameters.Select(p => p.Value).Should().Equal("%landscape%", "%draft%", "%.webp", 4, 1048576L, "artist", "%moe%");
    }

    [Fact]
    public void Compile_TagFilter_MatchesOnlyTagNamesAndAliases()
    {
        CompiledQuery query = Compile("tag:landscape");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be("%landscape%");
        query.Sql.Should().Contain("T.Name LIKE @p0 OR TA.Alias LIKE @p0");
        query.Sql.Should().NotContain("F.Name LIKE @p0");
        query.Sql.Should().NotContain("F.Path LIKE @p0");
    }

    [Fact]
    public void Compile_NegatedTagFilter_ExcludesTagMatches()
    {
        CompiledQuery query = Compile("-tag:draft");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be("%draft%");
        query.Sql.Should().Contain("AND NOT (");
        query.Sql.Should().Contain("T.Name LIKE @p0 OR TA.Alias LIKE @p0");
    }

    [Fact]
    public void Compile_UntaggedKeyword_RequiresNoTags()
    {
        CompiledQuery query = Compile("untagged");

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().Contain("AND NOT EXISTS (SELECT 1 FROM cache.FileTags FT WHERE FT.FileId = F.Id)");
    }

    [Fact]
    public void Compile_NegatedUntaggedKeyword_RequiresAtLeastOneTag()
    {
        CompiledQuery query = Compile("-untagged");

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().Contain("AND EXISTS (SELECT 1 FROM cache.FileTags FT WHERE FT.FileId = F.Id)");
    }

    [Theory]
    [InlineData("missing:true", "AND F.IsMissing = 1")]
    [InlineData("missing:yes", "AND F.IsMissing = 1")]
    [InlineData("missing:1", "AND F.IsMissing = 1")]
    [InlineData("missing:false", "AND F.IsMissing = 0")]
    [InlineData("missing:no", "AND F.IsMissing = 0")]
    [InlineData("missing:0", "AND F.IsMissing = 0")]
    [InlineData("-missing:true", "AND F.IsMissing = 0")]
    [InlineData("-missing:false", "AND F.IsMissing = 1")]
    public void Compile_MissingFilter_MapsBooleanValues(string searchText, string expectedClause)
    {
        CompiledQuery query = Compile(searchText);

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().Contain(expectedClause);
    }

    [Fact]
    public void Compile_InvalidMissingFilter_IsSkipped()
    {
        CompiledQuery query = Compile("missing:maybe");

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().NotContain("F.IsMissing =");
    }

    [Theory]
    [InlineData("has:rating", false, "AND (F.Rating IS NOT NULL)")]
    [InlineData("has:rating", true, "AND (F.Rating IS NULL)")]
    [InlineData("has:title", false, "AND (F.Title IS NOT NULL AND F.Title <> '')")]
    [InlineData("has:title", true, "AND (F.Title IS NULL OR F.Title = '')")]
    [InlineData("has:description", false, "AND (F.Description IS NOT NULL AND F.Description <> '')")]
    [InlineData("has:tags", false, "AND (EXISTS (SELECT 1 FROM cache.FileTags FT WHERE FT.FileId = F.Id))")]
    [InlineData("has:tags", true, "AND (NOT EXISTS (SELECT 1 FROM cache.FileTags FT WHERE FT.FileId = F.Id))")]
    public void Compile_HasFilter_ChecksFieldPresence(string searchText, bool negate, string expectedClause)
    {
        CompiledQuery query = Compile(negate ? "-" + searchText : searchText);

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().Contain(expectedClause);
    }

    [Fact]
    public void Compile_UnknownHasField_FallsBackToTagSearchOnRawToken()
    {
        CompiledQuery query = Compile("has:bogus");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be("%has:bogus%");
        query.Sql.Should().Contain("F.Name LIKE @p0");
    }

    [Theory]
    [InlineData("added:>=2026-01-01", "F.DateAdded", ">=", 1767225600L)]
    [InlineData("added:2026-06-15", "F.DateAdded", ">=", 1781481600L)]
    [InlineData("modified:<2025-12-31", "F.ModifiedTime", "<", 1767139200L)]
    [InlineData("added:>2026-01-01", "F.DateAdded", ">", 1767225600L)]
    public void Compile_DateFilter_ConvertsDatesToUnixTimestamps(string searchText, string column, string expectedOperator, long expectedTimestamp)
    {
        CompiledQuery query = Compile(searchText);

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be(expectedTimestamp);
        query.Sql.Should().Contain($"AND {column} {expectedOperator} @p0");
    }

    [Fact]
    public void Compile_BareDate_MeansOnOrAfter()
    {
        CompiledQuery query = Compile("added:2026-01-01");

        query.Sql.Should().Contain("AND F.DateAdded >= @p0");
        query.Sql.Should().NotContain("F.DateAdded = @p0");
    }

    [Fact]
    public void Compile_NegatedDateFilter_WrapsInNot()
    {
        CompiledQuery query = Compile("-added:>2026-01-01");

        query.Parameters.Should().ContainSingle();
        query.Parameters[0].Value.Should().Be(1767225600L);
        query.Sql.Should().Contain("AND NOT (F.DateAdded > @p0)");
    }

    [Fact]
    public void Compile_InvalidDateFilter_IsSkipped()
    {
        CompiledQuery query = Compile("added:notadate");

        query.Parameters.Should().BeEmpty();
        query.Sql.Should().NotContain("F.DateAdded >=");
        query.Sql.Should().NotContain("F.DateAdded <");
    }

    [Fact]
    public void Compile_NewFilters_KeepSequentialParameterNumbering()
    {
        CompiledQuery query = Compile("tag:art untagged missing:true has:rating added:>=2026-01-01 foo");

        query.Parameters.Select(p => p.Name).Should().Equal("@p0", "@p1", "@p2");
        query.Parameters.Select(p => p.Value).Should().Equal("%art%", 1767225600L, "%foo%");
    }
}
