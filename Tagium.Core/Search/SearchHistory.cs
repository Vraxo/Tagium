namespace Tagium.Core.Search;

/// <summary>
/// Rules behind the recent-searches list: what enters it, in what order, and what the box offers
/// back. Pure, so the ordering, dedupe, prefix-collapsing and cap are unit-tested instead of only
/// being observed through the UI.
/// </summary>
public static class SearchHistory
{
    public const int DefaultLimit = 10;

    /// <summary>Trims the query. Returns <c>null</c> when nothing searchable is left.</summary>
    public static string? Normalize(string? query)
    {
        string trimmed = (query ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Puts <paramref name="query"/> at the front of the list, then keeps the previous entries
    /// minus anything it supersedes: duplicates (case-insensitive) and shorter prefixes of it, so
    /// typing "pho" and then "photos" leaves one entry rather than two. Capped at
    /// <paramref name="limit"/>; a blank query leaves the list untouched.
    /// </summary>
    public static IReadOnlyList<string> Record(
        IReadOnlyList<string> existing,
        string? query,
        int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (limit <= 0)
        {
            return [];
        }

        if (Normalize(query) is not { } normalized)
        {
            return [.. existing.Take(limit)];
        }

        List<string> updated = [normalized];

        foreach (string entry in existing)
        {
            if (updated.Count >= limit)
            {
                break;
            }

            if (Normalize(entry) is not { } candidate)
            {
                continue;
            }

            if (IsSameQuery(candidate, normalized) || IsPrefixOf(candidate, normalized))
            {
                continue;
            }

            if (updated.Any(kept => IsSameQuery(kept, candidate)))
            {
                continue;
            }

            updated.Add(candidate);
        }

        return updated;
    }

    /// <summary>
    /// Entries worth offering for the text currently in the box: everything when the box is
    /// empty, otherwise the entries containing that text, never the text itself.
    /// </summary>
    public static IReadOnlyList<string> Suggestions(
        IReadOnlyList<string> existing,
        string? prefix,
        int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (limit <= 0)
        {
            return [];
        }

        string? filter = Normalize(prefix);
        List<string> matches = [];

        foreach (string entry in existing)
        {
            if (matches.Count >= limit)
            {
                break;
            }

            if (Normalize(entry) is not { } candidate)
            {
                continue;
            }

            if (filter is not null)
            {
                if (IsSameQuery(candidate, filter))
                {
                    continue;
                }

                if (!candidate.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (matches.Any(kept => IsSameQuery(kept, candidate)))
            {
                continue;
            }

            matches.Add(candidate);
        }

        return matches;
    }

    private static bool IsSameQuery(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrefixOf(string candidate, string query)
    {
        return candidate.Length < query.Length
            && query.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);
    }
}
