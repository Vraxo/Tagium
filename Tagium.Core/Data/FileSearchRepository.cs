using Microsoft.Data.Sqlite;
using System.Text;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class FileSearchRepository
{
    private readonly DatabaseManager _db;

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

        // Split tokens and separate into positive (include) and negative (exclude) terms
        string[] tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<string> includeTerms = [];
        List<string> excludeTerms = [];

        foreach (string token in tokens)
        {
            if (token.StartsWith('-') && token.Length > 1)
            {
                excludeTerms.Add(token[1..]);
            }
            else
            {
                includeTerms.Add(token);
            }
        }

        if (includeTerms.Count == 0 && excludeTerms.Count == 0)
        {
            return [];
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();

        // Dynamic SQL Construction
        // We want files that match ALL include terms (AND) and match NO exclude terms (NOT OR).
        // A match is defined as: Name matches OR Path matches OR Any Tag Name matches.

        StringBuilder query = new();
        _ = query.AppendLine("SELECT DISTINCT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing");
        _ = query.AppendLine("FROM Files F");
        _ = query.AppendLine("WHERE 1=1");

        // 1. Process Includes (AND logic)
        for (int i = 0; i < includeTerms.Count; i++)
        {
            string paramName = $"@in{i}";
            string termPattern = $"%{includeTerms[i]}%";

            _ = cmd.Parameters.AddWithValue(paramName, termPattern);

            // "Exists a tag matching term" subquery
            string tagSubQuery = $@"
                EXISTS (
                    SELECT 1 FROM FileTags FT 
                    JOIN Tags T ON FT.TagId = T.Id 
                    WHERE FT.FileId = F.Id AND T.Name LIKE {paramName}
                )";

            _ = query.AppendLine($"AND (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {tagSubQuery})");
        }

        // 2. Process Excludes (NOT logic)
        for (int i = 0; i < excludeTerms.Count; i++)
        {
            string paramName = $"@ex{i}";
            string termPattern = $"%{excludeTerms[i]}%";

            _ = cmd.Parameters.AddWithValue(paramName, termPattern);

            string tagSubQuery = $@"
                EXISTS (
                    SELECT 1 FROM FileTags FT 
                    JOIN Tags T ON FT.TagId = T.Id 
                    WHERE FT.FileId = F.Id AND T.Name LIKE {paramName}
                )";

            _ = query.AppendLine($"AND NOT (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {tagSubQuery})");
        }

        // 3. Sorting
        _ = query.AppendLine(GetOrderByClause(sortBy, sortDirection));

        cmd.CommandText = query.ToString();

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