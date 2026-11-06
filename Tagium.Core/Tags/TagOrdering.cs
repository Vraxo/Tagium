namespace Tagium.Core.Tags;

public static class TagOrdering
{
    public static List<Tag> ByName(IEnumerable<Tag> tags)
    {
        return tags
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tag => tag.Name, StringComparer.Ordinal)
            .ToList();
    }
}
