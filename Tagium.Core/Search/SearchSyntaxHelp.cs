namespace Tagium.Core.Search;

/// <summary>
/// One row of the in-app search syntax reference. The examples are real queries, not prose, so
/// the tests can prove the help never documents something the compiler does not accept.
/// </summary>
public sealed record SearchHelpTopic(string Syntax, string Description, IReadOnlyList<string> Examples);

/// <summary>
/// The query language as the user sees it. Kept next to the compiler and tested against it:
/// <c>SearchSyntaxHelpTests</c> compiles every example and checks that the category list matches
/// <see cref="SearchQueryCompiler.CategoryKeys"/>, so documentation cannot drift from behaviour.
/// </summary>
public static class SearchSyntaxHelp
{
    public const string Summary = "Terms combine with AND. Prefix any term with - to exclude it.";

    public static IReadOnlyList<SearchHelpTopic> Topics { get; } =
    [
        new("beach", "Matches file name, path, tag names and aliases.", ["beach"]),
        new("tag:text", "Files carrying a tag or alias containing the text.", ["tag:beach"]),
        new("rating:n", "Star rating; combine with >, >=, < or <=.", ["rating:>=4"]),
        new("size:n", "File size in bytes, or with kb, mb or gb.", ["size:>5mb"]),
        new("ext:mp4", "File extension.", ["ext:mp4", "ext:jpg"]),
        new("added:date", "Added on or after a date; accepts an operator too.", ["added:2024-06-01", "added:<2024-01-01"]),
        new("modified:date", "Last changed on or after a date.", ["modified:<2024-06-01"]),
        new("has:field", "Only files with a value: rating, title, description or tags.", ["has:title", "has:tags"]),
        new("missing:true", "Files whose original path is gone, or false for the rest.", ["missing:true", "missing:false"]),
        new("untagged", "Files with no tags at all.", ["untagged"]),
        new("-term", "Excludes a term, whatever form it takes.", ["-tag:beach", "-ext:png"]),
        new(
            "artist: / series: / …",
            "Category filters: artist, character, series, location, person, franchise, species, year, meta.",
            ["artist:monet", "series:aurora"]),
    ];
}
