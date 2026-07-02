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

        StringBuilder query = new();
        _ = query.AppendLine("SELECT DISTINCT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing");
        _ = query.AppendLine("FROM Files F");
        _ = query.AppendLine($"WHERE EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '{Path.DirectorySeparatorChar}'))");

        BuildIncludeClauses(includeTerms, cmd, query);
        BuildExcludeClauses(excludeTerms, cmd, query);

        _ = query.AppendLine(GetOrderByClause(sortBy, sortDirection));

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
                IsMissing = reader.GetBoolean(5)
            });
        }

        return files;
    }

    private static void BuildIncludeClauses(List<string> terms, SqliteCommand cmd, StringBuilder query)
    {
        for (int i = 0; i < terms.Count; i++)
        {
            string paramName = $"@in{i}";
            string termPattern = $"%{terms[i]}%";
            _ = cmd.Parameters.AddWithValue(paramName, termPattern);

            string tagSubQuery = $@"
                EXISTS (
                    SELECT 1 FROM FileTags FT 
                    JOIN Tags T ON FT.TagId = T.Id 
                    WHERE FT.FileId = F.Id AND T.Name LIKE {paramName}
                )";

            _ = query.AppendLine($"AND (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {tagSubQuery})");
        }
    }

    private static void BuildExcludeClauses(List<string> terms, SqliteCommand cmd, StringBuilder query)
    {
        for (int i = 0; i < terms.Count; i++)
        {
            string paramName = $"@ex{i}";
            string termPattern = $"%{terms[i]}%";
            _ = cmd.Parameters.AddWithValue(paramName, termPattern);

            string tagSubQuery = $@"
                EXISTS (
                    SELECT 1 FROM FileTags FT 
                    JOIN Tags T ON FT.TagId = T.Id 
                    WHERE FT.FileId = F.Id AND T.Name LIKE {paramName}
                )";

            _ = query.AppendLine($"AND NOT (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {tagSubQuery})");
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
        return $"ORDER BY {col} {dir}, F.Path ASC";
    }
}