using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tagium.Core.Search;
using Tagium.Core.Settings;

namespace Tagium.Core.Tests.Search;

/// <summary>
/// Keeps the in-app syntax reference honest: every documented example has to compile into a real
/// filter, keyed examples have to reach a field clause rather than the raw-tag fallback, and every
/// category the compiler accepts has to be mentioned in the docs.
/// </summary>
public class SearchSyntaxHelpTests
{
    private readonly SearchQueryCompiler _compiler = new(NullLogger<SearchQueryCompiler>.Instance);

    private IEnumerable<string> AllExamples =>
        SearchSyntaxHelp.Topics.SelectMany(topic => topic.Examples);

    [Fact]
    public void EveryTopic_HasSyntaxDescriptionAndExamples()
    {
        SearchSyntaxHelp.Topics.Should().NotBeEmpty();
        SearchSyntaxHelp.Summary.Should().NotBeNullOrWhiteSpace();

        foreach (SearchHelpTopic topic in SearchSyntaxHelp.Topics)
        {
            topic.Syntax.Should().NotBeNullOrWhiteSpace();
            topic.Description.Should().NotBeNullOrWhiteSpace();
            topic.Examples.Should().NotBeEmpty($"{topic.Syntax} needs at least one example");
        }
    }

    [Fact]
    public void EveryDocumentedExample_ProducesAFilter()
    {
        string baseline = Compile("").Sql;

        foreach (string example in AllExamples)
        {
            Compile(example).Sql.Should().NotBe(
                baseline,
                $"the documented example \"{example}\" must add a filter; if the compiler ignores it, the help is lying");
        }
    }

    [Fact]
    public void KeyedExamples_ReachAFieldClause()
    {
        foreach (string example in AllExamples.Where(HasKey))
        {
            // An unrecognised prefix silently degrades to a raw tag search. Comparing the SQL of
            // the real key against the SQL of a deliberately bogus prefix proves the key exists.
            Compile(example).Sql.Should().NotBe(
                Compile("zz" + example).Sql,
                $"\"{example}\" should be handled as a field filter, not fall back to a raw tag search");
        }
    }

    [Fact]
    public void EveryCategoryFilter_IsMentionedInTheHelp()
    {
        // Syntax and description together: the category row spells the full list out in prose.
        string documented = string.Join(
            " ",
            SearchSyntaxHelp.Topics.Select(topic => topic.Syntax + " " + topic.Description));

        foreach (string category in SearchQueryCompiler.CategoryKeys)
        {
            documented.Should().Contain(
                category,
                $"the syntax help should mention the {category} category filter");
        }
    }

    private static bool HasKey(string example)
    {
        return example.Contains(':') && !example.StartsWith('-');
    }

    private CompiledQuery Compile(string searchText)
    {
        return _compiler.Compile(searchText, SortBy.Path, SortDirection.Asc);
    }
}
