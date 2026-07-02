### `Tagium.Core\Tagium.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FFMpegCore" Version="5.4.0" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.9" />
    <PackageReference Include="SkiaSharp" Version="4.148.0" />
  </ItemGroup>

</Project>
```

---

### `Tagium.Core\Data\DatabaseManager.cs`

```csharp
using Microsoft.Data.Sqlite;
using Tagium.Core.Helpers;

namespace Tagium.Core.Data;

public class DatabaseManager
{
    public string ConnectionString { get; }

    public DatabaseManager()
    {
        string dbPath = AppDataHelper.GetDatabasePath();
        ConnectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using SqliteConnection connection = new(ConnectionString);
        connection.Open();

        using (SqliteCommand walCmd = connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            _ = walCmd.ExecuteNonQuery();
        }

        InitializeSchema(connection);
        MigrateSchema(connection);
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        string schema = @"
            CREATE TABLE IF NOT EXISTS Tags (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                ColorHex TEXT NOT NULL DEFAULT '#757575',
                Emoji TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Files (
                Id TEXT PRIMARY KEY,
                Path TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL DEFAULT '',
                Hash TEXT NULL,
                DateAdded INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
                IsMissing INTEGER NOT NULL DEFAULT 0,
                Size INTEGER NOT NULL DEFAULT 0,
                ModifiedTime INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS FileTags (
                FileId TEXT NOT NULL,
                TagId TEXT NOT NULL,
                FOREIGN KEY(FileId) REFERENCES Files(Id) ON DELETE CASCADE,
                FOREIGN KEY(TagId) REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY (FileId, TagId)
            );

            CREATE TABLE IF NOT EXISTS LibraryRoots (
                Id TEXT PRIMARY KEY,
                Path TEXT NOT NULL UNIQUE,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                LastScanTime INTEGER NULL
            );
        ";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = schema;
        _ = command.ExecuteNonQuery();
    }

    private static void MigrateSchema(SqliteConnection connection)
    {
        string[] columns = ["Size", "ModifiedTime"];
        foreach (string col in columns)
        {
            try
            {
                using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE Files ADD COLUMN {col} INTEGER NOT NULL DEFAULT 0;";
                _ = cmd.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }
}
```

---

### `Tagium.Core\Data\FileRepository.cs`

```csharp
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
```

---

### `Tagium.Core\Data\FileSearchRepository.cs`

```csharp
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
        _ = query.AppendLine("SELECT DISTINCT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime");
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
                IsMissing = reader.GetBoolean(5),
                Size = reader.GetInt64(6),
                ModifiedTime = reader.GetInt64(7)
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
```

---

### `Tagium.Core\Data\FileTagRepository.cs`

```csharp
using Microsoft.Data.Sqlite;

namespace Tagium.Core.Data;

public class FileTagRepository
{
    private readonly DatabaseManager _db;

    public FileTagRepository(DatabaseManager db)
    {
        _db = db;
    }

    public void AddTagToFile(Guid fileId, Guid tagId)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO FileTags (FileId, TagId) VALUES (@FileId, @TagId)";
        cmd.Parameters.AddWithValue("@FileId", fileId.ToString("N"));
        cmd.Parameters.AddWithValue("@TagId", tagId.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public void RemoveTagFromFile(Guid fileId, Guid tagId)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM FileTags WHERE FileId = @FileId AND TagId = @TagId";
        cmd.Parameters.AddWithValue("@FileId", fileId.ToString("N"));
        cmd.Parameters.AddWithValue("@TagId", tagId.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public List<Guid> GetTagIdsForFile(Guid fileId)
    {
        List<Guid> ids = [];

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TagId FROM FileTags WHERE FileId = @FileId";
        cmd.Parameters.AddWithValue("@FileId", fileId.ToString("N"));

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }
}
```

---

### `Tagium.Core\Data\LibraryRootRepository.cs`

```csharp
using Microsoft.Data.Sqlite;
using Tagium.Core.Models;

namespace Tagium.Core.Data;

public class LibraryRootRepository
{
    private readonly DatabaseManager _db;

    public LibraryRootRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<LibraryRoot> GetAll()
    {
        List<LibraryRoot> roots = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, IsEnabled, LastScanTime FROM LibraryRoots ORDER BY Path ASC";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            roots.Add(ReadRoot(reader));
        }
        return roots;
    }

    public void Add(string path)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO LibraryRoots (Id, Path, IsEnabled, LastScanTime) VALUES (@Id, @Path, 1, NULL)";
        _ = cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.ExecuteNonQuery();
    }

    public void Update(LibraryRoot root)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE LibraryRoots SET IsEnabled = @IsEnabled, LastScanTime = @LastScanTime WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", root.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@IsEnabled", root.IsEnabled ? 1 : 0);
        _ = cmd.Parameters.AddWithValue("@LastScanTime", (object?)root.LastScanTime ?? DBNull.Value);
        _ = cmd.ExecuteNonQuery();
    }

    public void Delete(Guid id, string path)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteTransaction trans = conn.BeginTransaction();

        try
        {
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = "DELETE FROM LibraryRoots WHERE Id = @Id";
                _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
                _ = cmd.ExecuteNonQuery();
            }

            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = $"DELETE FROM Files WHERE Path = @Path OR SUBSTR(Path, 1, LENGTH(@Path) + 1) = @Path || '{Path.DirectorySeparatorChar}'";
                _ = cmd.Parameters.AddWithValue("@Path", path);
                _ = cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }

    private static LibraryRoot ReadRoot(SqliteDataReader reader)
    {
        return new LibraryRoot
        {
            Id = Guid.Parse(reader.GetString(0)),
            Path = reader.GetString(1),
            IsEnabled = reader.GetBoolean(2),
            LastScanTime = reader.IsDBNull(3) ? null : reader.GetInt64(3)
        };
    }
}
```

---

### `Tagium.Core\Data\TagRepository.cs`

```csharp
using Microsoft.Data.Sqlite;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class TagRepository
{
    private readonly DatabaseManager _db;

    public TagRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<Tag> GetAll(TagSortBy sortBy = TagSortBy.Name, SortDirection direction = SortDirection.Asc)
    {
        List<Tag> tags = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        string orderBy = GetOrderByClause(sortBy, direction);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount
            FROM Tags T
            LEFT JOIN FileTags FT ON T.Id = FT.TagId
            GROUP BY T.Id
            {orderBy}";

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            tags.Add(ReadTag(reader));
        }

        return tags;
    }

    private static string GetOrderByClause(TagSortBy sortBy, SortDirection direction)
    {
        string col = sortBy switch
        {
            TagSortBy.FileCount => "FileCount",
            _ => "T.Name"
        };

        string dir = direction == SortDirection.Desc ? "DESC" : "ASC";
        return $"ORDER BY {col} {dir}, T.Name ASC";
    }

    public List<Tag> GetTagsByIds(IEnumerable<Guid> ids)
    {
        List<Guid> idList = ids.ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        List<Tag> tags = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        // SQLite can handle 'guid'|'guid'|... syntax for IN clause with TEXT
        cmd.CommandText = @"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount
            FROM Tags T
            LEFT JOIN FileTags FT ON T.Id = FT.TagId
            WHERE T.Id IN (" + string.Join(",", idList.Select(id => $"'{id:N}'")) + @")
            GROUP BY T.Id";

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            tags.Add(ReadTag(reader));
        }

        return tags;
    }

    public void Add(string name, string colorHex, string? emoji)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Tags (Id, Name, ColorHex, Emoji) VALUES (@Id, @Name, @Color, @Emoji)";
        _ = cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Name", name);
        _ = cmd.Parameters.AddWithValue("@Color", colorHex);
        _ = cmd.Parameters.AddWithValue("@Emoji", (object?)emoji ?? DBNull.Value);

        try { _ = cmd.ExecuteNonQuery(); } catch { /* Ignore duplicates */ }
    }

    public void Update(Tag tag)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Tags 
            SET Name = @Name, ColorHex = @Color, Emoji = @Emoji 
            WHERE Id = @Id";

        _ = cmd.Parameters.AddWithValue("@Id", tag.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Name", tag.Name);
        _ = cmd.Parameters.AddWithValue("@Color", tag.ColorHex);
        _ = cmd.Parameters.AddWithValue("@Emoji", (object?)tag.Emoji ?? DBNull.Value);

        try { _ = cmd.ExecuteNonQuery(); } catch { /* Ignore name collision */ }
    }

    public void Delete(Guid id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tags WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    private static Tag ReadTag(SqliteDataReader reader)
    {
        return new Tag
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            ColorHex = reader.GetString(2),
            Emoji = reader.IsDBNull(3) ? null : reader.GetString(3),
            FileCount = reader.GetInt32(4)
        };
    }
}
```

---

### `Tagium.Core\Helpers\AppDataHelpers.cs`

```csharp
namespace Tagium.Core.Helpers;

public static class AppDataHelper
{
    private const string AppName = "Tagium";

    public static string GetBaseDirectory()
    {
        // Using LocalApplicationData to be distinct from the old "Tagra" app
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string path = Path.Combine(appData, AppName);
        if (!Directory.Exists(path))
        {
            _ = Directory.CreateDirectory(path);
        }
        return path;
    }

    public static string GetDatabasePath()
    {
        return Path.Combine(GetBaseDirectory(), "tagium.db");
    }

    public static string GetThumbnailCacheDirectory()
    {
        string path = Path.Combine(GetBaseDirectory(), "Thumbnails");

        if (!Directory.Exists(path))
        {
            _ = Directory.CreateDirectory(path);
        }

        return path;
    }
}
```

---

### `Tagium.Core\Helpers\FileHasher.cs`

```csharp
using System.Security.Cryptography;

namespace Tagium.Core.Helpers;

public static class FileHasher
{
    public static async Task<string?> ComputeSha256Async(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(filePath);
            long length = stream.Length;

            if (length <= 256 * 1024)
            {
                using SHA256 sha256 = SHA256.Create();
                byte[] hash = await sha256.ComputeHashAsync(stream);
                return Convert.ToHexStringLower(hash);
            }

            byte[] buffer = new byte[256 * 1024];

            int bytesReadStart = await stream.ReadAsync(buffer.AsMemory(0, 128 * 1024));

            stream.Seek(-128 * 1024, SeekOrigin.End);
            int bytesReadEnd = await stream.ReadAsync(buffer.AsMemory(128 * 1024, 128 * 1024));

            using SHA256 sha256Combined = SHA256.Create();
            byte[] hashBytes = sha256Combined.ComputeHash(buffer, 0, bytesReadStart + bytesReadEnd);

            return $"{length:X}-{Convert.ToHexStringLower(hashBytes)}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileHasher] Error: {ex.Message}");
            return null;
        }
    }
}
```

---

### `Tagium.Core\Helpers\TagFormatter.cs`

```csharp
using System.Text.RegularExpressions;

namespace Tagium.Core.Helpers;

public static class TagFormatter
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string s = input.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[\s\-_\+\.]+", "_");
        s = Regex.Replace(s, @"[^a-z0-9_]", "");
        s = Regex.Replace(s, @"_+", "_");
        return s.Trim('_');
    }
}
```

---

### `Tagium.Core\Models\FileEntry.cs`

```csharp
namespace Tagium.Core.Models;

public class FileEntry
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public long Size { get; set; }
    public long ModifiedTime { get; set; }
    public List<Tag> Tags { get; set; } = [];
}
```

---

### `Tagium.Core\Models\LibraryRoot.cs`

```csharp
namespace Tagium.Core.Models;

public class LibraryRoot
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public bool IsEnabled { get; set; } = true;
    public long? LastScanTime { get; set; }
}
```

---

### `Tagium.Core\Models\MediaMetadata.cs`

```csharp
namespace Tagium.Photino.Blazor.Components.Modals;

public class MediaMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Rating { get; set; }
    public List<string> Tags { get; set; } = [];
}
```

---

### `Tagium.Core\Models\Tag.cs`

```csharp
namespace Tagium.Core.Models;

public class Tag
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string ColorHex { get; set; } = "#757575";
    public string? Emoji { get; set; }
    public int FileCount { get; set; }

    public override string ToString()
    {
        return Name;
    }
}
```

---

### `Tagium.Core\Services\ExifToolLocator.cs`

```csharp
```

---

### `Tagium.Core\Services\ExifToolService.cs`

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tagium.Photino.Blazor.Components.Modals;

namespace Tagium.Core.Services;

public class ExifToolService : IDisposable
{
    private readonly string _commandName;
    private Process? _exifToolProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly object _lock = new();
    private bool _isDisposed;

    public bool IsEnabled { get; private set; }

    public ExifToolService()
    {
        _commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "exiftool.exe" : "exiftool";
        InitializeProcess();
    }

    private void InitializeProcess()
    {
        try
        {
            ProcessStartInfo startInfo = new(_commandName)
            {
                Arguments = "-stay_open True -@ -",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            _exifToolProcess = Process.Start(startInfo);
            if (_exifToolProcess is not null)
            {
                _stdin = _exifToolProcess.StandardInput;
                _stdout = _exifToolProcess.StandardOutput;

                // Asynchronously drain standard error to prevent OS buffer locks
                _exifToolProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Console.WriteLine($"[ExifTool Warning] {e.Data}");
                    }
                };
                _exifToolProcess.BeginErrorReadLine();

                IsEnabled = !_exifToolProcess.HasExited;
            }
        }
        catch (Exception ex)
        {
            IsEnabled = false;
            Console.WriteLine($"[ExifToolService] WARNING: Failed to initialize persistent daemon: {ex.Message}. Metadata writing and reading will be disabled.");
        }
    }

    public async Task<MediaMetadata?> ReadMetadataAsync(string filePath)
    {
        if (!IsEnabled || _exifToolProcess is null || _exifToolProcess.HasExited)
        {
            return null;
        }

        try
        {
            string output;
            lock (_lock)
            {
                _stdin!.WriteLine("-json");
                _stdin!.WriteLine("-Subject");
                _stdin!.WriteLine("-Keywords");
                _stdin!.WriteLine("-Title");
                _stdin!.WriteLine("-Description");
                _stdin!.WriteLine("-Rating");
                _stdin!.WriteLine(filePath);
                _stdin!.WriteLine("-execute");
                _stdin!.Flush();

                StringBuilder sb = new();
                while (true)
                {
                    string? line = _stdout!.ReadLine();
                    if (line is null || line.StartsWith("{ready}", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    sb.AppendLine(line);
                }
                output = sb.ToString();
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement first = doc.RootElement[0];
            MediaMetadata metadata = new();

            if (first.TryGetProperty("Title", out JsonElement titleProp))
            {
                metadata.Title = titleProp.GetString();
            }

            if (first.TryGetProperty("Description", out JsonElement descProp))
            {
                metadata.Description = descProp.GetString();
            }

            if (first.TryGetProperty("Rating", out JsonElement ratingProp))
            {
                if (ratingProp.ValueKind == JsonValueKind.Number)
                {
                    metadata.Rating = ratingProp.GetInt32();
                }
                else if (ratingProp.ValueKind == JsonValueKind.String && int.TryParse(ratingProp.GetString(), out int r))
                {
                    metadata.Rating = r;
                }
            }

            HashSet<string> uniqueTags = new(StringComparer.OrdinalIgnoreCase);

            if (first.TryGetProperty("Subject", out JsonElement subjProp))
            {
                PopulateTags(subjProp, uniqueTags);
            }

            if (first.TryGetProperty("Keywords", out JsonElement kwProp))
            {
                PopulateTags(kwProp, uniqueTags);
            }

            metadata.Tags = [.. uniqueTags];
            return metadata;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExifTool] Exception reading {filePath}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> WriteMetadataAsync(string filePath, MediaMetadata metadata)
    {
        if (!IsEnabled || _exifToolProcess is null || _exifToolProcess.HasExited)
        {
            return false;
        }

        try
        {
            lock (_lock)
            {
                _stdin!.WriteLine("-overwrite_original");
                _stdin!.WriteLine("-Subject=");
                _stdin!.WriteLine("-Keywords=");

                foreach (string tag in metadata.Tags)
                {
                    _stdin!.WriteLine($"-Subject={tag}");
                    _stdin!.WriteLine($"-Keywords={tag}");
                }

                if (metadata.Title is not null)
                {
                    _stdin!.WriteLine($"-Title={metadata.Title}");
                }
                else
                {
                    _stdin!.WriteLine("-Title=");
                }

                if (metadata.Description is not null)
                {
                    _stdin!.WriteLine($"-Description={metadata.Description}");
                }
                else
                {
                    _stdin!.WriteLine("-Description=");
                }

                if (metadata.Rating.HasValue)
                {
                    _stdin!.WriteLine($"-Rating={metadata.Rating.Value}");
                }
                else
                {
                    _stdin!.WriteLine("-Rating=");
                }

                _stdin!.WriteLine(filePath);
                _stdin!.WriteLine("-execute");
                _stdin!.Flush();

                // Clear stream output until we read the boundary token
                while (true)
                {
                    string? line = _stdout!.ReadLine();
                    if (line is null || line.StartsWith("{ready}", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExifTool] Exception writing {filePath}: {ex.Message}");
            return false;
        }
    }

    private static void PopulateTags(JsonElement element, HashSet<string> tags)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    _ = tags.Add(s.Trim());
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string? s = element.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                _ = tags.Add(s.Trim());
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        if (disposing && _exifToolProcess is not null && !_exifToolProcess.HasExited)
        {
            try
            {
                lock (_lock)
                {
                    _stdin?.WriteLine("-stay_open");
                    _stdin?.WriteLine("False");
                    _stdin?.WriteLine("-execute");
                    _stdin?.Flush();
                }
                _exifToolProcess.WaitForExit(3000);
            }
            catch
            {
                // Suppress shutdown race condition failures
            }
            finally
            {
                _exifToolProcess.Dispose();
            }
        }

        _isDisposed = true;
    }
}
```

---

### `Tagium.Core\Services\FFmpegLocator.cs`

```csharp
namespace Tagium.Core.Services;

internal static class FFmpegLocator
{
    public static string? Find()
    {
        string? ffmpegPath = FindInAppDirectory() ?? FindInPath();

        return ffmpegPath is not null && HasCompanionFfprobe(ffmpegPath)
            ? ffmpegPath : null;
    }

    private static string? FindInAppDirectory()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

        return File.Exists(ffmpeg)
            ? ffmpeg
            : null;
    }

    private static string? FindInPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        string[] paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string dir in paths)
        {
            string candidate = Path.Combine(dir, "ffmpeg.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasCompanionFfprobe(string ffmpegPath)
    {
        string dir = Path.GetDirectoryName(ffmpegPath)!;
        string ffprobe = Path.Combine(dir, "ffprobe.exe");

        return File.Exists(ffprobe);
    }
}
```

---

### `Tagium.Core\Services\LibraryService.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Modals;

namespace Tagium.Core.Services;

public class LibraryService
{
    public bool CanUndo => _undoService.CanUndo;

    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly FileSearchRepository _searchRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SettingsManager _settingsManager;
    private readonly UndoService _undoService;
    private readonly LibraryRootRepository _rootRepo;
    private readonly LibraryScanService _scanService;
    private readonly ExifToolService _exifTool;

    public event Action? LibraryChanged;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager,
        UndoService undoService,
        LibraryRootRepository rootRepo,
        LibraryScanService scanService,
        ExifToolService exifTool)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
        _undoService = undoService;
        _rootRepo = rootRepo;
        _scanService = scanService;
        _exifTool = exifTool;

        _scanService.ScanCompleted += () => LibraryChanged?.Invoke();
        _scanService.FilesImported += () => LibraryChanged?.Invoke();
    }

    public void Refresh()
    {
        LibraryChanged?.Invoke();
    }

    public void Undo()
    {
        _undoService.Undo();
    }

    public List<FileEntry> GetFiles(string? searchText = null, bool missingOnly = false)
    {
        SortBy sort = _settingsManager.Current.FileSortBy;
        SortDirection dir = _settingsManager.Current.FileSortDirection;

        if (missingOnly)
        {
            return [.. _fileRepo.GetFilesWithTags(sort, dir, true)
                .Where(f => f.IsMissing)];
        }
        else
        {
            return string.IsNullOrWhiteSpace(searchText)
                ? _fileRepo.GetFilesWithTags(sort, dir)
                : _searchRepo.Search(searchText, sort, dir);
        }
    }

    public FileEntry? GetFileById(Guid id)
    {
        return _fileRepo.GetById(id);
    }

    public FileEntry? GetFileWithTagsById(Guid id)
    {
        return _fileRepo.GetFileWithTagsById(id);
    }

    public void AddTagToFile(Guid fileId, Guid tagId)
    {
        List<Guid> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
        bool alreadyHadTag = existingIds.Contains(tagId);

        _fileTagRepo.AddTagToFile(fileId, tagId);

        if (!alreadyHadTag)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: true));

            _ = SyncMetadataToFileAsync(fileId);
        }

        LibraryChanged?.Invoke();
    }

    public void AddTagToFiles(IEnumerable<Guid> fileIds, Guid tagId)
    {
        List<Guid> ids = [.. fileIds];

        if (ids.Count == 0)
        {
            return;
        }

        List<Guid> modifiedFiles = [];

        foreach (Guid id in ids)
        {
            List<Guid> existing = _fileTagRepo.GetTagIdsForFile(id);
            if (!existing.Contains(tagId))
            {
                modifiedFiles.Add(id);
            }

            _fileTagRepo.AddTagToFile(id, tagId);
        }

        if (modifiedFiles.Count != 0)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: true));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = SyncMetadataToFileAsync(fileId);
            }
        }

        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFile(Guid fileId, Guid tagId)
    {
        List<Guid> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
        bool hadTag = existingIds.Contains(tagId);

        _fileTagRepo.RemoveTagFromFile(fileId, tagId);

        if (hadTag)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: false));

            _ = SyncMetadataToFileAsync(fileId);
        }

        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFiles(IEnumerable<Guid> fileIds, Guid tagId)
    {
        List<Guid> ids = [.. fileIds];

        if (ids.Count == 0)
        {
            return;
        }

        List<Guid> modifiedFiles = [];
        foreach (Guid id in ids)
        {
            List<Guid> existing = _fileTagRepo.GetTagIdsForFile(id);
            if (existing.Contains(tagId))
            {
                modifiedFiles.Add(id);
            }

            _fileTagRepo.RemoveTagFromFile(id, tagId);
        }

        if (modifiedFiles.Count != 0)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: false));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = SyncMetadataToFileAsync(fileId);
            }
        }

        LibraryChanged?.Invoke();
    }

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public List<Tag> GetTags(TagSortBy sortBy, SortDirection direction)
    {
        return _tagRepo.GetAll(sortBy, direction);
    }

    public List<Tag> GetTagsForFile(Guid fileId)
    {
        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        return _tagRepo.GetTagsByIds(tagIds);
    }

    public List<Tag> GetCommonTags(IEnumerable<Guid> fileIds)
    {
        List<Guid> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        HashSet<Guid> commonIds = [.. _fileTagRepo.GetTagIdsForFile(ids[0])];

        for (int i = 1; i < ids.Count; i++)
        {
            List<Guid> tags = _fileTagRepo.GetTagIdsForFile(ids[i]);
            commonIds.IntersectWith(tags);

            if (commonIds.Count == 0)
            {
                break;
            }
        }

        return _tagRepo.GetTagsByIds(commonIds);
    }

    public void AddTag(string name)
    {
        _tagRepo.Add(name, "#757575", null);
        LibraryChanged?.Invoke();
    }

    public void UpdateTag(Tag tag)
    {
        _tagRepo.Update(tag);
        LibraryChanged?.Invoke();
    }

    public void DeleteTag(Guid id)
    {
        _tagRepo.Delete(id);
        LibraryChanged?.Invoke();
    }

    public List<LibraryRoot> GetRoots()
    {
        return _rootRepo.GetAll();
    }

    public void AddRoot(string path)
    {
        _rootRepo.Add(path);
        LibraryChanged?.Invoke();
    }

    public void UpdateRoot(LibraryRoot root)
    {
        _rootRepo.Update(root);
        LibraryChanged?.Invoke();
    }

    public void DeleteRoot(Guid id, string path)
    {
        _rootRepo.Delete(id, path);
        LibraryChanged?.Invoke();
    }

    public async Task ScanAsync()
    {
        await _scanService.ScanAllAsync();
    }

    private async Task SyncMetadataToFileAsync(Guid fileId)
    {
        FileEntry? file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            return;
        }

        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        List<Tag> tags = _tagRepo.GetTagsByIds(tagIds);

        MediaMetadata metadata = new()
        {
            Tags = tags.Select(t => t.Name).ToList()
        };

        _ = await Task.Run(() => _exifTool.WriteMetadataAsync(file.Path, metadata));
    }
}
```

---

### `Tagium.Core\Services\RelocateResult.cs`

```csharp
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public record RelocateResult(RelocateResultStatus Status, FileEntry? ConflictingFile);
```

---

### `Tagium.Core\Services\RelocateResultStatus.cs`

```csharp
namespace Tagium.Core.Services;

public enum RelocateResultStatus { Success, Duplicate, Error, NotFound }
```

---

### `Tagium.Core\Services\ScanContext.cs`

```csharp
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ScanContext
{
    public List<string> CandidateFiles { get; } = [];
    public Dictionary<string, FileEntry> DbFilesMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> UntrackedFiles { get; } = [];
    public HashSet<string> VerifiedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasChanges { get; set; }
}
```

---

### `Tagium.Core\Services\Scanning.cs`

```csharp
using System.Diagnostics;
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Components.Modals;

namespace Tagium.Core.Services;

public class LibraryScanService
{
    private readonly LibraryRootRepository _rootRepo;
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;

    public bool IsScanning { get; private set; }
    public int ProgressCurrent { get; private set; }
    public int ProgressTotal { get; private set; }

    public event Action<int, int>? ScanProgress;
    public event Action? ScanCompleted;
    public event Action? FilesImported;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"
    };

    public LibraryScanService(
        LibraryRootRepository rootRepo,
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ExifToolService exifTool)
    {
        _rootRepo = rootRepo;
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
    }

    public async Task ScanAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        ProgressCurrent = 0;
        ProgressTotal = 0;

        await Task.Run(async () => await ExecuteScanPipelineAsync(cancellationToken), cancellationToken);
    }

    private async Task ExecuteScanPipelineAsync(CancellationToken cancellationToken)
    {
        Stopwatch uiTimer = Stopwatch.StartNew();
        ScanContext context = new();

        try
        {
            List<LibraryRoot> roots = _rootRepo.GetAll().Where(r => r.IsEnabled).ToList();
            if (roots.Count == 0)
            {
                return;
            }

            DiscoverCandidateFiles(roots, context);
            LoadDatabaseCache(roots, context);

            ProgressTotal = context.CandidateFiles.Count;
            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);

            await ProcessCandidateFilesAsync(context, uiTimer, cancellationToken);
            await ResolveMissingAndImportNewAsync(context, uiTimer, cancellationToken);

            FinalizeMissingFiles(context);
            UpdateRootsLastScanTime(roots);

            if (context.HasChanges)
            {
                FilesImported?.Invoke();
            }
        }
        finally
        {
            IsScanning = false;
            ScanProgress?.Invoke(ProgressTotal, ProgressTotal);
            ScanCompleted?.Invoke();
        }
    }

    private static void DiscoverCandidateFiles(List<LibraryRoot> roots, ScanContext context)
    {
        foreach (LibraryRoot root in roots)
        {
            if (!Directory.Exists(root.Path))
            {
                continue;
            }

            try
            {
                IEnumerable<string> files = Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)));

                context.CandidateFiles.AddRange(files);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scan] Error enumerating directory {root.Path}: {ex.Message}");
            }
        }
    }

    private void LoadDatabaseCache(List<LibraryRoot> roots, ScanContext context)
    {
        foreach (LibraryRoot root in roots)
        {
            List<FileEntry> dbFiles = _fileRepo.GetFilesUnderPath(root.Path);
            foreach (FileEntry dbFile in dbFiles)
            {
                context.DbFilesMap[dbFile.Path] = dbFile;
            }
        }
    }

    private async Task ProcessCandidateFilesAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        foreach (string filePath in context.CandidateFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await EvaluateSingleFileAsync(filePath, context, cancellationToken);

            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private async Task EvaluateSingleFileAsync(string filePath, ScanContext context, CancellationToken cancellationToken)
    {
        try
        {
            FileInfo fileInfo = new(filePath);
            long size = fileInfo.Length;
            long modifiedTime = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();

            if (context.DbFilesMap.TryGetValue(filePath, out FileEntry? existing))
            {
                if (existing.Size == size && existing.ModifiedTime == modifiedTime)
                {
                    if (existing.IsMissing)
                    {
                        existing.IsMissing = false;
                        _fileRepo.Update(existing);
                        context.HasChanges = true;
                    }
                    _ = context.VerifiedPaths.Add(filePath);
                    ProgressCurrent++;
                }
                else
                {
                    await UpdateChangedFileAsync(existing, filePath, size, modifiedTime, context, cancellationToken);
                }
            }
            else
            {
                context.UntrackedFiles.Add(filePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scan] Error examining path {filePath}: {ex.Message}");
        }
    }

    private async Task UpdateChangedFileAsync(FileEntry existing, string filePath, long size, long modifiedTime, ScanContext context, CancellationToken cancellationToken)
    {
        string? hash = await FileHasher.ComputeSha256Async(filePath);
        if (hash is not null)
        {
            existing.Hash = hash;
            existing.Size = size;
            existing.ModifiedTime = modifiedTime;
            existing.IsMissing = false;

            _fileRepo.Update(existing);
            context.HasChanges = true;
            _ = context.VerifiedPaths.Add(filePath);
        }
        ProgressCurrent++;
    }

    private async Task ResolveMissingAndImportNewAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        List<FileEntry> missingDbFiles = context.DbFilesMap.Values
            .Where(e => !context.VerifiedPaths.Contains(e.Path))
            .ToList();

        HashSet<string> processedUntracked = [];

        await DetectRenamesAndMovesAsync(missingDbFiles, context, processedUntracked, uiTimer, cancellationToken);
        await ImportRemainingUntrackedAsync(context, processedUntracked, uiTimer, cancellationToken);
    }

    private async Task DetectRenamesAndMovesAsync(List<FileEntry> missingDbFiles, ScanContext context, HashSet<string> processedUntracked, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        Dictionary<long, List<string>> untrackedBySize = GroupUntrackedBySize(context.UntrackedFiles);

        foreach (FileEntry missing in missingDbFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrEmpty(missing.Hash) || !untrackedBySize.TryGetValue(missing.Size, out List<string>? candidates))
            {
                continue;
            }

            foreach (string candidatePath in candidates)
            {
                if (processedUntracked.Contains(candidatePath))
                {
                    continue;
                }

                try
                {
                    FileInfo info = new(candidatePath);
                    long modifiedTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

                    if (missing.ModifiedTime == modifiedTime)
                    {
                        string? candidateHash = await FileHasher.ComputeSha256Async(candidatePath);
                        if (candidateHash == missing.Hash)
                        {
                            missing.Path = candidatePath;
                            missing.Name = Path.GetFileName(candidatePath);
                            missing.IsMissing = false;

                            _fileRepo.Update(missing);
                            context.HasChanges = true;

                            _ = processedUntracked.Add(candidatePath);
                            _ = context.VerifiedPaths.Add(candidatePath);
                            ProgressCurrent++;
                            break;
                        }
                    }
                }
                catch { }
            }

            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private static Dictionary<long, List<string>> GroupUntrackedBySize(List<string> untrackedFiles)
    {
        Dictionary<long, List<string>> map = [];
        foreach (string path in untrackedFiles)
        {
            try
            {
                long size = new FileInfo(path).Length;
                if (!map.TryGetValue(size, out List<string>? list))
                {
                    list = [];
                    map[size] = list;
                }
                list.Add(path);
            }
            catch { }
        }
        return map;
    }

    private async Task ImportRemainingUntrackedAsync(ScanContext context, HashSet<string> processedUntracked, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        foreach (string path in context.UntrackedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (processedUntracked.Contains(path))
            {
                continue;
            }

            try
            {
                FileInfo info = new(path);
                long size = info.Length;
                long modifiedTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

                string? hash = await FileHasher.ComputeSha256Async(path);
                if (hash is not null)
                {
                    _ = _fileRepo.Add(path, hash, size, modifiedTime);
                    context.HasChanges = true;

                    FileEntry? newEntry = _fileRepo.GetByHash(hash);
                    if (newEntry is not null)
                    {
                        await ImportMetadataTagsAsync(path, newEntry);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scan] Error importing untracked file {path}: {ex.Message}");
            }

            ProgressCurrent++;
            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private async Task ImportMetadataTagsAsync(string path, FileEntry newEntry)
    {
        MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(path);
        if (metadata is null || metadata.Tags.Count == 0)
        {
            return;
        }

        Dictionary<string, Tag> existingTags = _tagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string rawTag in metadata.Tags)
        {
            string norm = TagFormatter.Normalize(rawTag);
            if (string.IsNullOrWhiteSpace(norm))
            {
                continue;
            }

            if (!existingTags.TryGetValue(norm, out Tag? dbTag))
            {
                _tagRepo.Add(norm, "#757575", null);
                Tag? refreshed = _tagRepo.GetAll().FirstOrDefault(t => string.Equals(t.Name, norm, StringComparison.OrdinalIgnoreCase));
                if (refreshed is not null)
                {
                    _fileTagRepo.AddTagToFile(newEntry.Id, refreshed.Id);
                }
            }
            else
            {
                _fileTagRepo.AddTagToFile(newEntry.Id, dbTag.Id);
            }
        }
    }

    private void FinalizeMissingFiles(ScanContext context)
    {
        List<Guid> finalMissingIds = context.DbFilesMap.Values
            .Where(e => !context.VerifiedPaths.Contains(e.Path) && !e.IsMissing)
            .Select(e => e.Id)
            .ToList();

        if (finalMissingIds.Count > 0)
        {
            _fileRepo.SetMissingStatus(finalMissingIds, true);
            context.HasChanges = true;
        }
    }

    private void UpdateRootsLastScanTime(List<LibraryRoot> roots)
    {
        foreach (LibraryRoot root in roots)
        {
            root.LastScanTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _rootRepo.Update(root);
        }
    }

    private async Task YieldAndNotifyAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        if (uiTimer.ElapsedMilliseconds > 250)
        {
            if (context.HasChanges)
            {
                FilesImported?.Invoke();
                context.HasChanges = false;
            }

            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);
            uiTimer.Restart();

            // Critical fix: Ensure Blazor UI thread gets enough time to fetch SQLite updates and render the grid.
            await Task.Delay(50, cancellationToken);
        }
    }
}
```

---

### `Tagium.Core\Services\ThumbnailService.cs`

```csharp
using FFMpegCore;
using SkiaSharp;
using System.Collections.Concurrent;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public sealed class ThumbnailService
{
    private readonly string _cacheDir;
    private readonly bool _hasFFmpeg;
    private readonly ConcurrentDictionary<string, bool> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _activeGenerations = new(StringComparer.OrdinalIgnoreCase);

    private const int DefaultThumbnailSize = 800;
    private const int PngQuality = 85;
    private const int VideoEarlyCaptureSeconds = 5;
    private const int VideoDurationThresholdSeconds = 10;
    private const double VideoLateCaptureRatio = 0.1;

    private static readonly SKColor PlaceholderBackgroundColor = new(30, 30, 30);
    private static readonly SKColor ErrorColor = SKColors.DarkRed;
    private static readonly SKColor VideoColor = SKColors.DarkSlateBlue;
    private static readonly SKColor GenericColor = SKColors.Gray;

    public event Action<Guid>? ThumbnailGenerated;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"
    };

    public ThumbnailService()
    {
        _cacheDir = AppDataHelper.GetThumbnailCacheDirectory();

        // Populate the cache with existing thumbnail files from disk
        if (Directory.Exists(_cacheDir))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(_cacheDir, "*.png"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    _thumbnailCache.TryAdd(name, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ThumbnailService] Error scanning cache: {ex.Message}");
            }
        }

        string? ffmpegPath = FFmpegLocator.Find();
        _hasFFmpeg = ffmpegPath is not null;

        if (!_hasFFmpeg)
        {
            Console.WriteLine("[ThumbnailService] WARNING: ffmpeg.exe or ffprobe.exe not found. Video thumbnails disabled.");
            return;
        }

        string binaryFolder = Path.GetDirectoryName(ffmpegPath)!;
        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = binaryFolder });
    }

    public string GetThumbnailPath(string? hash, Guid fileId)
    {
        string key = !string.IsNullOrEmpty(hash) ? hash : fileId.ToString("N");
        return Path.Combine(_cacheDir, $"{key}.png");
    }

    public bool HasThumbnail(Guid fileId, string? hash)
    {
        string key = !string.IsNullOrEmpty(hash) ? hash : fileId.ToString("N");
        return _thumbnailCache.ContainsKey(key);
    }

    public async Task GenerateThumbnailAsync(FileEntry file, int size = DefaultThumbnailSize, CancellationToken cancellationToken = default)
    {
        string key = !string.IsNullOrEmpty(file.Hash) ? file.Hash : file.Id.ToString("N");
        string destPath = GetThumbnailPath(file.Hash, file.Id);

        // Skip immediately if we already know it exists or the original file was removed from disk
        if (_thumbnailCache.ContainsKey(key) || !File.Exists(file.Path))
        {
            return;
        }

        // Avoid triggering multiple duplicate task threads for the exact same file
        if (!_activeGenerations.TryAdd(key, true))
        {
            return;
        }

        try
        {
            if (!File.Exists(destPath))
            {
                await TryGenerateThumbnailAsync(file.Path, destPath, size, cancellationToken);
            }
            _thumbnailCache.TryAdd(key, true);
            ThumbnailGenerated?.Invoke(file.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Error generating for {file.Path}: {ex.Message}");
            GeneratePlaceholder(destPath, size, "ERR", ErrorColor);
            _thumbnailCache.TryAdd(key, true);
            ThumbnailGenerated?.Invoke(file.Id);
        }
        finally
        {
            _activeGenerations.TryRemove(key, out _);
        }
    }

    private async Task TryGenerateThumbnailAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();

        bool success = extension switch
        {
            var e when ImageExtensions.Contains(e) => await TryGenerateImageAsync(sourcePath, destPath, size, cancellationToken),
            var e when VideoExtensions.Contains(e) => await TryGenerateVideoAsync(sourcePath, destPath, size, cancellationToken),
            _ => false
        };

        if (!success)
        {
            string label = extension.TrimStart('.').ToUpperInvariant();
            GeneratePlaceholder(destPath, size, label, GenericColor);
        }
    }

    private static async Task<bool> TryGenerateImageAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        using SKBitmap? original = await Task.Run(() => SKBitmap.Decode(sourcePath), cancellationToken);

        if (original is null)
        {
            return false;
        }

        SaveResized(original, destPath, size);
        return true;
    }

    private async Task<bool> TryGenerateVideoAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        if (!_hasFFmpeg)
        {
            GeneratePlaceholder(destPath, size, "VIDEO", VideoColor);
            return true;
        }

        try
        {
            TimeSpan? duration = await AnalyzeDurationAsync(sourcePath);
            if (duration is null)
            {
                return false;
            }

            TimeSpan captureTime = CalculateCaptureTime(duration.Value);
            string? tempFramePath = await ExtractSnapshotAsync(sourcePath, captureTime);

            return tempFramePath is not null
                && await ProcessSnapshotAsync(tempFramePath, destPath, size, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Video processing error: {ex.Message}");
            GeneratePlaceholder(destPath, size, "VIDEO", VideoColor);
            return true;
        }
    }

    private static async Task<TimeSpan?> AnalyzeDurationAsync(string sourcePath)
    {
        try
        {
            IMediaAnalysis analysis = await FFProbe.AnalyseAsync(sourcePath);
            return analysis.Duration;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan CalculateCaptureTime(TimeSpan duration)
    {
        bool isLongVideo = duration.TotalSeconds > VideoDurationThresholdSeconds;

        double seconds = isLongVideo
            ? VideoEarlyCaptureSeconds
            : duration.TotalSeconds * VideoLateCaptureRatio;

        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<string?> ExtractSnapshotAsync(string sourcePath, TimeSpan captureTime)
    {
        string tempPath = Path.GetTempFileName() + ".png";

        try
        {
            await FFMpeg.SnapshotAsync(sourcePath, tempPath, null, captureTime);
            return File.Exists(tempPath) ? tempPath : null;
        }
        catch
        {
            CleanupTempFile(tempPath);
            return null;
        }
    }

    private static async Task<bool> ProcessSnapshotAsync(string tempPath, string destPath, int size, CancellationToken cancellationToken)
    {
        try
        {
            using SKBitmap? original = await Task.Run(() => SKBitmap.Decode(tempPath), cancellationToken);

            if (original is null)
            {
                return false;
            }

            SaveResized(original, destPath, size);
            return true;
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    private static void SaveResized(SKBitmap original, string destPath, int size)
    {
        using SKBitmap square = new(size, size);
        using SKCanvas canvas = new(square);

        canvas.Clear(PlaceholderBackgroundColor);

        double ratio = Math.Min((double)size / original.Width, (double)size / original.Height);
        int drawWidth = (int)(original.Width * ratio);
        int drawHeight = (int)(original.Height * ratio);
        int x = (size - drawWidth) / 2;
        int y = (size - drawHeight) / 2;

        SKImageInfo info = new(drawWidth, drawHeight);
        using SKBitmap? resized = original.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Nearest));

        if (resized is null)
        {
            return;
        }

        canvas.DrawBitmap(resized, x, y);
        EncodeAndSave(square, destPath);
    }

    private static void GeneratePlaceholder(string destPath, int size, string text, SKColor backgroundColor)
    {
        using SKBitmap bitmap = new(size, size);
        using SKCanvas canvas = new(bitmap);

        canvas.Clear(backgroundColor);

        using SKPaint textPaint = new()
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        float fontSize = size / 4.0f;
        using SKFont font = new()
        {
            Size = fontSize
        };

        float x = size / 2.0f;
        float y = (size / 2.0f) + (fontSize / 2.0f) - 5;

        canvas.DrawText(text, x, y, SKTextAlign.Center, font, textPaint);
        EncodeAndSave(bitmap, destPath);
    }

    private static void EncodeAndSave(SKBitmap bitmap, string destPath)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, PngQuality);
        using FileStream stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }

    private static void CleanupTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Failed to cleanup temp file {path}: {ex.Message}");
        }
    }
}
```

---

### `Tagium.Core\Settings\EmojiSet.cs`

```csharp
namespace Tagium.Core.Settings;

public enum EmojiSet
{
    SystemDefault,
    Apple
}
```

---

### `Tagium.Core\Settings\FileViewMode.cs`

```csharp
namespace Tagium.Core.Settings;

public enum FileViewMode
{
    Grid,
    List
}
```

---

### `Tagium.Core\Settings\Settings.cs`

```csharp
namespace Tagium.Core.Settings;

public class Settings
{
    public TagDisplayMode TagDisplay { get; set; } = TagDisplayMode.ColorCircle;
    public EmojiSet EmojiPreference { get; set; } = EmojiSet.SystemDefault;
    public int ThumbnailSize { get; set; } = 120;
    public SortBy FileSortBy { get; set; } = SortBy.DateAdded;
    public SortDirection FileSortDirection { get; set; } = SortDirection.Desc;
    public TagSidebarDisplayMode TagSidebarStyle { get; set; } = TagSidebarDisplayMode.Pill;
    public TagSortBy TagSortBy { get; set; } = TagSortBy.Name;
    public SortDirection TagSortDirection { get; set; } = SortDirection.Asc;
    public FileViewMode FileViewMode { get; set; } = FileViewMode.Grid;
}
```

---

### `Tagium.Core\Settings\SettingsManager.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Tagium.Core.Helpers;

namespace Tagium.Core.Settings;

public class SettingsManager
{
    public Settings Current { get; }

    private readonly string _settingsPath;

    public event Action? SettingsChanged;

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public SettingsManager()
    {
        string baseDir = AppDataHelper.GetBaseDirectory();
        _settingsPath = Path.Combine(baseDir, "settings.json");
        Current = Load();
    }


    public void Update(Action<Settings> updateAction, bool save = true)
    {
        updateAction(Current);

        if (save)
        {
            Save();
        }

        SettingsChanged?.Invoke();
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Current, _serializerOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error saving: {ex.Message}");
        }
    }

    private Settings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new Settings();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<Settings>(json, _serializerOptions)
                ?? new Settings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error loading: {ex.Message}. Using defaults.");
            return new Settings();
        }
    }
}
```

---

### `Tagium.Core\Settings\SortBy.cs`

```csharp
namespace Tagium.Core.Settings;

public enum SortBy
{
    Path,
    Name,
    DateAdded
}
```

---

### `Tagium.Core\Settings\SortDirection.cs`

```csharp
namespace Tagium.Core.Settings;

public enum SortDirection
{
    Asc,
    Desc
}
```

---

### `Tagium.Core\Settings\TagDisplayMode.cs`

```csharp
namespace Tagium.Core.Settings;

public enum TagDisplayMode
{
    ColorCircle,
    Emoji
}
```

---

### `Tagium.Core\Settings\TagSidebarDisplayMode.cs`

```csharp
namespace Tagium.Core.Settings;

public enum TagSidebarDisplayMode
{
    Pill,
    MinimalCircle
}
```

---

### `Tagium.Core\Settings\TagSortBy.cs`

```csharp
namespace Tagium.Core.Settings;

public enum TagSortBy
{
    Name,
    FileCount
}
```

---

### `Tagium.Core\Services\DataExport\DatabaseExport.cs`

```csharp
using Tagium.Core.Models;

namespace Tagium.Core.Services.DataExport;

public class DatabaseExport
{
    public List<Tag> Tags { get; set; } = [];
    public List<FileExportData> Files { get; set; } = [];
}
```

---

### `Tagium.Core\Services\DataExport\DataExportService.cs`

```csharp
using System.Text;
using System.Text.Json;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services.DataExport;

public class DataExportService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;

    public DataExportService(FileRepository fileRepo, TagRepository tagRepo, FileTagRepository fileTagRepo)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
    }

    public async Task ExportJsonAsync(string path)
    {
        DatabaseExport data = BuildExportData();
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }

    public async Task ExportCsvAsync(string path)
    {
        DatabaseExport data = BuildExportData();
        Dictionary<Guid, string> tagLookup = data.Tags.ToDictionary(t => t.Id, t => t.Name);
        StringBuilder sb = new();

        _ = sb.AppendLine("Id,Path,Name,Hash,DateAdded,IsMissing,Size,ModifiedTime,Tags");

        foreach (FileExportData file in data.Files)
        {
            string tags = string.Join(";", file.TagIds.Select(id => tagLookup.GetValueOrDefault(id, "unknown")));
            string line = $"{file.Id},\"{Escape(file.Path)}\",\"{Escape(file.Name)}\",{file.Hash},{file.DateAdded},{file.IsMissing},{file.Size},{file.ModifiedTime},\"{Escape(tags)}\"";
            _ = sb.AppendLine(line);
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private DatabaseExport BuildExportData()
    {
        List<FileEntry> files = _fileRepo.GetAll(SortBy.Path, SortDirection.Asc);
        List<Tag> tags = _tagRepo.GetAll();

        DatabaseExport export = new() { Tags = tags };

        foreach (FileEntry f in files)
        {
            List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(f.Id);

            export.Files.Add(new FileExportData
            {
                Id = f.Id,
                Path = f.Path,
                Name = f.Name,
                Hash = f.Hash,
                DateAdded = f.DateAdded,
                IsMissing = f.IsMissing,
                Size = f.Size,
                ModifiedTime = f.ModifiedTime,
                TagIds = tagIds
            });
        }
        return export;
    }

    private static string Escape(string s)
    {
        return s.Replace("\"", "\"\"");
    }
}
```

---

### `Tagium.Core\Services\DataExport\FileExportData.cs`

```csharp
namespace Tagium.Core.Services.DataExport;

public class FileExportData
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public long Size { get; set; }
    public long ModifiedTime { get; set; }
    public List<Guid> TagIds { get; set; } = [];
}
```

---

### `Tagium.Core\Services\DataMerge\DataMergeService.cs`

```csharp
using Microsoft.Data.Sqlite;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services.DataMerge;

public class DatabaseMergeService
{
    private readonly FileRepository _targetFileRepo;
    private readonly TagRepository _targetTagRepo;
    private readonly FileTagRepository _targetFileTagRepo;

    public DatabaseMergeService(
        DatabaseManager targetDb,
        FileRepository targetFileRepo,
        TagRepository targetTagRepo,
        FileTagRepository targetFileTagRepo)
    {
        _targetFileRepo = targetFileRepo;
        _targetTagRepo = targetTagRepo;
        _targetFileTagRepo = targetFileTagRepo;
    }

    public MergeResult Merge(string sourceDbPath)
    {
        MergeResult result = new();

        string sourceConnString = $"Data Source={sourceDbPath}";
        using SqliteConnection sourceConn = new(sourceConnString);
        sourceConn.Open();

        List<Tag> sourceTags = LoadTags(sourceConn);
        List<FileEntry> sourceFiles = LoadFiles(sourceConn);
        Dictionary<Guid, List<Guid>> sourceFileTagMap = LoadFileTags(sourceConn);

        Dictionary<string, Tag> targetTags = _targetTagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FileEntry> targetFiles = _targetFileRepo.GetAll(SortBy.Path, SortDirection.Asc)
            .Where(f => f.Hash is not null)
            .ToDictionary(f => f.Hash!);

        foreach (Tag sTag in sourceTags)
        {
            if (!targetTags.ContainsKey(sTag.Name))
            {
                _targetTagRepo.Add(sTag.Name, sTag.ColorHex, sTag.Emoji);
                result.NewTags++;
            }
        }

        targetTags = _targetTagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (FileEntry sFile in sourceFiles)
        {
            if (sFile.Hash is null || targetFiles.ContainsKey(sFile.Hash))
            {
                continue;
            }

            _ = _targetFileRepo.Add(sFile.Path, sFile.Hash, sFile.Size, sFile.ModifiedTime);
            result.NewFiles++;

            FileEntry? added = _targetFileRepo.GetByHash(sFile.Hash);
            if (added is null)
            {
                continue;
            }

            if (!sourceFileTagMap.TryGetValue(sFile.Id, out List<Guid>? sTagIds))
            {
                continue;
            }

            foreach (Guid sTagId in sTagIds)
            {
                Tag? sourceTag = sourceTags.FirstOrDefault(t => t.Id == sTagId);
                if (sourceTag is null)
                {
                    continue;
                }

                Tag? targetTag = targetTags.GetValueOrDefault(sourceTag.Name);
                if (targetTag is null)
                {
                    continue;
                }

                _targetFileTagRepo.AddTagToFile(added.Id, targetTag.Id);
                result.NewAssociations++;
            }
        }

        return result;
    }

    private static List<Tag> LoadTags(SqliteConnection conn)
    {
        List<Tag> list = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, ColorHex, Emoji FROM Tags";

        using SqliteDataReader r = cmd.ExecuteReader();

        while (r.Read())
        {
            list.Add(new Tag
            {
                Id = Guid.Parse(r.GetString(0)),
                Name = r.GetString(1),
                ColorHex = r.GetString(2),
                Emoji = r.IsDBNull(3) ? null : r.GetString(3)
            });
        }

        return list;
    }

    private static List<FileEntry> LoadFiles(SqliteConnection conn)
    {
        List<FileEntry> list = [];

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime FROM Files";

        using SqliteDataReader r = cmd.ExecuteReader();

        while (r.Read())
        {
            list.Add(new()
            {
                Id = Guid.Parse(r.GetString(0)),
                Path = r.GetString(1),
                Name = r.GetString(2),
                Hash = r.IsDBNull(3) ? null : r.GetString(3),
                DateAdded = r.GetInt64(4),
                IsMissing = r.GetBoolean(5),
                Size = r.GetInt64(6),
                ModifiedTime = r.GetInt64(7)
            });
        }

        return list;
    }

    private static Dictionary<Guid, List<Guid>> LoadFileTags(SqliteConnection conn)
    {
        Dictionary<Guid, List<Guid>> map = [];

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileId, TagId FROM FileTags";

        using SqliteDataReader r = cmd.ExecuteReader();

        while (r.Read())
        {
            Guid fid = Guid.Parse(r.GetString(0));
            Guid tid = Guid.Parse(r.GetString(1));

            if (!map.TryGetValue(fid, out List<Guid>? value))
            {
                value = [];
                map[fid] = value;
            }

            value.Add(tid);
        }

        return map;
    }
}
```

---

### `Tagium.Core\Services\DataMerge\MergeResult.cs`

```csharp
namespace Tagium.Core.Services.DataMerge;

public class MergeResult()
{
    public int NewFiles { get; set; }
    public int NewTags { get; set; }
    public int NewAssociations { get; set; }
}
```

---

### `Tagium.Core\Services\Import\ImportProgress.cs`

```csharp
namespace Tagium.Core.Services.Import;

public sealed record ImportProgress(int Processed, int Total);
```

---

### `Tagium.Core\Services\Import\ImportSummary.cs`

```csharp
namespace Tagium.Core.Services.Import;

public sealed record ImportSummary(int Added, int Duplicates, int Errors);
```

---

### `Tagium.Core\Services\LibraryIntegrity\IntegrityScanResult.cs`

```csharp
namespace Tagium.Core.Services.LibraryIntegrity;

public class IntegrityScanResult
{
    public int FilesScanned { get; set; }
    public int NewlyMissing { get; set; }
    public int NewlyFound { get; set; }
}
```

---

### `Tagium.Core\Services\LibraryIntegrity\LibraryIntegrityService.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services.LibraryIntegrity;

public class LibraryIntegrityService
{
    private readonly FileRepository _fileRepo;

    public LibraryIntegrityService(FileRepository fileRepo)
    {
        _fileRepo = fileRepo;
    }

    public async Task<IntegrityScanResult> ScanAsync()
    {
        List<FileEntry> allFiles = _fileRepo.GetAll(SortBy.Path, SortDirection.Asc);
        IntegrityScanResult result = new() { FilesScanned = allFiles.Count };

        List<Guid> toMarkMissing = [];
        List<Guid> toMarkFound = [];

        await Task.Run(() =>
        {
            foreach (FileEntry file in allFiles)
            {
                bool exists = File.Exists(file.Path);

                if (!exists && !file.IsMissing)
                {
                    toMarkMissing.Add(file.Id);
                }
                else if (exists && file.IsMissing)
                {
                    toMarkFound.Add(file.Id);
                }
            }
        });

        if (toMarkMissing.Count != 0)
        {
            _fileRepo.SetMissingStatus(toMarkMissing, true);
            result.NewlyMissing = toMarkMissing.Count;
        }

        if (toMarkFound.Count != 0)
        {
            _fileRepo.SetMissingStatus(toMarkFound, false);
            result.NewlyFound = toMarkFound.Count;
        }

        return result;
    }

    public void PurgeMissing()
    {
        _fileRepo.RemoveMissingFiles();
    }
}
```

---

### `Tagium.Core\Services\Undo\FileDeletionUndoCommand.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Core.Services.Undo;

public class FileDeletionUndoCommand : IUndoCommand
{
    private readonly FileRepository _fileRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly Action _onChanged;
    private readonly List<(FileEntry file, List<Guid> tagIds)> _deletedItems;

    public FileDeletionUndoCommand(
        FileRepository fileRepo,
        FileTagRepository fileTagRepo,
        Action onChanged,
        List<(FileEntry file, List<Guid> tagIds)> deletedItems)
    {
        _fileRepo = fileRepo;
        _fileTagRepo = fileTagRepo;
        _onChanged = onChanged;
        _deletedItems = deletedItems;
    }

    public void Undo()
    {
        foreach (var (file, tagIds) in _deletedItems)
        {
            _fileRepo.Restore(file);

            foreach (var tagId in tagIds)
            {
                _fileTagRepo.AddTagToFile(file.Id, tagId);
            }
        }

        _onChanged();
    }
}
```

---

### `Tagium.Core\Services\Undo\IUndoCommand.cs`

```csharp
namespace Tagium.Core.Services.Undo;

public interface IUndoCommand
{
    void Undo();
}
```

---

### `Tagium.Core\Services\Undo\TagAssignmentUndoCommand.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Components.Modals;

namespace Tagium.Core.Services.Undo;

public class TagAssignmentUndoCommand : IUndoCommand
{
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly Action _onChanged;
    private readonly Guid _tagId;
    private readonly List<Guid> _fileIds;
    private readonly bool _wasAdding;

    public TagAssignmentUndoCommand(
        FileTagRepository fileTagRepo,
        ExifToolService exifTool,
        FileRepository fileRepo,
        TagRepository tagRepo,
        Action onChanged,
        Guid tagId,
        IEnumerable<Guid> fileIds,
        bool wasAdding)
    {
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _onChanged = onChanged;
        _tagId = tagId;
        _fileIds = fileIds.ToList();
        _wasAdding = wasAdding;
    }

    public void Undo()
    {
        foreach (Guid fileId in _fileIds)
        {
            if (_wasAdding)
            {
                _fileTagRepo.RemoveTagFromFile(fileId, _tagId);
            }
            else
            {
                _fileTagRepo.AddTagToFile(fileId, _tagId);
            }

            _ = SyncMetadataToFileAsync(fileId);
        }

        _onChanged();
    }

    private async Task SyncMetadataToFileAsync(Guid fileId)
    {
        FileEntry? file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            return;
        }

        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        List<Tag> tags = _tagRepo.GetTagsByIds(tagIds);

        MediaMetadata metadata = new()
        {
            Tags = tags.Select(t => t.Name).ToList()
        };

        _ = await Task.Run(() => _exifTool.WriteMetadataAsync(file.Path, metadata));
    }
}
```

---

### `Tagium.Core\Services\Undo\UndoService.cs`

```csharp
namespace Tagium.Core.Services.Undo;

public class UndoService
{
    private readonly Stack<IUndoCommand> _history = new();
    private const int MaxHistory = 50;
    private bool _isExecutingUndo;

    public bool CanUndo => _history.Count > 0 && !_isExecutingUndo;

    public void Push(IUndoCommand command)
    {
        if (_isExecutingUndo)
        {
            return;
        }

        if (_history.Count >= MaxHistory)
        {
            // Remove oldest to prevent unbounded growth
            var temp = _history.ToArray();
            _history.Clear();
            for (int i = temp.Length - 2; i >= 0; i--)
            {
                _history.Push(temp[i]);
            }
        }

        _history.Push(command);
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        _isExecutingUndo = true;
        try
        {
            IUndoCommand command = _history.Pop();
            command.Undo();
        }
        finally
        {
            _isExecutingUndo = false;
        }
    }

    public void Clear()
    {
        _history.Clear();
    }
}
```

---

### `Tagium.Photino.Blazor\Program.cs`

```csharp
using Photino.Blazor;
using Tagium.Photino.Blazor.Setup;

namespace Tagium.Photino.Blazor;

public class Program
{
    [STAThread]
    [Obsolete]
    private static void Main(string[] args)
    {
        PhotinoBlazorAppBuilder appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

        // Register Services
        ServiceRegistrar.ConfigureServices(appBuilder.Services);

        // Register Root Component
        appBuilder.RootComponents.Add<App>("#app");

        PhotinoBlazorApp app = appBuilder.Build();

        // Pass ServiceProvider to static SchemeHandler
        SchemeHandlers.ServiceProvider = app.Services;

        // Custom Scheme for Thumbnails and Previews
        _ = app.MainWindow.RegisterCustomSchemeHandler("tagium", SchemeHandlers.HandleTagiumScheme);

        _ = app.MainWindow
            .SetTitle("Tagium")
            .SetUseOsDefaultSize(false)
            .SetSize(1200, 800)
            .Center()
            .SetLogVerbosity(0);

        app.Run();
    }
}
```

---

### `Tagium.Photino.Blazor\Tagium.Photino.Blazor.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>Tagium</AssemblyName>
    <UseWindowsForms>False</UseWindowsForms>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
    <PackageReference Include="NativeFileDialogs.Net" Version="1.3.0.1" />
    <!-- Downgraded to 3.2.3 to restore RegisterFileDropHandler support -->
    <PackageReference Include="Photino.Blazor" Version="3.2.3" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Tagium.Core\Tagium.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Content Update="wwwroot\**">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

</Project>
```

---

### `Tagium.Photino.Blazor\Components\App.razor`

```
@namespace Tagium.Photino.Blazor
@using Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor

<Router AppAssembly="@typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" DefaultLayout="@typeof(MainLayout)" />
    </Found>
    <NotFound>
        <LayoutView Layout="@typeof(MainLayout)">
            <p>Page not found.</p>
        </LayoutView>
    </NotFound>
</Router>
```

---

### `Tagium.Photino.Blazor\Components\AppToolbar.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor.Services
@inject AppState AppState

<div class="app-toolbar">
    <div class="toolbar-left">
        <button class="btn btn-primary" @onclick="OpenLibraryFolders">
            📁 Library Folders
        </button>
    </div>

    <div class="toolbar-center">
    </div>

    <div class="toolbar-right">
        <button class="btn btn-secondary" @onclick="ToggleTagView">
            @(AppState.CurrentView == AppView.Tags ? "Show Files" : "Manage Tags")
        </button>
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\AppToolbar.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Photino.Blazor.Components.Modals;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    [Inject] private ModalService ModalService { get; set; } = default!;

    private void OpenLibraryFolders()
    {
        ModalService.Show("Library Folders", builder =>
        {
            builder.OpenComponent<LibraryRootsModal>(0);
            builder.CloseComponent();
        });
    }

    private void ToggleTagView()
    {
        AppView view = AppState.CurrentView == AppView.Tags
            ? AppView.Files
            : AppView.Tags;

        AppState.SwitchView(view);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\FileGrid.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@implements IDisposable
@inject LibraryService Library
@inject AppState AppState
@inject SettingsManager Settings
@inject SystemLauncherService Launcher
@inject IJSRuntime JS

<div id="file-grid-container"
     class="grid-container"
     style="--thumb-size: @(Settings.Current.ThumbnailSize)px">

    @foreach (var file in Files)
    {
        bool isSelected = AppState.SelectedFileIds.Contains(file.Id);
        bool hasThumb = ThumbnailService.HasThumbnail(file.Id, file.Hash);

        <div class="file-card @(isSelected ? "selected" : "")"
             data-id="@file.Id"
             @onclick="e => OnCardClick(e, file.Id)"
             @ondblclick="e => OnCardDoubleClick(e, file)"
             @onclick:stopPropagation="true">

            <div class="thumbnail @(hasThumb ? "" : "broken")">
                @if (hasThumb)
                {
                    <img src="tagium://thumbnail/@file.Id" alt="@file.Name" />
                }
                else
                {
                    <div class="placeholder-icon">📷</div>
                }
            </div>
            <div class="filename" title="@file.Name">
                @file.Name
            </div>
        </div>
    }
</div>
```

---

### `Tagium.Photino.Blazor\Components\FileGrid.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileGrid : IDisposable
{
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;

    private List<FileEntry> Files = [];
    private DotNetObjectReference<FileGrid>? _objRef;

    protected override void OnInitialized()
    {
        LoadFiles();

        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Library.LibraryChanged += ReloadData;
        Settings.SettingsChanged += ReloadData;
        ThumbnailService.ThumbnailGenerated += OnThumbnailGenerated;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _objRef = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("gridInteractions.init", _objRef, "file-grid-container");
    }

    private void ReloadData()
    {
        _ = InvokeAsync(() =>
        {
            LoadFiles();
            StateHasChanged();
        });
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnThumbnailGenerated(Guid fileId)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
        TriggerThumbnailGeneration();
    }

    private void TriggerThumbnailGeneration()
    {
        foreach (FileEntry file in Files)
        {
            if (!ThumbnailService.HasThumbnail(file.Id, file.Hash))
            {
                _ = Task.Run(() => ThumbnailService.GenerateThumbnailAsync(file));
            }
        }
    }

    private void OnCardClick(MouseEventArgs e, Guid id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private static void OnCardDoubleClick(MouseEventArgs e, FileEntry file)
    {
        if (file.IsMissing)
        {
            return;
        }

        SystemLauncherService.OpenFile(file.Path);
    }

    [JSInvokable]
    public void SelectFile(string idStr)
    {
        if (!Guid.TryParse(idStr, out Guid id))
        {
            return;
        }

        AppState.SelectFile(id, false, false);
    }

    [JSInvokable]
    public void SelectFiles(string[] idStrs)
    {
        IEnumerable<Guid> ids = idStrs.Select(s => Guid.TryParse(s, out Guid g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value);

        AppState.SetSelection(ids);
    }

    [JSInvokable]
    public void SelectAll()
    {
        AppState.SetSelection(Files.Select(f => f.Id));
    }

    [JSInvokable]
    public void ClearSelection()
    {
        AppState.ClearSelection();
    }

    [JSInvokable]
    public void OnEnterKey()
    {
        if (AppState.SelectedFileIds.Count != 1)
        {
            return;
        }

        Guid id = AppState.SelectedFileIds.First();
        FileEntry? file = Files.FirstOrDefault(f => f.Id == id);

        if (file is null || file.IsMissing)
        {
            return;
        }

        SystemLauncherService.OpenFile(file.Path);
    }

    [JSInvokable]
    public void OnDeleteKey()
    {
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        Library.LibraryChanged -= ReloadData;
        Settings.SettingsChanged -= ReloadData;
        ThumbnailService.ThumbnailGenerated -= OnThumbnailGenerated;

        _objRef?.Dispose();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\FileListView.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@implements IDisposable
@inject LibraryService Library
@inject AppState AppState
@inject SettingsManager Settings
@inject SystemLauncherService Launcher
@inject IJSRuntime JS

<div id="file-list-container" class="list-container">
    <table class="file-list">
        <thead>
            <tr>
                <th></th> <!-- Thumbnail -->
                <th>Name</th>
                <th>Path</th>
                <th>Tags</th>
                <th>Date Added</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var file in Files)
            {
                bool isSelected = AppState.SelectedFileIds.Contains(file.Id);
                bool hasThumb = ThumbnailService.HasThumbnail(file.Id, file.Hash);

                <tr class="file-row @(isSelected ? "selected" : "")"
                    data-id="@file.Id"
                    @onclick="e => OnRowClick(e, file.Id)"
                    @ondblclick="e => OnRowDoubleClick(e, file)">

                    <td class="thumbnail-cell">
                        <div class="thumbnail @(hasThumb ? "" : "broken")">
                            @if (hasThumb)
                            {
                                <img src="tagium://thumbnail/@file.Id" alt="@file.Name" />
                            }
                            else
                            {
                                <div class="placeholder-icon">📷</div>
                            }
                        </div>
                    </td>
                    <td class="name-cell" title="@file.Name">@file.Name</td>
                    <td class="path-cell" title="@file.Path">@file.Path</td>
                    <td class="tags-cell">
                        @foreach (var tag in file.Tags)
                        {
                            <span class="tag-pill" style="border-color: @tag.ColorHex">@tag.Name</span>
                        }
                    </td>
                    <td class="date-cell">@FormatDate(file.DateAdded)</td>
                </tr>
            }
        </tbody>
    </table>
</div>
```

---

### `Tagium.Photino.Blazor\Components\FileListView.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileListView : IDisposable
{
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;

    private List<FileEntry> Files = [];

    protected override void OnInitialized()
    {
        LoadFiles();

        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Library.LibraryChanged += ReloadData;
        Settings.SettingsChanged += ReloadData;
        ThumbnailService.ThumbnailGenerated += OnThumbnailGenerated;
    }

    private void ReloadData()
    {
        _ = InvokeAsync(() =>
        {
            LoadFiles();
            StateHasChanged();
        });
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnThumbnailGenerated(Guid fileId)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
        TriggerThumbnailGeneration();
    }

    private void TriggerThumbnailGeneration()
    {
        foreach (FileEntry file in Files)
        {
            if (!ThumbnailService.HasThumbnail(file.Id, file.Hash))
            {
                _ = Task.Run(() => ThumbnailService.GenerateThumbnailAsync(file));
            }
        }
    }

    private void OnRowClick(MouseEventArgs e, Guid id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private void OnRowDoubleClick(MouseEventArgs e, FileEntry file)
    {
        if (file.IsMissing)
        {
            return;
        }

        SystemLauncherService.OpenFile(file.Path);
    }

    private static string FormatDate(long unixTimestamp)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        return dateTime.ToShortDateString();
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        Library.LibraryChanged -= ReloadData;
        Settings.SettingsChanged -= ReloadData;
        ThumbnailService.ThumbnailGenerated -= OnThumbnailGenerated;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor`

```
@namespace Tagium.Photino.Blazor.Components
@inherits LayoutComponentBase
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Services.Toasting
@using Tagium.Core.Services
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Components.Views
@inject AppState AppState
@inject LibraryService Library
@inject SettingsManager Settings
@inject ToastService ToastService

<style>
    /* 1. Force the main container to be a rigid, full-screen horizontal row that never wraps */
    .main-layout {
        display: flex !important;
        flex-direction: row !important;
        flex-wrap: nowrap !important;
        width: 100vw !important;
        height: 100vh !important;
        overflow: hidden !important;
        box-sizing: border-box !important;
    }

    /* 2. Constrain the sidebar panel horizontally and vertically */
    .sidebar-panel {
        height: 100% !important;
        overflow: hidden !important;
        display: flex !important;
        flex-direction: column !important;
    }

    /* 3. Lock the middle content panel to occupy exactly the remaining width and height */
    .content-panel {
        flex: 1 !important;
        min-width: 0 !important;
        height: 100% !important;
        display: flex !important;
        flex-direction: column !important;
        overflow: hidden !important;
    }

    /* 4. Force the view container to take up remaining height inside content panel */
    .view-container {
        flex: 1 !important;
        min-height: 0 !important;
        display: flex !important;
        flex-direction: column !important;
        overflow: hidden !important;
    }

    /* 5. Force the file grid and list scrolling containers to scroll natively */
    .list-container,
    .file-grid-container,
    .grid-container {
        flex: 1 !important;
        min-height: 0 !important;
        overflow-y: auto !important;
        overflow-x: auto !important;
    }

    /* 6. Lock the inspector panel width.
              Note: We specify a default width of 320px WITHOUT !important. This acts as the baseline,
              and allows the splitter's JavaScript inline style modification to override it perfectly. */
    .inspector-panel {
        width: 320px;
        height: 100% !important;
        overflow: hidden !important;
        display: flex !important;
        flex-direction: column !important;
        min-width: 200px !important;
        flex-shrink: 0 !important;
        flex-grow: 0 !important;
    }

        /* 7. Force all elements inside the inspector panel to respect the outer width bounds */
        .inspector-panel * {
            max-width: 100% !important;
            box-sizing: border-box !important;
        }

    /* 8. Force all container layers in the inspector subtree to wrap and clip overflow horizontally */
    .inspector-container,
    .inspector-content,
    .single-file-inspector,
    .multi-file-inspector,
    .inspector-preview {
        width: 100% !important;
        min-width: 0 !important;
        max-width: 100% !important;
        overflow-x: hidden !important;
    }

    /* 9. Guard splitters from collapsing or wrapping */
    .splitter {
        width: 4px !important;
        cursor: col-resize !important;
        background-color: #1a1a1a !important;
        align-self: stretch !important;
        flex-shrink: 0 !important;
    }
</style>

<div class="main-layout">
    <div class="sidebar-panel" id="sidebar-panel">
        <TagSidebar />
    </div>

    <div class="splitter" id="splitter-left"></div>

    <div class="content-panel">
        <AppToolbar />

        <div class="view-container">
            @if (AppState.CurrentView == AppView.Files)
            {
                <div style="display: flex; flex-direction: column; height: 100%; min-width: 0; overflow: hidden;">
                    <FilesViewToolbar @ref="FilesViewToolbarRef" />

                    @if (Settings.Current.FileViewMode == FileViewMode.Grid)
                    {
                        <FileGrid />
                    }
                    else
                    {
                        <FileListView />
                    }
                </div>
            }
            else if (AppState.CurrentView == AppView.Tags)
            {
                <TagManagerView />
            }
        </div>

        @if (_scanInProgress)
        {
            <div class="scan-progress-bar">
                <div class="progress-fill" style="width: @(_scanProgress)%"></div>
                <div class="progress-label">Scanning library... @(_scanProcessed) / @(_scanTotal)</div>
            </div>
        }
    </div>

    <div class="splitter" id="splitter-right"></div>

    <div class="inspector-panel" id="inspector-panel">
        <Tagium.Photino.Blazor.Components.Inspectors.FileInspector />
    </div>

    <ToastContainer />
    <ModalHost />
</div>
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Components.Views;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class MainLayout : IDisposable
{
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;

    private bool _scanInProgress = false;
    private int _scanProcessed = 0;
    private int _scanTotal = 0;
    private double _scanProgress = 0;
    private DotNetObjectReference<MainLayout>? _objRef;

    private FilesViewToolbar? FilesViewToolbarRef { get; set; }

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
        ScanService.ScanProgress += UpdateScanProgress;
        ScanService.ScanCompleted += ShowScanStatus;
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += OnLibraryChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _objRef = DotNetObjectReference.Create(this);

        await JS.InvokeVoidAsync(
            "layoutResizer.init",
            "sidebar-panel",
            "splitter-left",
            "inspector-panel",
            "splitter-right");

        await JS.InvokeVoidAsync("globalShortcuts.init", _objRef);
    }

    [JSInvokable]
    public async Task HandleGlobalKeyDown(string key, bool ctrlOrMeta, bool shiftKey)
    {
        if (!ctrlOrMeta)
        {
            return;
        }

        switch (key.ToLower())
        {
            case "z":
                AppState.Undo();
                break;
            case "f":
                if (FilesViewToolbarRef != null)
                {
                    await FilesViewToolbarRef.FocusSearchAsync();
                }
                break;
            case "t":
                AppState.SwitchView(AppState.CurrentView == AppView.Tags
                    ? AppView.Files
                    : AppView.Tags);
                break;
            case "r":
                await ScanService.ScanAllAsync();
                break;
        }
    }

    private void UpdateScanProgress(int current, int total)
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = true;
            _scanProcessed = current;
            _scanTotal = total;
            _scanProgress = total > 0 ? (double)current / total * 100 : 0;
            StateHasChanged();
        });
    }

    private void ShowScanStatus()
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = false;
            Toast.Show("Library scan complete.", ToastType.Success);
            Library.Refresh();
            StateHasChanged();
        });
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void ReloadCurrentView()
    {
        AppState.SetSearch("");
        AppState.ClearSelection();
        Library.Refresh();
    }

    public void Dispose()
    {
        Settings.SettingsChanged -= StateHasChanged;
        ScanService.ScanProgress -= UpdateScanProgress;
        ScanService.ScanCompleted -= ShowScanStatus;
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= OnLibraryChanged;

        _objRef?.Dispose();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\ModalHost.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor.Services
@inject ModalService ModalService

@if (IsVisible)
{
    <div class="modal-backdrop" @onclick="Close">
        <div class="modal-container" @onclick:stopPropagation="true">
            <div class="modal-header">
                <h3>@ModalService.Title</h3>
                <button class="close-btn" @onclick="Close">&times;</button>
            </div>
            <div class="modal-body">
                @ModalService.Content
            </div>
        </div>
    </div>
}
```

---

### `Tagium.Photino.Blazor\Components\ModalHost.razor.cs`

```csharp
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class ModalHost
{
    private bool IsVisible => ModalService.Content is not null;

    protected override void OnInitialized()
    {
        ModalService.OnShow += StateHasChanged;
        ModalService.OnClose += StateHasChanged;
    }

    private void Close()
    {
        ModalService.Close();
    }

    public void Dispose()
    {
        ModalService.OnShow -= StateHasChanged;
        ModalService.OnClose -= StateHasChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\TagSidebar.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Components.Shared
@inject LibraryService Library
@inject AppState AppState
@inject SettingsManager Settings

<div class="sidebar-container">
    <div class="tag-list @GetDisplayModeClass()">
        <div class="tag-item system-item @(IsAllFilesActive ? "active" : "")"
             @onclick="() => SetSearch(string.Empty)">
            <span class="icon">📁</span> All Files
        </div>

        <div class="tag-item system-item @(IsMissingActive ? "active" : "")"
             @onclick="() => AppState.SetMissingOnly(true)">
            <span class="icon">⚠️</span> Missing Files
        </div>

        <hr />

        @foreach (var tag in Tags)
        {
            bool isActive = AppState.SearchText.Equals(tag.Name, StringComparison.OrdinalIgnoreCase)
            && !AppState.IsMissingOnly
            && AppState.CurrentView == AppView.Files;

            if (Settings.Current.TagSidebarStyle == TagSidebarDisplayMode.MinimalCircle)
            {
                // Legacy mode: colored circles
                <div class="tag-item legacy @(isActive ? "active" : "")" @onclick="() => SetSearch(tag.Name)">
                    <TagIcon Tag="@tag" />
                    <span class="name">@tag.Name</span>
                    <span class="count">@tag.FileCount</span>
                </div>
            }
            else
            {
                // Modern mode: colored pills
                string tagStyle = isActive
                ? $"background-color: {tag.ColorHex}; border-color: {tag.ColorHex}; border-style: solid;"
                : $"border-color: {tag.ColorHex};";

                <div class="tag-item pill @(isActive ? "active" : "")"
                     @onclick="() => SetSearch(tag.Name)"
                     style="@tagStyle">
                    @if (!string.IsNullOrWhiteSpace(tag.Emoji))
                    {
                        <span class="item-emoji">@tag.Emoji</span>
                    }
                    <span class="name">@tag.Name</span>
                    <span class="count">@tag.FileCount</span>
                </div>
            }
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\TagSidebar.razor.cs`

```csharp
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class TagSidebar
{
    private List<Tag> Tags = [];

    private bool IsAllFilesActive => string.IsNullOrEmpty(AppState.SearchText)
        && !AppState.IsMissingOnly
        && AppState.CurrentView == AppView.Files;

    private bool IsMissingActive => AppState.IsMissingOnly
        && AppState.CurrentView == AppView.Files;

    private string GetDisplayModeClass()
    {
        return Settings.Current.TagSidebarStyle.ToString().ToLowerInvariant();
    }

    protected override void OnInitialized()
    {
        Tags = Library.GetTags();
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += RefreshData;
        Settings.SettingsChanged += StateHasChanged;
    }

    private void RefreshData()
    {
        _ = InvokeAsync(() =>
        {
            Tags = Library.GetTags();
            StateHasChanged();
        });
    }

    private void SetSearch(string text)
    {
        AppState.SetSearch(text);
    }

    public void Dispose()
    {
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= RefreshData;
        Settings.SettingsChanged -= StateHasChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\ToastContainer.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Services.Toasting
@inject ToastService ToastService

<div class="toast-container">
    @foreach (var toast in ToastService.Toasts)
    {
        <div class="toast @GetClass(toast)" @onclick="() => Remove(toast)">
            @toast.Message
        </div>
    }
</div>

<style>
    .toast-container {
        position: fixed;
        bottom: 20px;
        right: 20px;
        display: flex;
        flex-direction: column;
        gap: 10px;
        z-index: 9999;
        pointer-events: none;
    }

    .toast {
        background: #333;
        color: white;
        padding: 10px 20px;
        border-radius: 4px;
        box-shadow: 0 4px 12px rgba(0,0,0,0.5);
        font-size: 0.9em;
        min-width: 200px;
        pointer-events: auto;
        cursor: pointer;
        animation: slideIn 0.3s ease-out;
        border-left: 4px solid transparent;
        opacity: 0.95;
    }

        .toast:hover {
            opacity: 1;
        }

        .toast.info {
            border-left-color: #42a5f5;
        }

        .toast.success {
            border-left-color: #66bb6a;
        }

        .toast.error {
            border-left-color: #ef5350;
        }

    @@keyframes slideIn {
        from {
            transform: translateX(100%);
            opacity: 0;
        }

        to {
            transform: translateX(0);
            opacity: 0.95;
        }
    }
</style>
```

---

### `Tagium.Photino.Blazor\Components\ToastContainer.razor.cs`

```csharp
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class ToastContainer
{
    protected override void OnInitialized()
    {
        ToastService.OnChange += HandleToastChange;
    }

    private async void HandleToastChange()
    {
        // Marshals the update to the UI thread to prevent cross-thread exceptions/silence
        await InvokeAsync(StateHasChanged);
    }

    private static string GetClass(Toast toast)
    {
        return toast.Type switch
        {
            ToastType.Success => "success",
            ToastType.Error => "error",
            _ => "info"
        };
    }

    private void Remove(Toast toast)
    {
        ToastService.Remove(toast);
    }

    public void Dispose()
    {
        ToastService.OnChange -= HandleToastChange;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\_Imports.razor`

```
@using System.Net.Http
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.JSInterop
@using Tagium.Photino.Blazor
@using Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor.Components.Shared
@using Tagium.Photino.Blazor.Components.Views
@using Tagium.Core.Models
@using Tagium.Core.Services
```

---

### `Tagium.Photino.Blazor\Services\AppState.cs`

```csharp
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Services;

public enum AppView { Files, Tags }

public class AppState
{
    private readonly LibraryService _library;

    public AppView CurrentView { get; private set; } = AppView.Files;
    public string SearchText { get; private set; } = "";
    public bool IsMissingOnly { get; private set; }
    public HashSet<Guid> SelectedFileIds { get; private set; } = [];

    public event Action? OnChange;
    public event Action? OnSelectionChange;

    public AppState(LibraryService library)
    {
        _library = library;
    }

    public void SwitchView(AppView view)
    {
        if (CurrentView == view)
        {
            return;
        }

        CurrentView = view;
        NotifyStateChanged();
    }

    public void Undo()
    {
        _library.Undo();
    }

    public void SetSearch(string text)
    {
        SearchText = text;
        IsMissingOnly = false;
        SelectedFileIds.Clear();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        NotifyStateChanged();
    }

    public void SetMissingOnly(bool isMissing)
    {
        IsMissingOnly = isMissing;
        SearchText = "";
        SelectedFileIds.Clear();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        NotifyStateChanged();
    }

    public void SelectFile(Guid id, bool multiSelect, bool rangeSelect)
    {
        if (!multiSelect)
        {
            SelectedFileIds.Clear();
            _ = SelectedFileIds.Add(id);
        }
        else
        {
            _ = SelectedFileIds.Contains(id)
                ? SelectedFileIds.Remove(id)
                : SelectedFileIds.Add(id);
        }

        NotifySelectionChanged();
    }

    public void SetSelection(IEnumerable<Guid> ids)
    {
        SelectedFileIds.Clear();

        foreach (Guid id in ids)
        {
            _ = SelectedFileIds.Add(id);
        }

        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        if (SelectedFileIds.Count <= 0)
        {
            return;
        }

        SelectedFileIds.Clear();
        NotifySelectionChanged();
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    private void NotifySelectionChanged()
    {
        OnSelectionChange?.Invoke();
    }
}
```

---

### `Tagium.Photino.Blazor\Services\FileDialogService.cs`

```csharp
using NativeFileDialogs.Net;

namespace Tagium.Photino.Blazor.Services;

public class FileDialogService
{
    public static string[]? OpenFiles()
    {
        NfdStatus result = Nfd.OpenDialogMultiple(out string[] paths, null, null);

        return result == NfdStatus.Ok
            ? paths
            : null;
    }

    public static string? PickFolder()
    {
        NfdStatus result = Nfd.PickFolder(out string path, null);

        return result == NfdStatus.Ok
            ? path
            : null;
    }
}
```

---

### `Tagium.Photino.Blazor\Services\FileImportService.cs`

```csharp
using Microsoft.AspNetCore.Components.Forms;
using Tagium.Core.Helpers;

namespace Tagium.Photino.Blazor.Services;

public class FileImportService
{
    private const long MaxFileSize = 500 * 1024 * 1024; // 500 MB

    public async Task<string[]> SaveDroppedFilesAsync(IReadOnlyList<IBrowserFile> files)
    {
        string importDir = Path.Combine(AppDataHelper.GetBaseDirectory(), "Imports");
        if (!Directory.Exists(importDir))
        {
            _ = Directory.CreateDirectory(importDir);
        }

        List<string> savedPaths = [];

        foreach (var file in files)
        {
            try
            {
                // Create unique filename to prevent collisions in staging
                string safeName = Path.GetFileNameWithoutExtension(file.Name)
                                  + "_" + Guid.NewGuid().ToString()[..8]
                                  + Path.GetExtension(file.Name);

                string localPath = Path.Combine(importDir, safeName);

                using var stream = file.OpenReadStream(MaxFileSize);
                using var fs = File.Create(localPath);
                await stream.CopyToAsync(fs);

                savedPaths.Add(localPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileImportService] Failed to save dropped file '{file.Name}': {ex.Message}");
            }
        }

        return savedPaths.ToArray();
    }
}
```

---

### `Tagium.Photino.Blazor\Services\KeyboardShortcutService.cs`

```csharp
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Components.Shared;

namespace Tagium.Photino.Blazor.Services;

public class KeyboardShortcutService
{
    private readonly AppState _appState;
    private readonly LibraryService _library;
    private readonly SearchInput _searchInput;

    public KeyboardShortcutService(AppState appState, LibraryService library, SearchInput searchInput)
    {
        _appState = appState;
        _library = library;
        _searchInput = searchInput;
    }

    public async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (!e.CtrlKey && !e.MetaKey)
        {
            return;
        }

        switch (e.Key.ToLower())
        {
            case "f":
                await _searchInput.FocusAsync();
                break;
            case "t":
                ToggleTagView();
                break;
            case "r":
                _library.Refresh();
                break;
        }
    }

    private void ToggleTagView()
    {
        _appState.SwitchView(
            _appState.CurrentView == AppView.Tags ? AppView.Files : AppView.Tags
        );
    }
}
```

---

### `Tagium.Photino.Blazor\Services\ModalService.cs`

```csharp
using Microsoft.AspNetCore.Components;

namespace Tagium.Photino.Blazor.Services;

public class ModalService
{
    public event Action? OnShow;
    public event Action? OnClose;

    public RenderFragment? Content { get; private set; }
    public string Title { get; private set; } = "";

    public void Show(string title, RenderFragment content)
    {
        Title = title;
        Content = content;
        OnShow?.Invoke();
    }

    public void Close()
    {
        Content = null;
        OnClose?.Invoke();
    }
}
```

---

### `Tagium.Photino.Blazor\Services\SystemLauncherService.cs`

```csharp
using System.Diagnostics;

namespace Tagium.Photino.Blazor.Services;

public class SystemLauncherService
{
    public static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Launcher] Failed to open file: {ex.Message}");
        }
    }

    public static void ShowInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            // Select the file in explorer
            string argument = $"/select,\"{path}\"";

            ProcessStartInfo processStartInfo = new("explorer.exe", argument)
            {
                UseShellExecute = true
            };

            _ = Process.Start(processStartInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Launcher] Failed to show in explorer: {ex.Message}");
        }
    }
}
```

---

### `Tagium.Photino.Blazor\Setup\SchemeHandlers.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Setup;

public static class SchemeHandlers
{
    public static IServiceProvider? ServiceProvider { get; set; }

    [Obsolete]
    public static Stream HandleTagiumScheme(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Uri uri = new(url);

        if (uri.Host.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return HandleThumbnail(uri, out contentType);
        }

        return uri.Host.Equals("preview", StringComparison.OrdinalIgnoreCase) ? HandlePreview(uri, out contentType) : Stream.Null;
    }

    [Obsolete]
    private static Stream HandleThumbnail(Uri uri, out string contentType)
    {
        contentType = "image/png";

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !Guid.TryParse(segments[0], out Guid fileId))
        {
            return Stream.Null;
        }

        if (ServiceProvider is null)
        {
            return Stream.Null;
        }

        try
        {
            FileRepository fileRepo = ServiceProvider.GetRequiredService<FileRepository>();
            ThumbnailService thumbnailService = ServiceProvider.GetRequiredService<ThumbnailService>();

            FileEntry? file = fileRepo.GetById(fileId);
            if (file is null)
            {
                return Stream.Null;
            }

            string path = thumbnailService.GetThumbnailPath(file.Hash, file.Id);

            if (!File.Exists(path))
            {
                return Stream.Null;
            }

            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SchemeHandler] Failed to resolve thumbnail for {fileId}: {ex.Message}");
        }

        return Stream.Null;
    }

    private static Stream HandlePreview(Uri uri, out string contentType)
    {
        contentType = "application/octet-stream";

        if (ServiceProvider is null)
        {
            return Stream.Null;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !Guid.TryParse(segments[0], out Guid fileId))
        {
            return Stream.Null;
        }

        FileRepository fileRepo = ServiceProvider.GetRequiredService<FileRepository>();
        FileEntry? entry = fileRepo.GetById(fileId);

        if (entry is null || !File.Exists(entry.Path))
        {
            return Stream.Null;
        }

        contentType = GetMimeType(Path.GetExtension(entry.Path));
        return new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static string GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };
    }
}
```

---

### `Tagium.Photino.Blazor\Setup\ServiceRegister.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Services;
using Tagium.Core.Services.DataExport;
using Tagium.Core.Services.DataMerge;
using Tagium.Core.Services.LibraryIntegrity;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Shared;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Setup;

public static class ServiceRegistrar
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Core - Settings
        _ = services.AddSingleton<SettingsManager>();

        // Core - Data
        _ = services.AddSingleton<DatabaseManager>();
        _ = services.AddSingleton<TagRepository>();
        _ = services.AddSingleton<FileRepository>();
        _ = services.AddSingleton<FileTagRepository>();
        _ = services.AddSingleton<FileSearchRepository>();
        _ = services.AddSingleton<LibraryRootRepository>();

        // Core - Undo
        _ = services.AddSingleton<UndoService>();

        // Core - Services
        _ = services.AddSingleton<ExifToolService>();
        _ = services.AddSingleton<ThumbnailService>();
        _ = services.AddSingleton<LibraryScanService>();
        _ = services.AddSingleton<LibraryService>();
        _ = services.AddSingleton<LibraryIntegrityService>();
        _ = services.AddSingleton<DataExportService>();
        _ = services.AddSingleton<DatabaseMergeService>();

        // UI - Components
        _ = services.AddTransient<SearchInput>();

        // UI - Services
        _ = services.AddSingleton<AppState>();
        _ = services.AddSingleton<ModalService>();
        _ = services.AddSingleton<FileDialogService>();
        _ = services.AddSingleton<SystemLauncherService>();
        _ = services.AddSingleton<ToastService>();
        _ = services.AddSingleton<KeyboardShortcutService>();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\Component.razor`

```
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\FileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject SystemLauncherService Launcher
@inject FileDialogService FileDialog
@inject AppState AppState
@inject ModalService ModalService

<div class="inspector-container">
    <div class="inspector-header">
        <span class="title">Inspector</span>
        <span class="badge">@AppState.SelectedFileIds.Count selected</span>
    </div>

    <div class="inspector-content">
        @if (HasRenderError)
        {
            <div class="state-message error">
                <p>⚠️ Display Error</p>
                <button class="btn" @onclick="LoadInspectorDataAsync">Retry</button>
            </div>
        }
        else if (SelectionMode == InspectorMode.None)
        {
            <div class="state-message">
                <span class="icon">👆</span>
                <p>Select a file to view details</p>
            </div>
        }
        else if (SelectionMode == InspectorMode.Single && SelectedFile is not null)
        {
            <SingleFileInspector File="SelectedFile"
                                 AssignedTags="AssignedTags"
                                 AvailableTags="AvailableTags" />
        }
        else if (SelectionMode == InspectorMode.Multi)
        {
            <MultiFileInspector CommonTags="CommonTags"
                                AvailableTags="AvailableTags" />
        }
        else
        {
            <div class="state-message">
                <p>Loading...</p>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\FileInspector.razor.cs`

```csharp
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class FileInspector
{
    private InspectorMode SelectionMode = InspectorMode.None;
    private FileEntry? SelectedFile;
    private bool HasRenderError = false;
    private List<Tag> AssignedTags = [];
    private List<Tag> CommonTags = [];
    private List<Tag> AvailableTags = [];
    private List<Tag> _allTagsCache = [];
    private CancellationTokenSource? _cts;

    protected override void OnInitialized()
    {
        LoadAllTags();
        AppState.OnSelectionChange += OnSelectionChanged;
        Library.LibraryChanged += OnLibraryChanged;
        _ = LoadInspectorDataAsync();
    }

    private void LoadAllTags()
    {
        _allTagsCache = Library.GetTags();
    }

    private void OnSelectionChanged()
    {
        _ = LoadInspectorDataAsync();
    }

    private void OnLibraryChanged()
    {
        LoadAllTags(); // Refresh tag cache
        _ = LoadInspectorDataAsync();
    }

    private async Task LoadInspectorDataAsync()
    {
        // Cancel any previous ongoing operation
        _cts?.Cancel();
        using var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        try
        {
            // Capture selected IDs on UI thread
            var selectedIds = AppState.SelectedFileIds.ToList();
            int count = selectedIds.Count;

            if (count == 0)
            {
                await InvokeAsync(() =>
                {
                    SelectionMode = InspectorMode.None;
                    SelectedFile = null;
                    StateHasChanged();
                });
                return;
            }

            // Perform database operations in background
            await Task.Run(async () =>
            {
                if (count == 1)
                {
                    var id = selectedIds[0];
                    var file = Library.GetFileWithTagsById(id);
                    if (file is null)
                    {
                        return;
                    }

                    var assigned = file.Tags ?? [];
                    var available = GetAvailableTags(assigned);

                    // Update UI on UI thread
                    await InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        SelectionMode = InspectorMode.Single;
                        SelectedFile = file;
                        AssignedTags = assigned;
                        AvailableTags = available;
                        StateHasChanged();
                    });
                }
                else
                {
                    var common = Library.GetCommonTags(selectedIds);
                    var available = GetAvailableTags(common);

                    await InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        SelectionMode = InspectorMode.Multi;
                        SelectedFile = null;
                        CommonTags = common;
                        AvailableTags = available;
                        StateHasChanged();
                    });
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            // Ignore, expected when new selection occurs
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Inspector] Error: {ex}");
            await InvokeAsync(() =>
            {
                HasRenderError = true;
                StateHasChanged();
            });
        }
        finally
        {
            if (_cts == cts)
            {
                _cts = null;
            }
        }
    }

    private List<Tag> GetAvailableTags(List<Tag> existingTags)
    {
        HashSet<Guid> existingIds = [.. existingTags.Select(t => t.Id)];
        return [.. _allTagsCache.Where(t => !existingIds.Contains(t.Id))];
    }

    public void Dispose()
    {
        _cts?.Cancel();
        AppState.OnSelectionChange -= OnSelectionChanged;
        Library.LibraryChanged -= OnLibraryChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\InspectorMode.cs`

```csharp
namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class FileInspector
{
    private enum InspectorMode { None, Single, Multi }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\MultiFileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors
@using Tagium.Photino.Blazor.Services
@inject AppState AppState
@inject LibraryService Library

<div class="multi-file-inspector">
    <div class="inspector-header">
        <h3>@AppState.SelectedFileIds.Count Files Selected</h3>
    </div>

    <div class="inspector-tags">
        <h4>Common Tags</h4>
        <div class="tag-pills">
            @foreach (var tag in CommonTags)
            {
                <span class="tag-pill" style="background-color: @tag.ColorHex">
                    @if (!string.IsNullOrEmpty(tag.Emoji))
                    {
                        <span>@tag.Emoji</span>
                    }
                    @tag.Name
                    <button class="btn-remove-tag" @onclick="() => BulkRemoveTag(tag.Id)">✕</button>
                </span>
            }
        </div>

        @if (AvailableTags.Count > 0)
        {
            <div class="add-tag-section">
                <select @onchange="@(e => { if (Guid.TryParse(e.Value?.ToString(), out var tid)) BulkAddTag(tid); })">
                    <option value="">Add tag to all...</option>
                    @foreach (var tag in AvailableTags)
                    {
                        <option value="@tag.Id">@tag.Name</option>
                    }
                </select>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\MultiFileInspector.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class MultiFileInspector
{
    [Parameter] public List<Tag> CommonTags { get; set; } = [];
    [Parameter] public List<Tag> AvailableTags { get; set; } = [];

    private void BulkAddTag(Guid tagId)
    {
        Library.AddTagToFiles(AppState.SelectedFileIds, tagId);
    }

    private void BulkRemoveTag(Guid tagId)
    {
        Library.RemoveTagFromFiles(AppState.SelectedFileIds, tagId);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\SingleFileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors
@inject LibraryService Library

<div class="single-file-inspector" style="display: flex; flex-direction: column; gap: 15px; height: 100%; overflow-y: auto; padding: 15px; box-sizing: border-box; width: 100%; min-width: 0;">
    <div class="inspector-header" style="overflow-wrap: break-word; word-break: break-all;">
        <h3 class="file-name" style="margin: 0 0 5px 0; font-size: 1.1em; line-height: 1.3;">@File.Name</h3>
        <p class="file-path" style="margin: 0; font-size: 0.85em; color: #888;">@File.Path</p>
    </div>

    <!-- Container uses relative positioning with a locked height -->
    <div class="inspector-preview" style="position: relative !important; display: block !important; height: 250px !important; background-color: #161616 !important; border-radius: 6px !important; overflow: hidden !important; border: 1px solid #2a2a2a !important; flex-shrink: 0 !important; width: 100% !important; box-sizing: border-box !important;">
        @if (IsImage)
        {
            <!-- Image uses absolute positioning so it never influences parent width calculations -->
            <img src="tagium://preview/@File.Id" alt="@File.Name" style="position: absolute !important; top: 0 !important; left: 0 !important; width: 100% !important; height: 100% !important; object-fit: contain !important; display: block !important;" />
        }
        else if (IsVideo)
        {
            <!-- Video uses absolute positioning so it never influences parent width calculations -->
            <video src="tagium://preview/@File.Id" controls style="position: absolute !important; top: 0 !important; left: 0 !important; width: 100% !important; height: 100% !important; object-fit: contain !important; display: block !important;"></video>
        }
        else
        {
            <div class="generic-file-preview" style="font-size: 48px; color: #666; display: flex; align-items: center; justify-content: center; height: 100%; width: 100%;">📄</div>
        }
    </div>

    <div class="inspector-tags" style="display: flex; flex-direction: column; gap: 8px;">
        <h4 style="margin: 0; font-size: 0.9em; text-transform: uppercase; letter-spacing: 0.5px; color: #aaa;">Tags</h4>
        <div class="tag-pills" style="display: flex; flex-wrap: wrap; gap: 6px;">
            @foreach (var tag in AssignedTags)
            {
                <span class="tag-pill" style="background-color: @tag.ColorHex; display: inline-flex; align-items: center; gap: 6px; padding: 4px 8px; border-radius: 12px; font-size: 0.85em; color: white;">
                    @if (!string.IsNullOrEmpty(tag.Emoji))
                    {
                        <span>@tag.Emoji</span>
                    }
                    @tag.Name
                    <button class="btn-remove-tag" @onclick="() => RemoveTag(tag.Id)" style="background: none; border: none; color: white; cursor: pointer; padding: 0 2px; font-size: 0.9em; margin-left: 4px; line-height: 1;">✕</button>
                </span>
            }
        </div>

        @if (AvailableTags.Count > 0)
        {
            <div class="add-tag-section" style="margin-top: 5px;">
                <select @onchange="@(e => { if (Guid.TryParse(e.Value?.ToString(), out var tid)) AddTag(tid); })" style="width: 100%; padding: 6px; background-color: #222; border: 1px solid #333; border-radius: 4px; color: white; font-size: 0.9em;">
                    <option value="">Add tag...</option>
                    @foreach (var tag in AvailableTags)
                    {
                        <option value="@tag.Id">@tag.Name</option>
                    }
                </select>
            </div>
        }
    </div>

    <div class="inspector-actions" style="display: flex; flex-direction: column; gap: 8px; margin-top: auto; padding-top: 15px; border-top: 1px solid #222;">
        <button class="btn btn-secondary" @onclick="OpenFile" style="width: 100%; padding: 8px; background-color: #2c2c2c; border: 1px solid #3d3d3d; border-radius: 4px; color: white; cursor: pointer; text-align: center; font-size: 0.9em;">Open File</button>
        <button class="btn btn-secondary" @onclick="ShowInExplorer" style="width: 100%; padding: 8px; background-color: #2c2c2c; border: 1px solid #3d3d3d; border-radius: 4px; color: white; cursor: pointer; text-align: center; font-size: 0.9em;">Show in Explorer</button>
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\SingleFileInspector.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class SingleFileInspector
{
    [Parameter, EditorRequired] public required FileEntry File { get; set; }
    [Parameter] public List<Tag> AssignedTags { get; set; } = [];
    [Parameter] public List<Tag> AvailableTags { get; set; } = [];

    private bool IsImage => CheckExtension([".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"]);
    private bool IsVideo => CheckExtension([".mp4", ".webm", ".mov", ".mkv"]);

    private bool CheckExtension(string[] exts)
    {
        string ext = Path.GetExtension(File.Path);
        return exts.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void AddTag(Guid tagId)
    {
        Library.AddTagToFile(File.Id, tagId);
    }

    private void RemoveTag(Guid tagId)
    {
        Library.RemoveTagFromFile(File.Id, tagId);
    }

    private void OpenFile()
    {
        SystemLauncherService.OpenFile(File.Path);
    }

    private void ShowInExplorer()
    {
        SystemLauncherService.ShowInExplorer(File.Path);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Modals\DataManagementModal.razor`

```
@namespace Tagium.Photino.Blazor.Components.Modals
@using Tagium.Core.Data
@using Tagium.Core.Services
@using Tagium.Core.Services.DataExport
@using Tagium.Core.Services.DataMerge
@using Tagium.Core.Services.LibraryIntegrity
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Services.Toasting
@inject LibraryService Library
@inject LibraryIntegrityService IntegrityService
@inject DataExportService ExportService
@inject DatabaseMergeService MergeService
@inject DatabaseManager DbManager
@inject FileDialogService FileDialog
@inject ModalService ModalService
@inject ToastService Toast

<div class="data-tools">
    <div class="section">
        <h4>Integrity</h4>
        <p class="desc">Scan for files missing from disk.</p>
        <button class="btn" @onclick="ScanIntegrity">Scan Now</button>
        @if (ScanResult is not null)
        {
            <div class="result-box">
                Scanned: @ScanResult.FilesScanned <br />
                Missing: @ScanResult.NewlyMissing <br />
                Found: @ScanResult.NewlyFound
            </div>
        }
        <button class="btn btn-danger" @onclick="PurgeMissing" disabled="@(Library.GetFiles(null, true).Count == 0)">Purge Missing Files</button>
    </div>

    <hr />

    <div class="section">
        <h4>Export</h4>
        <div class="btn-row">
            <button class="btn" @onclick="ExportJson">Export JSON</button>
            <button class="btn" @onclick="ExportCsv">Export CSV</button>
            <button class="btn" @onclick="BackupDb">Backup DB</button>
        </div>
    </div>

    <hr />

    <div class="section">
        <h4>Merge & Restore</h4>
        <p class="desc">Import data from another Tagium DB, or restore a backup.</p>
        <div class="btn-row">
            <button class="btn" @onclick="MergeDb">Merge...</button>
            <button class="btn btn-danger" @onclick="RestoreDb">Restore...</button>
        </div>
        @if (MergeResult is not null)
        {
            <div class="result-box">
                New Files: @MergeResult.NewFiles <br />
                New Tags: @MergeResult.NewTags <br />
                Associations: @MergeResult.NewAssociations
            </div>
        }
    </div>
</div>

```

---

### `Tagium.Photino.Blazor\Components\Modals\DataManagementModal.razor.cs`

```csharp
using Tagium.Core.Services.DataMerge;
using Tagium.Core.Services.LibraryIntegrity;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class DataManagementModal
{
    private IntegrityScanResult? ScanResult;
    private MergeResult? MergeResult;

    private async Task ScanIntegrity()
    {
        ScanResult = null;
        ScanResult = await IntegrityService.ScanAsync();
        Library.Refresh();
    }

    private void PurgeMissing()
    {
        IntegrityService.PurgeMissing();
        Library.Refresh();
        ScanResult = null;
        Toast.Show("Missing files purged.", ToastType.Success);
    }

    private async Task ExportJson()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "tagium_export.json");
        await ExportService.ExportJsonAsync(path);
        Toast.Show($"Exported to {path}", ToastType.Success);
    }

    private async Task ExportCsv()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "tagium_export.csv");
        await ExportService.ExportCsvAsync(path);
        Toast.Show($"Exported to {path}", ToastType.Success);
    }

    private void BackupDb()
    {
        string source = DbManager.ConnectionString.Replace("Data Source=", "");
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"tagium_backup_{DateTime.Now:yyyyMMdd}.db");
        File.Copy(source, dest, true);
        Toast.Show($"Backup saved to {dest}", ToastType.Success);
    }

    private void MergeDb()
    {
        string[]? files = Services.FileDialogService.OpenFiles();

        if (files is null || files.Length == 0)
        {
            return;
        }

        MergeResult = MergeService.Merge(files[0]);
        Library.Refresh();
        Toast.Show("Merge complete.", ToastType.Success);
    }

    private void RestoreDb()
    {
        string[]? files = Services.FileDialogService.OpenFiles();

        if (files is null || files.Length == 0)
        {
            return;
        }

        string source = files[0];
        string dest = DbManager.ConnectionString.Replace("Data Source=", "");

        try
        {
            File.Copy(source, dest, true);
            Toast.Show("Database restored. Please restart Tagium.", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show($"Restore failed: {ex.Message}", ToastType.Error);
        }
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Modals\DuplicateFileModal.razor`

```
@namespace Tagium.Photino.Blazor.Components.Modals
@using Tagium.Core.Models
@using Tagium.Photino.Blazor.Services
@inject AppState AppState
@inject ModalService ModalService

<div class="duplicate-modal">
    <p>A file with the same content already exists in your library:</p>

    <div class="file-path">@ExistingFile.Path</div>

    <div class="footer">
        <button class="btn btn-primary" @onclick="ShowExisting">Show Existing File</button>
        <button class="btn" @onclick="Close">Close</button>
    </div>
</div>

```

---

### `Tagium.Photino.Blazor\Components\Modals\DuplicateFileModal.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class DuplicateFileModal
{
    [Parameter, EditorRequired]
    public required FileEntry ExistingFile { get; set; }

    private void ShowExisting()
    {
        AppState.SetSearch("");
        AppState.SetMissingOnly(false);
        AppState.SelectFile(ExistingFile.Id, false, false);

        ModalService.Close();
    }

    private void Close()
    {
        ModalService.Close();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Modals\LibraryRootsModal.razor`

```
@namespace Tagium.Photino.Blazor.Components.Modals
@using Tagium.Core.Services
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject LibraryScanService ScanService
@inject ModalService ModalService

<div class="library-roots-modal">
    <div class="modal-body">
        <p class="description">
            The library syncs automatically with configured root folders. Supported image and video formats will be indexed.
        </p>

        <div class="actions">
            <button class="btn btn-primary" @onclick="AddFolder">Add Folder...</button>
            <button class="btn btn-secondary" @onclick="TriggerRescan" disabled="@ScanService.IsScanning">
                @(ScanService.IsScanning ? "Scanning..." : "Rescan All")
            </button>
        </div>

        @if (ScanService.IsScanning)
        {
            <div class="scanning-status">
                <span class="spinner"></span>
                <span>Scanning files (@ScanService.ProgressCurrent / @ScanService.ProgressTotal)...</span>
            </div>
        }

        <div class="roots-list">
            @if (Roots.Count == 0)
            {
                <div class="empty-state">No library folders configured. Add a folder to start indexing.</div>
            }
            else
            {
                @foreach (var root in Roots)
                {
                    <div class="root-item @(root.IsEnabled ? "" : "disabled")">
                        <div class="root-info">
                            <span class="root-path" title="@root.Path">@root.Path</span>
                            <span class="root-meta">
                                @if (root.LastScanTime.HasValue)
                                {
                                    <span>Last scanned: @FormatDate(root.LastScanTime.Value)</span>
                                }
                                else
                                {
                                    <span>Never scanned</span>
                                }
                            </span>
                        </div>
                        <div class="root-actions">
                            <label class="switch" title="@(root.IsEnabled ? "Disable folder" : "Enable folder")">
                                <input type="checkbox" checked="@root.IsEnabled" @onchange="() => ToggleRoot(root)" />
                                <span class="slider"></span>
                            </label>
                            <button class="btn btn-danger-icon" @onclick="() => RemoveRoot(root)" title="Remove folder">
                                ✕
                            </button>
                        </div>
                    </div>
                }
            }
        </div>
    </div>
    <div class="modal-footer">
        <button class="btn btn-secondary" @onclick="Close">Close</button>
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Modals\LibraryRootsModal.razor.cs`

```csharp
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class LibraryRootsModal : IDisposable
{
    private List<LibraryRoot> Roots = [];

    protected override void OnInitialized()
    {
        LoadRoots();
        ScanService.ScanProgress += OnScanProgress;
        ScanService.ScanCompleted += OnScanCompleted;
    }

    private void LoadRoots()
    {
        Roots = Library.GetRoots();
    }

    private void AddFolder()
    {
        string? folder = FileDialogService.PickFolder();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Library.AddRoot(folder);
        LoadRoots();

        _ = Task.Run(() => Library.ScanAsync());
    }

    private void ToggleRoot(LibraryRoot root)
    {
        root.IsEnabled = !root.IsEnabled;
        Library.UpdateRoot(root);
        LoadRoots();
    }

    private void RemoveRoot(LibraryRoot root)
    {
        Library.DeleteRoot(root.Id, root.Path);
        LoadRoots();
    }

    private async Task TriggerRescan()
    {
        await Task.Run(() => Library.ScanAsync());
    }

    private void OnScanProgress(int current, int total)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnScanCompleted()
    {
        _ = InvokeAsync(() =>
        {
            LoadRoots();
            StateHasChanged();
        });
    }

    private static string FormatDate(long unixTimestamp)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        return dateTime.ToString("g");
    }

    private void Close()
    {
        ModalService.Close();
    }

    public void Dispose()
    {
        ScanService.ScanProgress -= OnScanProgress;
        ScanService.ScanCompleted -= OnScanCompleted;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Modals\PreferencesModal.razor`

```
@namespace Tagium.Photino.Blazor.Components.Modals
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@inject SettingsManager Settings
@inject ModalService ModalService

<div class="modal-header">Preferences</div>
<div class="modal-body">
    <h4>Tag Sidebar Style</h4>
    <p class="hint">Choose how tags appear in the sidebar.</p>
    <div class="radio-group">
        <label>
            <input type="radio"
                   name="tagSidebarStyle"
                   checked="@(Settings.Current.TagSidebarStyle == TagSidebarDisplayMode.Pill)"
                   @onchange="@(() => UpdateTagSidebarStyle(TagSidebarDisplayMode.Pill))" />
            Colored Pills
        </label>
        <label>
            <input type="radio"
                   name="tagSidebarStyle"
                   checked="@(Settings.Current.TagSidebarStyle == TagSidebarDisplayMode.MinimalCircle)"
                   @onchange="@(() => UpdateTagSidebarStyle(TagSidebarDisplayMode.MinimalCircle))" />
            Minimal Circles
        </label>
    </div>

    <h4>Emoji Set</h4>
    <p class="hint">Requires restart to fully apply font changes.</p>
    <div class="radio-group">
        <label>
            <input type="radio"
                   name="emojiSet"
                   checked="@(Settings.Current.EmojiPreference == EmojiSet.SystemDefault)"
                   @onchange="@(() => UpdateEmojiSet(EmojiSet.SystemDefault))" />
            System Default (Windows)
        </label>
        <label>
            <input type="radio"
                   name="emojiSet"
                   checked="@(Settings.Current.EmojiPreference == EmojiSet.Apple)"
                   @onchange="@(() => UpdateEmojiSet(EmojiSet.Apple))" />
            Apple Color Emoji
        </label>
    </div>
</div>
<div class="modal-footer">
    <button class="btn" @onclick="Close">Close</button>
</div>

```

---

### `Tagium.Photino.Blazor\Components\Modals\PreferencesModal.razor.cs`

```csharp
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class PreferencesModal
{
    private void UpdateTagSidebarStyle(TagSidebarDisplayMode mode)
    {
        Settings.Update(s => s.TagSidebarStyle = mode);
    }

    private void UpdateEmojiSet(EmojiSet set)
    {
        Settings.Update(s => s.EmojiPreference = set);
    }

    private void Close()
    {
        ModalService.Close();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\IconButton.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared

<button class="icon-button @Class"
        @onclick="OnClick"
        title="@Title"
        aria-label="@Title"
        disabled="@Disabled">
    @ChildContent
</button>
```

---

### `Tagium.Photino.Blazor\Components\Shared\IconButton.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class IconButton
{
    [Parameter, EditorRequired] public RenderFragment? ChildContent { get; set; }
    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter] public string Class { get; set; } = "";
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback OnClick { get; set; }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\SearchInput.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Photino.Blazor.Services
@inject AppState AppState
@implements IDisposable

<input type="text"
       class="search-input"
       placeholder="Search files..."
       value="@_value"
       @oninput="OnInput"
       @ref="_inputElement"
       aria-label="Search files" />
```

---

### `Tagium.Photino.Blazor\Components\Shared\SearchInput.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class SearchInput
{
    private CancellationTokenSource? _debounceCts;
    private string _value = "";
    private ElementReference _inputElement;

    private const int DebounceMilliseconds = 300;

    protected override void OnInitialized()
    {
        _value = AppState.SearchText;
    }

    private void OnInput(ChangeEventArgs e)
    {
        _value = e.Value?.ToString() ?? "";

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();

        _ = DebounceSearchAsync(_debounceCts.Token);
    }

    private async Task DebounceSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken);
            AppState.SetSearch(_value);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
    }

    public async Task FocusAsync()
    {
        await _inputElement.FocusAsync();
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\SortControls.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Settings
@inject SettingsManager Settings

<div class="control-group">
    <select @onchange="OnSortByChange" value="@Settings.Current.FileSortBy" aria-label="Sort by">
        <option value="@SortBy.DateAdded">Date</option>
        <option value="@SortBy.Name">Name</option>
        <option value="@SortBy.Path">Path</option>
    </select>
    <select @onchange="OnSortDirChange" value="@Settings.Current.FileSortDirection" aria-label="Sort direction">
        <option value="@SortDirection.Asc">Asc</option>
        <option value="@SortDirection.Desc">Desc</option>
    </select>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Shared\SortControls.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class SortControls
{
    private void OnSortByChange(ChangeEventArgs e)
    {
        if (!Enum.TryParse<SortBy>(e.Value?.ToString(), out var result))
        {
            return;
        }

        Settings.Update(s => s.FileSortBy = result);
    }

    private void OnSortDirChange(ChangeEventArgs e)
    {
        if (!Enum.TryParse<SortDirection>(e.Value?.ToString(), out var result))
        {
            return;
        }

        Settings.Update(s => s.FileSortDirection = result);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\TagIcon.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Models

@if (ShowIcon)
{
    @if (!string.IsNullOrWhiteSpace(Tag.Emoji))
    {
        <span class="tag-emoji">@Tag.Emoji</span>
    }
    else
    {
        <span class="tag-color" style="background-color: @Tag.ColorHex"></span>
    }
}

```

---

### `Tagium.Photino.Blazor\Components\Shared\TagIcon.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class TagIcon
{
    [Parameter, EditorRequired] public required Tag Tag { get; set; }
    [Parameter] public bool ShowIcon { get; set; } = true;
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\ThumbnailSizeSlider.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Settings
@inject SettingsManager Settings

<div class="control-group">
    <input type="range" min="64" max="256" step="16"
           value="@Settings.Current.ThumbnailSize"
           @oninput="OnInput"
           @onchange="OnCommit"
           aria-label="Thumbnail size" />
</div>

```

---

### `Tagium.Photino.Blazor\Components\Shared\ThumbnailSizeSlider.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class ThumbnailSizeSlider
{
    private void OnInput(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out int val))
        {
            return;
        }

        Settings.Update(s => s.ThumbnailSize = val, save: false);
    }

    private void OnCommit(ChangeEventArgs e)
    {
        Settings.Save();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Views\FilesViewToolbar.razor`

```
@namespace Tagium.Photino.Blazor.Components.Views
@using Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Settings
@inject SearchInput SearchInput
@inject SettingsManager Settings

<div class="files-view-toolbar">
    <div class="search-container">
        <SearchInput @ref="SearchInput" />
    </div>

    <div class="view-mode-buttons">
        <button class="btn-icon"
                @onclick="() => SetViewMode(Settings.Current.FileViewMode == FileViewMode.Grid ? FileViewMode.List : FileViewMode.Grid)"
                title="@(Settings.Current.FileViewMode == FileViewMode.Grid ? "Switch to List View" : "Switch to Grid View")">
            <span>@(Settings.Current.FileViewMode == FileViewMode.Grid ? "☰" : "⊞")</span>
        </button>
    </div>

    <div class="view-options-wrapper">
        <button class="btn-icon @(IsMenuOpen ? "active" : "")"
                @onclick="ToggleMenu"
                title="View Options">
            <span>👁</span>
        </button>

        @if (IsMenuOpen)
        {
            <div class="view-menu-overlay" @onclick="CloseMenu"></div>
            <div class="view-menu-dropdown">
                <div class="menu-section">
                    <label>Sort By</label>
                    <SortControls />
                </div>
                <hr />
                <div class="menu-section">
                    <label>Thumbnail Size</label>
                    <ThumbnailSizeSlider />
                </div>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Views\FilesViewToolbar.razor.cs`

```csharp
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Shared;

namespace Tagium.Photino.Blazor.Components.Views;

public partial class FilesViewToolbar
{
    private bool IsMenuOpen = false;

    public async Task FocusSearchAsync()
    {
        await SearchInput.FocusAsync();
    }

    private void ToggleMenu()
    {
        IsMenuOpen = !IsMenuOpen;
    }

    private void CloseMenu()
    {
        IsMenuOpen = false;
    }

    private void SetViewMode(FileViewMode mode)
    {
        Settings.Update(s => s.FileViewMode = mode);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Views\TagManagerView.razor`

```
@namespace Tagium.Photino.Blazor.Components.Views
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject SettingsManager Settings

<div class="tag-manager-view">
    <div class="header-row">
        <h3>Manage Tags</h3>
        <div class="add-row">
            <input type="text" @bind="NewTagName" placeholder="New tag name..." @onkeyup="HandleKeyUp" />
            <button class="btn-primary" @onclick="AddTag">Add</button>
        </div>
    </div>

    <div class="sort-controls">
        <select @onchange="OnSortByChange" value="@Settings.Current.TagSortBy">
            <option value="@TagSortBy.Name">Name</option>
            <option value="@TagSortBy.FileCount">File Count</option>
        </select>
        <select @onchange="OnSortDirChange" value="@Settings.Current.TagSortDirection">
            <option value="@SortDirection.Asc">Asc</option>
            <option value="@SortDirection.Desc">Desc</option>
        </select>
    </div>

    <div class="tag-list">
        @foreach (var tag in Tags)
        {
            <div class="tag-row">
                <!-- Color Picker -->
                <div class="color-picker-wrapper">
                    <button class="color-circle"
                            style="background-color: @tag.ColorHex"
                            @onclick="() => ToggleColorPicker(tag.Id)"></button>

                    @if (ActiveColorPickerId == tag.Id)
                    {
                        <div class="color-popup" @onclick:stopPropagation="true">
                            <div class="color-grid">
                                @foreach (var color in Palette)
                                {
                                    <div class="swatch"
                                         style="background-color: @color"
                                         @onclick="() => UpdateTagColor(tag, color)"></div>
                                }
                            </div>
                            <div class="overlay" @onclick="() => ActiveColorPickerId = null"></div>
                        </div>
                    }
                </div>

                <!-- Emoji Input -->
                <input type="text" class="emoji-input"
                       value="@tag.Emoji"
                       @onchange="@(e => UpdateTagEmoji(tag, e.Value?.ToString()))"
                       maxlength="2" />

                <!-- Name Input -->
                <input type="text" class="name-input"
                       value="@tag.Name"
                       @onchange="@(e => UpdateTagName(tag, e.Value?.ToString()))" />

                <span class="count">@tag.FileCount files</span>

                <button class="btn-icon delete" @onclick="() => DeleteTag(tag.Id)" title="Delete">&times;</button>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Views\TagManagerView.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor.Components.Views;

public partial class TagManagerView
{
    private List<Tag> Tags = [];
    private string NewTagName = "";
    private Guid? ActiveColorPickerId = null;

    private static readonly List<string> Palette =
    [
        "#e53935",
        "#d81b60",
        "#8e24aa",
        "#5e35b1",
        "#3949ab",
        "#1e88e5",
        "#039be5",
        "#00acc1",
        "#00897b",
        "#43a047",
        "#7cb342",
        "#c0ca33",
        "#fdd835",
        "#ffb300",
        "#fb8c00",
        "#f4511e",
        "#6d4c41",
        "#757575",
        "#546e7a",
        "#FFFFFF",
        "#000000"
    ];

    protected override void OnInitialized()
    {
        RefreshTags();
        Library.LibraryChanged += RefreshData;
        Settings.SettingsChanged += StateHasChanged;
    }

    private void RefreshData()
    {
        _ = InvokeAsync(() =>
        {
            RefreshTags();
            StateHasChanged();
        });
    }

    private void RefreshTags()
    {
        Tags = Library.GetTags(Settings.Current.TagSortBy, Settings.Current.TagSortDirection);
    }

    private void HandleKeyUp(KeyboardEventArgs e)
    {
        if (e.Key != "Enter")
        {
            return;
        }

        AddTag();
    }

    private void AddTag()
    {
        if (string.IsNullOrWhiteSpace(NewTagName))
        {
            return;
        }

        Library.AddTag(NewTagName);
        NewTagName = "";
    }

    private void DeleteTag(Guid id)
    {
        Library.DeleteTag(id);
    }

    private void UpdateTagName(Tag tag, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName == tag.Name)
        {
            return;
        }

        tag.Name = newName;
        Library.UpdateTag(tag);
    }

    private void UpdateTagEmoji(Tag tag, string? newEmoji)
    {
        tag.Emoji = string.IsNullOrWhiteSpace(newEmoji) ? null : newEmoji;
        Library.UpdateTag(tag);
    }

    private void ToggleColorPicker(Guid id)
    {
        ActiveColorPickerId = ActiveColorPickerId == id ? null : id;
    }

    private void UpdateTagColor(Tag tag, string color)
    {
        tag.ColorHex = color;
        Library.UpdateTag(tag);
        ActiveColorPickerId = null;
    }

    private void OnSortByChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<TagSortBy>(e.Value?.ToString(), out TagSortBy result))
        {
            Settings.Update(s => s.TagSortBy = result);
            RefreshTags();
        }
    }

    private void OnSortDirChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<SortDirection>(e.Value?.ToString(), out SortDirection result))
        {
            Settings.Update(s => s.TagSortDirection = result);
            RefreshTags();
        }
    }

    public void Dispose()
    {
        Library.LibraryChanged -= RefreshData;
        Settings.SettingsChanged -= StateHasChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Services\Toasting\Toast.cs`

```csharp
namespace Tagium.Photino.Blazor.Services.Toasting;

public class Toast
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Message { get; init; }
    public ToastType Type { get; init; }
}
```

---

### `Tagium.Photino.Blazor\Services\Toasting\ToastService.cs`

```csharp
namespace Tagium.Photino.Blazor.Services.Toasting;

public class ToastService
{
    public event Action? OnChange;
    public List<Toast> Toasts { get; } = [];

    public void Show(string message, ToastType type = ToastType.Info, int durationMs = 3000)
    {
        Toast toast = new()
        {
            Message = message,
            Type = type
        };

        Toasts.Add(toast);
        NotifyStateChanged();

        if (durationMs <= 0)
        {
            return;
        }

        _ = RemoveAfterDelay(toast, durationMs);
    }

    public void Remove(Toast toast)
    {
        if (!Toasts.Remove(toast))
        {
            return;
        }

        NotifyStateChanged();
    }

    private async Task RemoveAfterDelay(Toast toast, int delay)
    {
        await Task.Delay(delay);
        Remove(toast);
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }
}
```

---

### `Tagium.Photino.Blazor\Services\Toasting\ToastType.cs`

```csharp
namespace Tagium.Photino.Blazor.Services.Toasting;

public enum ToastType
{
    Info,
    Success,
    Error
}
```

---

