using Microsoft.Data.Sqlite;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class FileSearchRepository
{
    private readonly DatabaseManager _db;
    private static readonly HashSet<string> _searchStopWords = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not" };

    public FileSearchRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<FileEntry> Search(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
        string[] tokens = searchText.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<string> includedTags = tokens.Where(t => !t.StartsWith("-") && !_searchStopWords.Contains(t)).Distinct().ToList();
        List<string> excludedTags = tokens.Where(t => t.StartsWith("-") && t.Length > 1).Select(t => t[1..]).Distinct().ToList();

        if (!includedTags.Any() && !excludedTags.Any())
        {
            return [];
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();

        string orderBy = GetOrderByClause(sortBy, sortDirection);
        string query;

        // Dynamic SQL construction is necessary for variable tag counts.
        // Parameters are safely parameterized.

        if (includedTags.Any() && excludedTags.Any())
        {
            AddParams(cmd, "@in", includedTags);
            AddParams(cmd, "@ex", excludedTags);

            string inParams = string.Join(",", includedTags.Select((_, i) => $"@in{i}"));
            string exParams = string.Join(",", excludedTags.Select((_, i) => $"@ex{i}"));

            query = $@"
                SELECT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing FROM Files F 
                WHERE F.Id IN (
                    SELECT FT.FileId FROM FileTags FT
                    INNER JOIN Tags T ON FT.TagId = T.Id
                    WHERE T.Name IN ({inParams})
                    GROUP BY FT.FileId
                    HAVING COUNT(DISTINCT T.Id) = {includedTags.Count}
                ) 
                AND F.Id NOT IN (
                    SELECT FT.FileId FROM FileTags FT
                    INNER JOIN Tags T ON FT.TagId = T.Id
                    WHERE T.Name IN ({exParams})
                )
                {orderBy}";
        }
        else if (includedTags.Any())
        {
            AddParams(cmd, "@tag", includedTags);
            string tagParams = string.Join(",", includedTags.Select((_, i) => $"@tag{i}"));

            query = $@"
                SELECT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing
                FROM Files F
                INNER JOIN FileTags FT ON F.Id = FT.FileId
                INNER JOIN Tags T ON FT.TagId = T.Id
                WHERE T.Name IN ({tagParams})
                GROUP BY F.Id
                HAVING COUNT(DISTINCT T.Id) = {includedTags.Count}
                {orderBy}";
        }
        else // Only excluded tags
        {
            AddParams(cmd, "@tag", excludedTags);
            string tagParams = string.Join(",", excludedTags.Select((_, i) => $"@tag{i}"));

            query = $@"
                SELECT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing
                FROM Files F
                WHERE F.Id NOT IN (
                    SELECT FT.FileId FROM FileTags FT
                    INNER JOIN Tags T ON FT.TagId = T.Id
                    WHERE T.Name IN ({tagParams})
                )
                {orderBy}";
        }

        cmd.CommandText = query;
        List<FileEntry> files = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(new FileEntry
            {
                Id = reader.GetInt64(0),
                Path = reader.GetString(1),
                Name = reader.GetString(2),
                Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
                DateAdded = reader.GetInt64(4),
                IsMissing = reader.GetBoolean(5)
            });
        }
        return files;
    }

    private static void AddParams(SqliteCommand cmd, string prefix, List<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            _ = cmd.Parameters.AddWithValue($"{prefix}{i}", values[i]);
        }
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
        // Secondary sort by path for stability
        return $"ORDER BY {col} {dir}, F.Path ASC";
    }
}