using Microsoft.Data.Sqlite;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public enum AddFileResult { Success, Duplicate, Error }

public class FileRepository
{
    private readonly DatabaseManager _db;

    public FileRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<FileEntry> GetAll(SortBy sortBy, SortDirection direction)
    {
        List<FileEntry> files = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        string orderBy = GetOrderByClause(sortBy, direction);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files {orderBy}";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(ReadFile(reader));
        }
        return files;
    }

    public FileEntry? GetById(long id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public FileEntry? GetByHash(string hash)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files WHERE Hash = @Hash";
        _ = cmd.Parameters.AddWithValue("@Hash", hash);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public AddFileResult Add(string path, string hash)
    {
        if (GetByHash(hash) != null)
        {
            return AddFileResult.Duplicate;
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        // Check if path exists (edge case where hash wasn't caught but path is same)
        using (SqliteCommand checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(1) FROM Files WHERE Path = @Path";
            _ = checkCmd.Parameters.AddWithValue("@Path", path);
            if (Convert.ToInt64(checkCmd.ExecuteScalar()) > 0)
            {
                // Update hash if it changed? For now, treat as duplicate/exists.
                return AddFileResult.Duplicate;
            }
        }

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Files (Path, Name, Hash, DateAdded) 
            VALUES (@Path, @Name, @Hash, strftime('%s','now'))";

        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.Parameters.AddWithValue("@Name", Path.GetFileName(path));
        _ = cmd.Parameters.AddWithValue("@Hash", hash);

        try
        {
            _ = cmd.ExecuteNonQuery();
            return AddFileResult.Success;
        }
        catch
        {
            return AddFileResult.Error;
        }
    }

    public void Remove(long id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id);
        _ = cmd.ExecuteNonQuery();
    }

    private static FileEntry ReadFile(SqliteDataReader reader)
    {
        return new FileEntry
        {
            Id = reader.GetInt64(0),
            Path = reader.GetString(1),
            Name = reader.GetString(2),
            Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
            DateAdded = reader.GetInt64(4),
            IsMissing = reader.GetBoolean(5)
        };
    }

    private static string GetOrderByClause(SortBy sortBy, SortDirection direction)
    {
        string col = sortBy switch
        {
            SortBy.Name => "Name",
            SortBy.DateAdded => "DateAdded",
            _ => "Path"
        };
        string dir = direction == SortDirection.Desc ? "DESC" : "ASC";
        return $"ORDER BY {col} {dir}";
    }
}