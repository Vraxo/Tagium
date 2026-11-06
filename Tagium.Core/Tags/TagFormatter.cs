using System.Text.RegularExpressions;

namespace Tagium.Core.Tags;

public static class TagFormatter
{
    public static string CanonicalizeName(string? input)
    {
        return string.IsNullOrWhiteSpace(input)
            ? string.Empty
            : input.Trim().ToLowerInvariant();
    }

    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string s = input.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[\s\-_\+\.]+", "_");
        s = Regex.Replace(s, @"[^a-z0-9_]", "");
        s = Regex.Replace(s, @"_+", "_");
        return s.Trim('_');
    }
}