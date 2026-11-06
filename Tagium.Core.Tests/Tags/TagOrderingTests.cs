using AwesomeAssertions;
using Tagium.Core.Tags;

namespace Tagium.Core.Tests.Tags;

public class TagOrderingTests
{
    [Fact]
    public void ByName_SortsCaseInsensitivelyAndBreaksTiesDeterministically()
    {
        Tag[] tags =
        [
            new() { Name = "pear" },
            new() { Name = "apple" },
            new() { Name = "Banana" },
            new() { Name = "Apple" }
        ];

        TagOrdering.ByName(tags)
            .Select(tag => tag.Name)
            .Should()
            .Equal("Apple", "apple", "Banana", "pear");
    }
}
