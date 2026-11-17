using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Tagium.Core.Search;

// The parsed representation contains no SQL. Comparison operators and field names are
// closed sets so the SQL compiler never interpolates untrusted search text as SQL syntax.
internal abstract record SearchFilter(bool IsNegated);
internal sealed record TextFilter(string Text, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record TagFilter(string Text, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record CategoryFilter(string Category, string Text, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record ExtensionFilter(string Extension, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record RatingFilter(SearchComparison Operator, int Rating, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record SizeFilter(SearchComparison Operator, long Bytes, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record DateFilter(SearchDateField Field, SearchComparison Operator, DateTime Date, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record MissingFilter(bool Missing, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record HasFilter(SearchPresenceField Field, bool IsNegated) : SearchFilter(IsNegated);
internal sealed record UntaggedFilter(bool IsNegated) : SearchFilter(IsNegated);

internal enum SearchComparison { Equal, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual }
internal enum SearchDateField { Added, Modified }
internal enum SearchPresenceField { Rating, Title, Description, Tags }

/// <summary>Interprets the user-facing search language without knowing the database schema.</summary>
internal static class SearchQueryParser
{
    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "artist", "character", "series", "location", "person", "franchise", "species", "year", "meta"
    };

    public static IReadOnlySet<string> CategoryKeys => ValidCategories;

    private static readonly Dictionary<string, string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rating"] = "rating", ["size"] = "size", ["ext"] = "ext", ["extension"] = "ext",
        ["tag"] = "tag", ["missing"] = "missing", ["has"] = "has",
        ["added"] = "added", ["modified"] = "modified"
    };

    public static IReadOnlyList<SearchFilter> Parse(string searchText, ILogger logger)
    {
        List<SearchFilter> filters = [];
        string[] tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string token in tokens)
        {
            bool negated = token.StartsWith('-') && token.Length > 1;
            string rawToken = negated ? token[1..] : token;
            int colon = rawToken.IndexOf(':');
            if (colon <= 0 || colon >= rawToken.Length - 1)
            {
                filters.Add(string.Equals(rawToken, "untagged", StringComparison.OrdinalIgnoreCase)
                    ? new UntaggedFilter(negated)
                    : new TextFilter(rawToken, negated));
                continue;
            }

            string key = rawToken[..colon];
            string value = rawToken[(colon + 1)..];
            KnownKeys.TryGetValue(key, out string? kind);
            switch (kind)
            {
                case "rating":
                {
                    (SearchComparison op, string rest) = SplitOperator(value);
                    if (int.TryParse(rest, out int rating))
                    {
                        filters.Add(new RatingFilter(op, rating, negated));
                    }
                    else
                    {
                        logger.LogWarning("Skipped invalid rating search filter value: {Value}", value);
                    }
                    break;
                }
                case "size":
                {
                    (SearchComparison op, string rest) = SplitOperator(value);
                    long? bytes = ParseSizeToBytes(rest);
                    if (bytes.HasValue)
                    {
                        filters.Add(new SizeFilter(op, bytes.Value, negated));
                    }
                    else
                    {
                        logger.LogWarning("Skipped invalid size search filter value: {Value}", value);
                    }
                    break;
                }
                case "ext":
                    filters.Add(new ExtensionFilter(value.TrimStart('.').ToLowerInvariant(), negated));
                    break;
                case "tag":
                    filters.Add(new TagFilter(value, negated));
                    break;
                case "missing":
                {
                    bool? missing = value.Trim().ToLowerInvariant() switch
                    {
                        "true" or "yes" or "1" => true,
                        "false" or "no" or "0" => false,
                        _ => null
                    };
                    if (missing.HasValue)
                    {
                        filters.Add(new MissingFilter(missing.Value, negated));
                    }
                    else
                    {
                        logger.LogWarning("Skipped invalid missing search filter value: {Value}", value);
                    }
                    break;
                }
                case "has":
                {
                    SearchPresenceField? field = value.Trim().ToLowerInvariant() switch
                    {
                        "rating" => SearchPresenceField.Rating,
                        "title" => SearchPresenceField.Title,
                        "description" => SearchPresenceField.Description,
                        "tags" => SearchPresenceField.Tags,
                        _ => null
                    };
                    filters.Add(field.HasValue ? new HasFilter(field.Value, negated) : new TextFilter(rawToken, negated));
                    break;
                }
                case "added":
                case "modified":
                {
                    (SearchComparison op, string rest) = SplitOperator(value);
                    if (!DateTime.TryParse(rest, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
                    {
                        logger.LogWarning("Skipped invalid {Key} search filter value: {Value}", key, value);
                        break;
                    }
                    // Both a bare date and an explicit equals mean "on or after".
                    if (op == SearchComparison.Equal)
                    {
                        op = SearchComparison.GreaterThanOrEqual;
                    }
                    SearchDateField field = string.Equals(key, "added", StringComparison.OrdinalIgnoreCase)
                        ? SearchDateField.Added : SearchDateField.Modified;
                    filters.Add(new DateFilter(field, op, date, negated));
                    break;
                }
                default:
                    filters.Add(ValidCategories.Contains(key)
                        ? new CategoryFilter(key, value, negated)
                        : new TextFilter(rawToken, negated));
                    break;
            }
        }

        return filters;
    }

    private static (SearchComparison Operator, string Rest) SplitOperator(string value)
    {
        if (value.StartsWith(">="))
        {
            return (SearchComparison.GreaterThanOrEqual, value[2..]);
        }
        if (value.StartsWith("<="))
        {
            return (SearchComparison.LessThanOrEqual, value[2..]);
        }
        if (value.StartsWith(">"))
        {
            return (SearchComparison.GreaterThan, value[1..]);
        }
        if (value.StartsWith("<"))
        {
            return (SearchComparison.LessThan, value[1..]);
        }
        if (value.StartsWith("="))
        {
            return (SearchComparison.Equal, value[1..]);
        }
        return (SearchComparison.Equal, value);
    }

    private static long? ParseSizeToBytes(string value)
    {
        value = value.Trim().ToLowerInvariant();
        int index = 0;
        while (index < value.Length && (char.IsDigit(value[index]) || value[index] == '.'))
        {
            index++;
        }

        if (index == 0 || !double.TryParse(value[..index], out double number))
        {
            return null;
        }

        string unit = value[index..].Trim();
        double multiplier = unit switch
        {
            "kb" or "k" => 1024.0,
            "mb" or "m" => 1024.0 * 1024.0,
            "gb" or "g" => 1024.0 * 1024.0 * 1024.0,
            _ => 1.0
        };
        return (long)(number * multiplier);
    }
}
