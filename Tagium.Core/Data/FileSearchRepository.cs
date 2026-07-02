using Microsoft.Data.Sqlite;
using System.Text;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class FileSearchRepository
{
    private readonly DatabaseManager _db;

    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "artist", "character", "series", "location", "person", "franchise", "species", "year", "meta"
    };

    public FileSearchRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<FileEntry> Search(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
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
                    (string? op, string? rStr) = SplitOperator(val);
                    if (int.TryParse(rStr, out int rVal))
                    {
                        string paramName = $"@p{paramCounter++}";
                        _ = cmd.Parameters.AddWithValue(paramName, rVal);

                        if (isNegated)
                        {
                            query.AppendLine($"AND (F.Rating IS NULL OR NOT (F.Rating {op} {paramName}))");
                        }
                        else
                        {
                            query.AppendLine($"AND (F.Rating IS NOT NULL AND F.Rating {op} {paramName})");
                        }
                    }
                }
                else if (string.Equals(key, "size", StringComparison.OrdinalIgnoreCase))
                {
                    (string? op, string? sStr) = SplitOperator(val);
                    long? bytes = ParseSizeToBytes(sStr);
                    if (bytes.HasValue)
                    {
                        string paramName = $"@p{paramCounter++}";
                        _ = cmd.Parameters.AddWithValue(paramName, bytes.Value);

                        if (isNegated)
                        {
                            query.AppendLine($"AND NOT (F.Size {op} {paramName})");
                        }
                        else
                        {
                            query.AppendLine($"AND F.Size {op} {paramName}");
                        }
                    }
                }
                else if (string.Equals(key, "ext", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "extension", StringComparison.OrdinalIgnoreCase))
                {
                    string extPattern = "%." + val.TrimStart('.').ToLowerInvariant();
                    string paramName = $"@p{paramCounter++}";
                    _ = cmd.Parameters.AddWithValue(paramName, extPattern);

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
                    string searchPattern = "%" + val + "%";
                    string catParam = $"@p{paramCounter++}";
                    string valParam = $"@p{paramCounter++}";

                    _ = cmd.Parameters.AddWithValue(catParam, key);
                    _ = cmd.Parameters.AddWithValue(valParam, searchPattern);

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

        return files;
    }

    private static void BuildTagSearchClause(string token, bool isNegated, SqliteCommand cmd, StringBuilder query, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        string termPattern = $"%{token}%";
        _ = cmd.Parameters.AddWithValue(paramName, termPattern);

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
        int lastDigitIdx = -1;
        for (int i = 0; i < val.Length; i++)
        {
            if (char.IsDigit(val[i]) || val[i] == '.')
            {
                lastDigitIdx = i;
            }
            else
            {
                break;
            }
        }

        if (lastDigitIdx == -1)
        {
            return null;
        }

        string numPart = val[..(lastDigitIdx + 1)];
        string unitPart = val[(lastDigitIdx + 1)..].Trim();

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