using Microsoft.Data.Sqlite;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

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
        cmd.CommandText = $@"
            SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime FROM Files F
            WHERE EXISTS (
                SELECT 1 FROM LibraryRoots LR 
                WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '{Path.DirectorySeparatorChar}')
            )
            {orderBy}";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(ReadFile(reader));
        }
        return files;
    }

    public List<FileEntry> GetFilesWithTags(SortBy sortBy, SortDirection direction, bool includeMissing = false)
    {
        List<FileEntry> files = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        string orderBy = GetOrderByClause(sortBy, direction);
        string rootFilter = $"EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '{Path.DirectorySeparatorChar}'))";
        string missingFilter = includeMissing ? $"WHERE {rootFilter}" : $"WHERE {rootFilter} AND F.IsMissing = 0";

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT 
                F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime,
                GROUP_CONCAT(T.Id || ':' || T.Name || ':' || T.ColorHex || ':' || IFNULL(T.Emoji, ''), ';;') AS TagData
            FROM Files F
            LEFT JOIN FileTags FT ON F.Id = FT.FileId
            LEFT JOIN Tags T ON FT.TagId = T.Id
            {missingFilter}
            GROUP BY F.Id
            {orderBy}";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            FileEntry file = ReadFile(reader);

            if (!reader.IsDBNull(8))
            {
                string tagData = reader.GetString(8);
                file.Tags = ParseTagData(tagData);
            }

            files.Add(file);
        }
        return files;
    }

    public FileEntry? GetFileWithTagsById(Guid id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime,
                GROUP_CONCAT(T.Id || ':' || T.Name || ':' || T.ColorHex || ':' || IFNULL(T.Emoji, ''), ';;') AS TagData
            FROM Files F
            LEFT JOIN FileTags FT ON F.Id = FT.FileId
            LEFT JOIN Tags T ON FT.TagId = T.Id
            WHERE F.Id = @Id
            GROUP BY F.Id";

        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            FileEntry file = ReadFile(reader);
            if (!reader.IsDBNull(8))
            {
                string tagData = reader.GetString(8);
                file.Tags = ParseTagData(tagData);
            }
            return file;
        }
        return null;
    }

    public FileEntry? GetById(Guid id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime FROM Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public FileEntry? GetByHash(string hash)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime FROM Files WHERE Hash = @Hash";
        _ = cmd.Parameters.AddWithValue("@Hash", hash);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public List<FileEntry> GetFilesUnderPath(string rootPath)
    {
        List<FileEntry> files = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime FROM Files WHERE Path = @Path OR SUBSTR(Path, 1, LENGTH(@Path) + 1) = @Path || '{Path.DirectorySeparatorChar}'";
        _ = cmd.Parameters.AddWithValue("@Path", rootPath);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(ReadFile(reader));
        }
        return files;
    }

    public bool Add(string path, string hash, long size, long modifiedTime)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Files (Id, Path, Name, Hash, DateAdded, Size, ModifiedTime) 
            VALUES (@Id, @Path, @Name, @Hash, strftime('%s','now'), @Size, @ModifiedTime)";

        Guid newId = Guid.NewGuid();
        _ = cmd.Parameters.AddWithValue("@Id", newId.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.Parameters.AddWithValue("@Name", Path.GetFileName(path));
        _ = cmd.Parameters.AddWithValue("@Hash", hash);
        _ = cmd.Parameters.AddWithValue("@Size", size);
        _ = cmd.Parameters.AddWithValue("@ModifiedTime", modifiedTime);

        try
        {
            _ = cmd.ExecuteNonQuery();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Restore(FileEntry entry)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Files (Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime) 
            VALUES (@Id, @Path, @Name, @Hash, @DateAdded, @IsMissing, @Size, @ModifiedTime)";

        _ = cmd.Parameters.AddWithValue("@Id", entry.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", entry.Path);
        _ = cmd.Parameters.AddWithValue("@Name", entry.Name);
        _ = cmd.Parameters.AddWithValue("@Hash", (object?)entry.Hash ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@DateAdded", entry.DateAdded);
        _ = cmd.Parameters.AddWithValue("@IsMissing", entry.IsMissing ? 1 : 0);
        _ = cmd.Parameters.AddWithValue("@Size", entry.Size);
        _ = cmd.Parameters.AddWithValue("@ModifiedTime", entry.ModifiedTime);

        _ = cmd.ExecuteNonQuery();
    }

    public void Update(FileEntry file)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Files 
            SET Path = @Path, Name = @Name, Hash = @Hash, IsMissing = @IsMissing, Size = @Size, ModifiedTime = @ModifiedTime 
            WHERE Id = @Id";

        _ = cmd.Parameters.AddWithValue("@Id", file.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", file.Path);
        _ = cmd.Parameters.AddWithValue("@Name", file.Name);
        _ = cmd.Parameters.AddWithValue("@Hash", (object?)file.Hash ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@IsMissing", file.IsMissing ? 1 : 0);
        _ = cmd.Parameters.AddWithValue("@Size", file.Size);
        _ = cmd.Parameters.AddWithValue("@ModifiedTime", file.ModifiedTime);

        _ = cmd.ExecuteNonQuery();
    }

    public void Remove(Guid id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public void SetMissingStatus(IEnumerable<Guid> fileIds, bool isMissing)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteTransaction trans = conn.BeginTransaction();

        foreach (Guid id in fileIds)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = trans;
            cmd.CommandText = "UPDATE Files SET IsMissing = @IsMissing WHERE Id = @Id";
            _ = cmd.Parameters.AddWithValue("@IsMissing", isMissing ? 1 : 0);
            _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
            _ = cmd.ExecuteNonQuery();
        }
        trans.Commit();
    }

    public void RemoveMissingFiles()
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Files WHERE IsMissing = 1";
        _ = cmd.ExecuteNonQuery();
    }

    private static FileEntry ReadFile(SqliteDataReader reader)
    {
        return new FileEntry
        {
            Id = Guid.Parse(reader.GetString(0)),
            Path = reader.GetString(1),
            Name = reader.GetString(2),
            Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
            DateAdded = reader.GetInt64(4),
            IsMissing = reader.GetBoolean(5),
            Size = reader.GetInt64(6),
            ModifiedTime = reader.GetInt64(7),
            Tags = []
        };
    }

    private List<Tag> ParseTagData(string tagData)
    {
        return string.IsNullOrEmpty(tagData)
            ? []
            : tagData.Split(";;", StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTagFromData)
            .Where(t => t != null)
            .Cast<Tag>()
            .ToList();
    }

    private Tag? ParseTagFromData(string tagData)
    {
        string[] parts = tagData.Split(':', 4);
        return parts.Length < 3 || !Guid.TryParse(parts[0], out Guid tagId)
            ? null
            : new Tag
            {
                Id = tagId,
                Name = parts[1],
                ColorHex = parts[2],
                Emoji = parts.Length > 3 && parts[3] != "" ? parts[3] : null
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