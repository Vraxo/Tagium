using AwesomeAssertions;
using Tagium.Core.Tags;

namespace Tagium.Core.Tests.Tags;

public class TagFormatterTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("!!!", "")]
    [InlineData("___", "")]
    [InlineData("My Cool Tag", "my_cool_tag")]
    [InlineData("  Spaced   Out  ", "spaced_out")]
    [InlineData("already_normalized", "already_normalized")]
    [InlineData("dots.and-dashes_underscores+plus", "dots_and_dashes_underscores_plus")]
    [InlineData("UPPER Case", "upper_case")]
    [InlineData("Café Ünicode", "caf_nicode")]
    [InlineData("123 456", "123_456")]
    [InlineData("a__b", "a_b")]
    [InlineData("-lead", "lead")]
    [InlineData("tag!!!", "tag")]
    public void Normalize_ProducesExpectedSlug(string? input, string expected)
    {
        TagFormatter.Normalize(input!).Should().Be(expected);
    }
}
