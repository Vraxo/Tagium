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
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.1" />
    <PackageReference Include="SkiaSharp" Version="3.119.1" />
  </ItemGroup>

</Project>
```

---

### `Tagium.Core\Data\AddFileResult.cs`

```csharp
namespace Tagium.Core.Data;

public enum AddFileResult
{
    Success,
    Duplicate,
    Error
}
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
                IsMissing INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS FileTags (
                FileId TEXT NOT NULL,
                TagId TEXT NOT NULL,
                FOREIGN KEY(FileId) REFERENCES Files(Id) ON DELETE CASCADE,
                FOREIGN KEY(TagId) REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY (FileId, TagId)
            );
        ";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = schema;
        _ = command.ExecuteNonQuery();
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
        cmd.CommandText = $"SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files {orderBy}";

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
        string missingFilter = includeMissing ? "" : "WHERE F.IsMissing = 0";

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT 
                F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing,
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
            var file = ReadFile(reader);

            if (!reader.IsDBNull(6))
            {
                string tagData = reader.GetString(6);
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
                F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing,
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
            var file = ReadFile(reader);
            if (!reader.IsDBNull(6))
            {
                string tagData = reader.GetString(6);
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
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));

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
        if (GetByHash(hash) is not null)
        {
            return AddFileResult.Duplicate;
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Files (Id, Path, Name, Hash, DateAdded) 
            VALUES (@Id, @Path, @Name, @Hash, strftime('%s','now'))";

        Guid newId = Guid.NewGuid();
        _ = cmd.Parameters.AddWithValue("@Id", newId.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.Parameters.AddWithValue("@Name", Path.GetFileName(path));
        _ = cmd.Parameters.AddWithValue("@Hash", hash);

        try
        {
            _ = cmd.ExecuteNonQuery();
            return AddFileResult.Success;
        }
        catch (SqliteException)
        {
            return AddFileResult.Duplicate;
        }
        catch
        {
            return AddFileResult.Error;
        }
    }

    public void Restore(FileEntry entry)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Files (Id, Path, Name, Hash, DateAdded, IsMissing) 
            VALUES (@Id, @Path, @Name, @Hash, @DateAdded, @IsMissing)";

        _ = cmd.Parameters.AddWithValue("@Id", entry.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", entry.Path);
        _ = cmd.Parameters.AddWithValue("@Name", entry.Name);
        _ = cmd.Parameters.AddWithValue("@Hash", (object?)entry.Hash ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@DateAdded", entry.DateAdded);
        _ = cmd.Parameters.AddWithValue("@IsMissing", entry.IsMissing ? 1 : 0);

        _ = cmd.ExecuteNonQuery();
    }

    public void Update(FileEntry file)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Files 
            SET Path = @Path, Name = @Name, Hash = @Hash, IsMissing = @IsMissing 
            WHERE Id = @Id";

        _ = cmd.Parameters.AddWithValue("@Id", file.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", file.Path);
        _ = cmd.Parameters.AddWithValue("@Name", file.Name);
        _ = cmd.Parameters.AddWithValue("@Hash", (object?)file.Hash ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@IsMissing", file.IsMissing ? 1 : 0);

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
        var parts = tagData.Split(':', 4);
        return parts.Length < 3 || !Guid.TryParse(parts[0], out var tagId)
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
        _ = query.AppendLine("SELECT DISTINCT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing");
        _ = query.AppendLine("FROM Files F");
        _ = query.AppendLine("WHERE 1=1");

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
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = await sha256.ComputeHashAsync(stream);

            return Convert.ToHexStringLower(hash);
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
    public List<Tag> Tags { get; set; } = [];
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
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;

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

    public event Action? LibraryChanged;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager,
        UndoService undoService)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
        _undoService = undoService;
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

    [Obsolete]
    public RelocateResult RelocateFile(Guid fileId, string newPath)
    {
        if (!File.Exists(newPath))
        {
            return new RelocateResult(RelocateResultStatus.NotFound, null);
        }

        string? newHash = FileHasher.ComputeSha256Async(newPath).GetAwaiter().GetResult();

        if (newHash is null)
        {
            return new RelocateResult(RelocateResultStatus.Error, null);
        }

        FileEntry? existing = _fileRepo.GetByHash(newHash);

        if (existing is not null && existing.Id != fileId)
        {
            return new RelocateResult(RelocateResultStatus.Duplicate, existing);
        }

        FileEntry? file = _fileRepo.GetById(fileId);

        if (file is null)
        {
            return new RelocateResult(RelocateResultStatus.Error, null);
        }

        file.Path = newPath;
        file.Name = Path.GetFileName(newPath);
        file.Hash = newHash;
        file.IsMissing = false;

        _fileRepo.Update(file);

        _ = _thumbnailService.GenerateThumbnailAsync(file);

        LibraryChanged?.Invoke();
        return new RelocateResult(RelocateResultStatus.Success, null);
    }

    public void RemoveFile(Guid fileId)
    {
        FileEntry? file = _fileRepo.GetById(fileId);

        if (file is null)
        {
            return;
        }

        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);

        _undoService.Push(new FileDeletionUndoCommand(
            _fileRepo,
            _fileTagRepo,
            () => LibraryChanged?.Invoke(),
            [(file, tagIds)]));

        _fileRepo.Remove(fileId);
        LibraryChanged?.Invoke();
    }

    public void RemoveFiles(IEnumerable<Guid> fileIds)
    {
        List<Guid> idsList = fileIds.ToList();

        if (idsList.Count == 0)
        {
            return;
        }

        var deletedItems = new List<(FileEntry file, List<Guid> tagIds)>();

        foreach (var id in idsList)
        {
            FileEntry? file = _fileRepo.GetById(id);

            if (file is null)
            {
                continue;
            }

            List<Guid> tags = _fileTagRepo.GetTagIdsForFile(id);
            deletedItems.Add((file, tags));
        }

        if (deletedItems.Count != 0)
        {
            _undoService.Push(new FileDeletionUndoCommand(
                _fileRepo,
                _fileTagRepo,
                () => LibraryChanged?.Invoke(),
                deletedItems));
        }

        foreach (var id in idsList)
        {
            _fileRepo.Remove(id);
        }

        LibraryChanged?.Invoke();
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
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: true));
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
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: true));
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
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: false));
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
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: false));
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

### `Tagium.Core\Services\ThumbnailService.cs`

```csharp
using FFMpegCore;
using SkiaSharp;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public sealed class ThumbnailService
{
    private readonly string _cacheDir;
    private readonly bool _hasFFmpeg;

    private const int DefaultThumbnailSize = 800;
    private const int PngQuality = 85;
    private const int VideoEarlyCaptureSeconds = 5;
    private const int VideoDurationThresholdSeconds = 10;
    private const double VideoLateCaptureRatio = 0.1;

    private static readonly SKColor PlaceholderBackgroundColor = new(30, 30, 30);
    private static readonly SKColor ErrorColor = SKColors.DarkRed;
    private static readonly SKColor VideoColor = SKColors.DarkSlateBlue;
    private static readonly SKColor GenericColor = SKColors.Gray;

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

    public string GetThumbnailPath(Guid fileId)
    {
        return Path.Combine(_cacheDir, $"{fileId:N}.png");
    }

    public bool HasThumbnail(Guid fileId)
    {
        return File.Exists(GetThumbnailPath(fileId));
    }

    public async Task GenerateThumbnailAsync(FileEntry file, int size = DefaultThumbnailSize, CancellationToken cancellationToken = default)
    {
        string destPath = GetThumbnailPath(file.Id);

        if (File.Exists(destPath) || !File.Exists(file.Path))
        {
            return;
        }

        try
        {
            await TryGenerateThumbnailAsync(file.Path, destPath, size, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Error generating for {file.Path}: {ex.Message}");
            GeneratePlaceholder(destPath, size, "ERR", ErrorColor);
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
        using SKBitmap? resized = original.Resize(info, SKFilterQuality.Medium);

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
            TextSize = size / 4.0f,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        float x = size / 2.0f;
        float y = (size / 2.0f) + (textPaint.TextSize / 2.0f) - 5;

        canvas.DrawText(text, x, y, textPaint);
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

        _ = sb.AppendLine("Id,Path,Name,Hash,DateAdded,Tags");

        foreach (var file in data.Files)
        {
            string tags = string.Join(";", file.TagIds.Select(id => tagLookup.GetValueOrDefault(id, "unknown")));
            string line = $"{file.Id},\"{Escape(file.Path)}\",\"{Escape(file.Name)}\",{file.Hash},{file.DateAdded},\"{Escape(tags)}\"";
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

        // Merge Tags - UUIDs allow direct insertion, no ID remapping needed
        foreach (Tag sTag in sourceTags)
        {
            if (!targetTags.ContainsKey(sTag.Name))
            {
                _targetTagRepo.Add(sTag.Name, sTag.ColorHex, sTag.Emoji);
                result.NewTags++;
            }
        }

        // Refresh target tags to get new ones with their UUIDs
        targetTags = _targetTagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        // Merge Files
        foreach (FileEntry sFile in sourceFiles)
        {
            if (sFile.Hash is null || targetFiles.ContainsKey(sFile.Hash))
            {
                continue;
            }

            _ = _targetFileRepo.Add(sFile.Path, sFile.Hash);
            result.NewFiles++;

            // Get the newly added file to establish associations
            FileEntry? added = _targetFileRepo.GetByHash(sFile.Hash);
            if (added is null)
            {
                continue;
            }

            // Merge Associations
            if (!sourceFileTagMap.TryGetValue(sFile.Id, out List<Guid>? sTagIds))
            {
                continue;
            }

            foreach (Guid sTagId in sTagIds)
            {
                // Find corresponding tag in source by ID, then match by name to target
                var sourceTag = sourceTags.FirstOrDefault(t => t.Id == sTagId);
                if (sourceTag is null)
                {
                    continue;
                }

                var targetTag = targetTags.GetValueOrDefault(sourceTag.Name);
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
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files";

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
                IsMissing = r.GetBoolean(5)
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

### `Tagium.Core\Services\Import\ImportResult.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Core.Services.Import;

public sealed record ImportResult(AddFileResult Status, FileEntry? Entry);
```

---

### `Tagium.Core\Services\Import\ImportService.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services.Import;

public sealed class ImportService
{
    private readonly FileRepository _fileRepo;
    private readonly ThumbnailService _thumbnailService;

    public event Action<ImportSummary>? ImportCompleted;
    public event Action<ImportProgress>? ImportProgress;

    private const int DefaultProgressInterval = 10;
    private const int MaxParallelImports = 4;

    public ImportService(FileRepository fileRepo, ThumbnailService thumbnailService)
    {
        _fileRepo = fileRepo;
        _thumbnailService = thumbnailService;
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        cancellationToken.ThrowIfCancellationRequested();

        string? hash = await FileHasher.ComputeSha256Async(path);

        if (hash is null)
        {
            return new(AddFileResult.Error, null);
        }

        FileEntry? existing = _fileRepo.GetByHash(hash);

        if (existing is not null)
        {
            return new(AddFileResult.Duplicate, existing);
        }

        AddFileResult result = _fileRepo.Add(path, hash);

        if (result != AddFileResult.Success)
        {
            return new(result, null);
        }

        FileEntry? newEntry = _fileRepo.GetByHash(hash);

        if (newEntry is not null)
        {
            await _thumbnailService.GenerateThumbnailAsync(newEntry);
        }

        return new(AddFileResult.Success, newEntry);
    }

    public async Task ImportBatchAsync(string[] paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Length == 0)
        {
            ImportCompleted?.Invoke(new ImportSummary(0, 0, 0));
            return;
        }

        using SemaphoreSlim semaphore = new(MaxParallelImports);
        List<AddFileResult> results = new(paths.Length);

        List<Task<AddFileResult>> tasks = [.. paths.Select(async (path, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);

            AddFileResult status;

            try
            {
                status = await TryImportAsync(path, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }

            lock (results)
            {
                results.Add(status);
            }

            if (ShouldReportProgress(index, paths.Length))
            {
                ImportProgress?.Invoke(new(index + 1, paths.Length));
            }

            return status;
        })];

        await Task.WhenAll(tasks);

        ImportCompleted?.Invoke(CreateSummary(results));
    }

    private async Task<AddFileResult> TryImportAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return AddFileResult.Error;
        }

        ImportResult result = await ImportAsync(path, cancellationToken);
        return result.Status;
    }

    private static ImportSummary CreateSummary(List<AddFileResult> results)
    {
        return new(
            Added: results.Count(r => r == AddFileResult.Success),
            Duplicates: results.Count(r => r == AddFileResult.Duplicate),
            Errors: results.Count(r => r == AddFileResult.Error)
        );
    }

    private static bool ShouldReportProgress(int index, int total)
    {
        int current = index + 1;
        bool isInterval = current % DefaultProgressInterval == 0;

        return isInterval || current == total;
    }
}
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

namespace Tagium.Core.Services.Undo;

public class TagAssignmentUndoCommand : IUndoCommand
{
    private readonly FileTagRepository _fileTagRepo;
    private readonly Action _onChanged;
    private readonly Guid _tagId;
    private readonly List<Guid> _fileIds;
    private readonly bool _wasAdding;

    public TagAssignmentUndoCommand(
        FileTagRepository fileTagRepo,
        Action onChanged,
        Guid tagId,
        IEnumerable<Guid> fileIds,
        bool wasAdding)
    {
        _fileTagRepo = fileTagRepo;
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
        }

        _onChanged();
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
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.1" />
    <PackageReference Include="NativeFileDialogs.Net" Version="1.2.1" />
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

### `Tagium.Photino.Blazor\Components\AppToolbar.razor.cs`

```csharp
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    [Obsolete]
    private async Task ImportFiles()
    {
        string[]? paths = FileDialogService.OpenFiles();

        if (paths is { Length: > 0 })
        {
            await ImportService.ImportBatchAsync(paths);
        }
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

### `Tagium.Photino.Blazor\Components\FileGrid.razor.cs`

```csharp
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileGrid : IDisposable
{
    private List<FileEntry> Files = [];
    private DotNetObjectReference<FileGrid>? _objRef;

    protected override void OnInitialized()
    {
        LoadFiles();

        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Library.LibraryChanged += ReloadData;
        Settings.SettingsChanged += ReloadData;
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
        LoadFiles();
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
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
        if (!Guid.TryParse(idStr, out var id))
        {
            return;
        }

        AppState.SelectFile(id, false, false);
    }

    [JSInvokable]
    public void SelectFiles(string[] idStrs)
    {
        IEnumerable<Guid> ids = idStrs.Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
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
        if (AppState.SelectedFileIds.Count == 0)
        {
            return;
        }

        Library.RemoveFiles(AppState.SelectedFileIds);
        AppState.ClearSelection();
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        Library.LibraryChanged -= ReloadData;
        Settings.SettingsChanged -= ReloadData;

        _objRef?.Dispose();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\FileListView.razor.cs`

```csharp
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileListView : IDisposable
{
    private List<FileEntry> Files = [];

    protected override void OnInitialized()
    {
        LoadFiles();

        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Library.LibraryChanged += ReloadData;
        Settings.SettingsChanged += ReloadData;
    }

    private void ReloadData()
    {
        LoadFiles();
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
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
    }
}
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor.cs`

```csharp
using Microsoft.JSInterop;
using Tagium.Photino.Blazor.Components.Views;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class MainLayout : IDisposable
{
    private bool _importInProgress = false;
    private int _importProcessed = 0;
    private int _importTotal = 0;
    private double _importProgress = 0;
    private DotNetObjectReference<MainLayout>? _objRef;
    private FilesViewToolbar? _filesViewToolbar;

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
        ImportService.ImportCompleted += ShowImportStatus;
        ImportService.ImportProgress += UpdateImportProgress;
        AppState.OnChange += StateHasChanged;
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
                if (_filesViewToolbar != null)
                {
                    await _filesViewToolbar.FocusSearchAsync();
                }
                break;
            case "t":
                AppState.SwitchView(AppState.CurrentView == AppView.Tags
                    ? AppView.Files
                    : AppView.Tags);
                break;
            case "r":
                Library.Refresh();
                break;
        }
    }

    private void UpdateImportProgress(int processed, int total)
    {
        _importInProgress = processed < total;
        _importProcessed = processed;
        _importTotal = total;
        _importProgress = (double)processed / total * 100;
        _ = InvokeAsync(StateHasChanged);
    }

    private void ShowImportStatus(int added, int duplicates, int errors)
    {
        _importInProgress = false;
        int total = added + duplicates + errors;
        if (total == 0)
        {
            return;
        }

        List<string> parts = [];

        if (added > 0)
        {
            parts.Add($"{added} added");
            Library.Refresh();
        }

        if (duplicates > 0)
        {
            parts.Add($"{duplicates} duplicate{(duplicates == 1 ? "" : "s")}");
        }

        if (errors > 0)
        {
            parts.Add($"{errors} failed");
        }

        string message = $"Import: {string.Join(", ", parts)}";

        ToastType type = errors > 0 ? ToastType.Error : added > 0 ? ToastType.Success : ToastType.Info;
        Toast.Show(message, type);
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
        ImportService.ImportCompleted -= ShowImportStatus;
        ImportService.ImportProgress -= UpdateImportProgress;
        AppState.OnChange -= StateHasChanged;

        _objRef?.Dispose();
    }
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
        Tags = Library.GetTags();
        _ = InvokeAsync(StateHasChanged);
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
using Tagium.Core.Helpers;
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

        string cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
        string path = Path.Combine(cacheDir, $"{fileId:N}.png");

        // If thumbnail exists, serve it
        if (File.Exists(path))
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        // Thumbnail missing – try to generate it on demand
        if (ServiceProvider is null)
        {
            return Stream.Null;
        }

        try
        {
            var fileRepo = ServiceProvider.GetRequiredService<FileRepository>();
            var thumbnailService = ServiceProvider.GetRequiredService<ThumbnailService>();

            var file = fileRepo.GetById(fileId);
            if (file == null)
            {
                return Stream.Null;
            }

            // Generate thumbnail synchronously (blocks this thread but it's a background handler)
            thumbnailService.GenerateThumbnailAsync(file).GetAwaiter().GetResult();

            // If generation succeeded, serve it
            if (File.Exists(path))
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SchemeHandler] Failed to generate thumbnail for {fileId}: {ex.Message}");
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

        var fileRepo = ServiceProvider.GetRequiredService<FileRepository>();
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
using Tagium.Core.Services.Import;
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

        // Core - Undo
        _ = services.AddSingleton<UndoService>();

        // Core - Services
        _ = services.AddSingleton<ThumbnailService>();
        _ = services.AddSingleton<ImportService>();
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
        _ = services.AddSingleton<FileImportService>();
        _ = services.AddSingleton<KeyboardShortcutService>();
    }
}
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

    private void RemoveMultipleFiles()
    {
        Library.RemoveFiles(AppState.SelectedFileIds);
        AppState.ClearSelection();
    }
}
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

    private void RemoveFile()
    {
        Library.RemoveFile(File.Id);
        AppState.ClearSelection();
    }
}
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

### `Tagium.Photino.Blazor\Components\Shared\FileDropZone.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class FileDropZone
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private bool _isDragging = false;
    private bool _isProcessing = false;
    private int _dragCounter = 0;

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
    }

    private void OnDragEnter(DragEventArgs e)
    {
        _dragCounter++;

        if (_dragCounter != 1)
        {
            return;
        }

        _isDragging = true;
    }

    private void OnDragLeave(DragEventArgs e)
    {
        _dragCounter--;

        if (_dragCounter > 0)
        {
            return;
        }

        _dragCounter = 0;
        _isDragging = false;
    }

    [Obsolete]
    private async Task HandleFileDrop(InputFileChangeEventArgs e)
    {
        _isProcessing = true;
        StateHasChanged();

        var files = e.GetMultipleFiles(500);
        string[] importedPaths = await FileImportService.SaveDroppedFilesAsync(files);

        _dragCounter = 0;
        _isDragging = false;
        _isProcessing = false;
        StateHasChanged();

        if (importedPaths.Length == 0)
        {
            return;
        }

        await ImportService.ImportBatchAsync(importedPaths);
    }

    public void Dispose()
    {
        Settings.SettingsChanged -= StateHasChanged;
    }
}
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
        RefreshTags();
        _ = InvokeAsync(StateHasChanged);
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
        if (Enum.TryParse<TagSortBy>(e.Value?.ToString(), out var result))
        {
            Settings.Update(s => s.TagSortBy = result);
            RefreshTags();
        }
    }

    private void OnSortDirChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<SortDirection>(e.Value?.ToString(), out var result))
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

