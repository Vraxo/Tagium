using Microsoft.Extensions.Logging;
using System.Text;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public record CompiledQuery(string Sql, List<(string Name, object Value)> Parameters);

public class SearchQueryCompiler
{
    private readonly ILogger<SearchQueryCompiler> _logger;

    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "artist", "character", "series", "location", "person", "franchise", "species", "year", "meta"
    };

    public SearchQueryCompiler(ILogger<SearchQueryCompiler> logger)
    {
        _logger = logger;
    }

    public CompiledQuery Compile(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
        string[] tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        StringBuilder query = new();
        query.AppendLine("SELECT DISTINCT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime, F.Rating, F.Title, F.Description");
        query.AppendLine("FROM cache.Files F");
        query.AppendLine("WHERE EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '" + Path.DirectorySeparatorChar + "'))");

        List<(string Name, object Value)> parameters = [];
        int paramCounter = 0;

        foreach (string token in tokens)
        {
            bool isNegated = false;
            string rawToken = token;

            if (rawToken.StartsWith('-') && rawToken.Length > 1)
            {
                isNegated = true;
                rawToken = rawToken[1..];
            }

            int colonIdx = rawToken.IndexOf(':');
            if (colonIdx > 0 && colonIdx < rawToken.Length - 1)
            {
                string key = rawToken[..colonIdx];
                string val = rawToken[(colonIdx + 1)..];

                if (string.Equals(key, "rating", StringComparison.OrdinalIgnoreCase))
                {
                    BuildRatingClause(val, isNegated, query, parameters, ref paramCounter);
                }
                else if (string.Equals(key, "size", StringComparison.OrdinalIgnoreCase))
                {
                    BuildSizeClause(val, isNegated, query, parameters, ref paramCounter);
                }
                else if (string.Equals(key, "ext", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "extension", StringComparison.OrdinalIgnoreCase))
                {
                    string extPattern = "%." + val.TrimStart('.').ToLowerInvariant();
                    string paramName = $"@p{paramCounter++}";
                    parameters.Add((paramName, extPattern));

                    if (isNegated)
                    {
                        query.AppendLine($"AND F.Path NOT LIKE {paramName}");
                    }
                    else
                    {
                        query.AppendLine($"AND F.Path LIKE {paramName}");
                    }
                }
                else if (ValidCategories.Contains(key))
                {
                    BuildCategoryClause(key, val, isNegated, query, parameters, ref paramCounter);
                }
                else
                {
                    BuildTagSearchClause(rawToken, isNegated, query, parameters, ref paramCounter);
                }
            }
            else
            {
                BuildTagSearchClause(rawToken, isNegated, query, parameters, ref paramCounter);
            }
        }

        query.AppendLine(GetOrderByClause(sortBy, sortDirection));

        return new CompiledQuery(query.ToString(), parameters);
    }

    private void BuildRatingClause(string val, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        (string op, string rStr) = SplitOperator(val);
        if (int.TryParse(rStr, out int rVal))
        {
            string paramName = $"@p{paramCounter++}";
            parameters.Add((paramName, rVal));

            if (isNegated)
            {
                query.AppendLine($"AND (F.Rating IS NULL OR NOT (F.Rating {op} {paramName}))");
            }
            else
            {
                query.AppendLine($"AND (F.Rating IS NOT NULL AND F.Rating {op} {paramName})");
            }
        }
        else
        {
            _logger.LogWarning("Skipped invalid rating search filter value: {Value}", val);
        }
    }

    private void BuildSizeClause(string val, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        (string op, string sStr) = SplitOperator(val);
        long? bytes = ParseSizeToBytes(sStr);
        if (bytes.HasValue)
        {
            string paramName = $"@p{paramCounter++}";
            parameters.Add((paramName, bytes.Value));

            if (isNegated)
            {
                query.AppendLine($"AND NOT (F.Size {op} {paramName})");
            }
            else
            {
                query.AppendLine($"AND F.Size {op} {paramName}");
            }
        }
        else
        {
            _logger.LogWarning("Skipped invalid size search filter value: {Value}", val);
        }
    }

    private void BuildCategoryClause(string key, string val, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string searchPattern = "%" + val + "%";
        string catParam = $"@p{paramCounter++}";
        string valParam = $"@p{paramCounter++}";

        parameters.Add((catParam, key));
        parameters.Add((valParam, searchPattern));

        string subQuery = $@"
            EXISTS (
                SELECT 1 FROM cache.FileTags FT
                JOIN Tags T ON FT.TagId = T.Id
                LEFT JOIN TagAliases TA ON T.Id = TA.TagId
                WHERE FT.FileId = F.Id AND T.Category = {catParam} AND (T.Name LIKE {valParam} OR TA.Alias LIKE {valParam})
            )";

        if (isNegated)
        {
            query.AppendLine($"AND NOT ({subQuery})");
        }
        else
        {
            query.AppendLine($"AND ({subQuery})");
        }
    }

    private void BuildTagSearchClause(string token, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        string termPattern = $"%{token}%";
        parameters.Add((paramName, termPattern));

        string subQuery = $@"
            EXISTS (
                SELECT 1 FROM cache.FileTags FT
                JOIN Tags T ON FT.TagId = T.Id
                LEFT JOIN TagAliases TA ON T.Id = TA.TagId
                WHERE FT.FileId = F.Id AND (T.Name LIKE {paramName} OR TA.Alias LIKE {paramName})
            )";

        if (isNegated)
        {
            query.AppendLine($"AND NOT (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {subQuery})");
        }
        else
        {
            query.AppendLine($"AND (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {subQuery})");
        }
    }

    private static (string op, string rest) SplitOperator(string val)
    {
        if (val.StartsWith(">="))
        {
            return (">=", val[2..]);
        }

        if (val.StartsWith("<="))
        {
            return ("<=", val[2..]);
        }

        if (val.StartsWith(">"))
        {
            return (">", val[1..]);
        }

        if (val.StartsWith("<"))
        {
            return ("<", val[1..]);
        }

        if (val.StartsWith("="))
        {
            return ("=", val[1..]);
        }

        return ("=", val);
    }

    private static long? ParseSizeToBytes(string val)
    {
        val = val.Trim().ToLowerInvariant();
        int idx = 0;
        while (idx < val.Length && (char.IsDigit(val[idx]) || val[idx] == '.'))
        {
            idx++;
        }

        if (idx == 0)
        {
            return null;
        }

        string numPart = val[..idx];
        string unitPart = val[idx..].Trim();

        if (!double.TryParse(numPart, out double num))
        {
            return null;
        }

        double multiplier = unitPart switch
        {
            "kb" or "k" => 1024.0,
            "mb" or "m" => 1024.0 * 1024.0,
            "gb" or "g" => 1024.0 * 1024.0 * 1024.0,
            _ => 1.0
        };

        return (long)(num * multiplier);
    }

    private static string GetOrderByClause(SortBy sortBy, SortDirection direction)
    {
        string col = sortBy switch
        {
            SortBy.Name => "F.Name",
            SortBy.DateAdded => "F.DateAdded",
            _ => "F.Path"
        };

        string dir = direction == SortDirection.Desc ? "DESC" : "ASC";
        return $"ORDER BY {col} {dir}, F.Path ASC";
    }
}