using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class FileSearchRepository
{
    private readonly DatabaseManager _db;
    private readonly ILogger<FileSearchRepository> _logger;

    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "artist", "character", "series", "location", "person", "franchise", "species", "year", "meta"
    };

    public FileSearchRepository(DatabaseManager db, ILogger<FileSearchRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public List<FileEntry> Search(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
        using LogTiming timer = LogTiming.StartDebug(_logger, "Search compilation and execution for query: {Query}", searchText);

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        string[] tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();

        StringBuilder query = new();
        query.AppendLine("SELECT DISTINCT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime, F.Rating, F.Title, F.Description");
        query.AppendLine("FROM cache.Files F");
        query.AppendLine("WHERE EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '" + Path.DirectorySeparatorChar + "'))");

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
                    BuildRatingClause(val, isNegated, cmd, query, ref paramCounter);
                }
                else if (string.Equals(key, "size", StringComparison.OrdinalIgnoreCase))
                {
                    BuildSizeClause(val, isNegated, cmd, query, ref paramCounter);
                }
                else if (string.Equals(key, "ext", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "extension", StringComparison.OrdinalIgnoreCase))
                {
                    BuildExtensionClause(val, isNegated, cmd, query, ref paramCounter);
                }
                else if (ValidCategories.Contains(key))
                {
                    BuildCategoryClause(key, val, isNegated, cmd, query, ref paramCounter);
                }
                else
                {
                    BuildTagSearchClause(rawToken, isNegated, cmd, query, ref paramCounter);
                }
            }
            else
            {
                BuildTagSearchClause(rawToken, isNegated, cmd, query, ref paramCounter);
            }
        }

        query.AppendLine(GetOrderByClause(sortBy, sortDirection));
        cmd.CommandText = query.ToString();

        _logger.LogDebug("Compiled SQLite Search Query: {Sql}", cmd.CommandText);

        List<FileEntry> files = [];
        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            files.Add(new FileEntry
            {
                Id = Guid.Parse(reader.GetString(0)),
                Path = reader.GetString(1),
                Name = reader.GetString(2),
                Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
                DateAdded = reader.GetInt64(4),
                IsMissing = reader.GetBoolean(5),
                Size = reader.GetInt64(6),
                ModifiedTime = reader.GetInt64(7),
                Rating = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Title = reader.IsDBNull(9) ? null : reader.GetString(9),
                Description = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        _logger.LogInformation("Search query returned {Count} matching files", files.Count);
        return files;
    }

    private void BuildRatingClause(string val, bool isNegated, SqliteCommand cmd, StringBuilder query, ref int paramCounter)
    {
        (string op, string rStr) = SplitOperator(val);
        if (int.TryParse(rStr, out int rVal))
        {
            string paramName = $"@p{paramCounter++}";
            cmd.Parameters.AddWithValue(paramName, rVal);

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

    private void BuildSizeClause(string val, bool isNegated, SqliteCommand cmd, StringBuilder query, ref int paramCounter)
    {
        (string op, string sStr) = SplitOperator(val);
        long? bytes = ParseSizeToBytes(sStr);
        if (bytes.HasValue)
        {
            string paramName = $"@p{paramCounter++}";
            cmd.Parameters.AddWithValue(paramName, bytes.Value);

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

    private void BuildExtensionClause(string val, bool isNegated, SqliteCommand cmd, StringBuilder query, ref int paramCounter)
    {
        string extPattern = "%." + val.TrimStart('.').ToLowerInvariant();
        string paramName = $"@p{paramCounter++}";
        cmd.Parameters.AddWithValue(paramName, extPattern);

        if (isNegated)
        {
            query.AppendLine($"AND F.Path NOT LIKE {paramName}");
        }
        else
        {
            query.AppendLine($"AND F.Path LIKE {paramName}");
        }
    }

    private void BuildCategoryClause(string key, string val, bool isNegated, SqliteCommand cmd, StringBuilder query, ref int paramCounter)
    {
        string searchPattern = "%" + val + "%";
        string catParam = $"@p{paramCounter++}";
        string valParam = $"@p{paramCounter++}";

        cmd.Parameters.AddWithValue(catParam, key);
        cmd.Parameters.AddWithValue(valParam, searchPattern);

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

    private void BuildTagSearchClause(string token, bool isNegated, SqliteCommand cmd, StringBuilder query, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        string termPattern = $"%{token}%";
        cmd.Parameters.AddWithValue(paramName, termPattern);

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