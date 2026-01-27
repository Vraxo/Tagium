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

        // Enable Write-Ahead Logging for better concurrency and performance
        using (SqliteCommand walCmd = connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            _ = walCmd.ExecuteNonQuery();
        }

        string schema = @"
            CREATE TABLE IF NOT EXISTS Tags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                ColorHex TEXT NOT NULL DEFAULT '#757575',
                Emoji TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL DEFAULT '',
                Hash TEXT NULL,
                DateAdded INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
                IsMissing INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS FileTags (
                FileId INTEGER NOT NULL,
                TagId INTEGER NOT NULL,
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

            // Parse tag data if present
            if (!reader.IsDBNull(6))
            {
                string tagData = reader.GetString(6);
                file.Tags = ParseTagData(tagData);
            }

            files.Add(file);
        }
        return files;
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
        return parts.Length < 3
            ? null
            : new Tag
            {
                Id = long.Parse(parts[0]),
                Name = parts[1],
                ColorHex = parts[2],
                Emoji = parts.Length > 3 && parts[3] != "" ? parts[3] : null
            };
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
        // Optimization: Checking hash first avoids expensive INSERT attempt if logic dictates hash uniqueness
        if (GetByHash(hash) is not null)
        {
            return AddFileResult.Duplicate;
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

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
        catch (SqliteException)
        {
            // UNIQUE constraint violation on Path returns duplicate/error
            return AddFileResult.Duplicate;
        }
        catch
        {
            return AddFileResult.Error;
        }
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

        _ = cmd.Parameters.AddWithValue("@Id", file.Id);
        _ = cmd.Parameters.AddWithValue("@Path", file.Path);
        _ = cmd.Parameters.AddWithValue("@Name", file.Name);
        _ = cmd.Parameters.AddWithValue("@Hash", (object?)file.Hash ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@IsMissing", file.IsMissing ? 1 : 0);

        _ = cmd.ExecuteNonQuery();
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

    public void SetMissingStatus(IEnumerable<long> fileIds, bool isMissing)
    {
        if (!fileIds.Any())
        {
            return;
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteTransaction trans = conn.BeginTransaction();

        foreach (long id in fileIds)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = trans;
            cmd.CommandText = "UPDATE Files SET IsMissing = @IsMissing WHERE Id = @Id";
            _ = cmd.Parameters.AddWithValue("@IsMissing", isMissing ? 1 : 0);
            _ = cmd.Parameters.AddWithValue("@Id", id);
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
            Id = reader.GetInt64(0),
            Path = reader.GetString(1),
            Name = reader.GetString(2),
            Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
            DateAdded = reader.GetInt64(4),
            IsMissing = reader.GetBoolean(5),
            Tags = [] // Initialize empty, will be populated by GetFilesWithTags if needed
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

    public void AddTagToFile(long fileId, long tagId)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO FileTags (FileId, TagId) VALUES (@FileId, @TagId)";
        _ = cmd.Parameters.AddWithValue("@FileId", fileId);
        _ = cmd.Parameters.AddWithValue("@TagId", tagId);
        _ = cmd.ExecuteNonQuery();
    }

    public void RemoveTagFromFile(long fileId, long tagId)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM FileTags WHERE FileId = @FileId AND TagId = @TagId";
        _ = cmd.Parameters.AddWithValue("@FileId", fileId);
        _ = cmd.Parameters.AddWithValue("@TagId", tagId);
        _ = cmd.ExecuteNonQuery();
    }

    public List<long> GetTagIdsForFile(long fileId)
    {
        List<long> ids = [];

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TagId FROM FileTags WHERE FileId = @FileId";
        _ = cmd.Parameters.AddWithValue("@FileId", fileId);

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
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
            tags.Add(new Tag
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ColorHex = reader.GetString(2),
                Emoji = reader.IsDBNull(3) ? null : reader.GetString(3),
                FileCount = reader.GetInt32(4)
            });
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

    public List<Tag> GetTagsByIds(IEnumerable<long> ids)
    {
        if (!ids.Any())
        {
            return [];
        }

        List<Tag> tags = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount
            FROM Tags T
            LEFT JOIN FileTags FT ON T.Id = FT.TagId
            WHERE T.Id IN (" + string.Join(",", ids) + @")
            GROUP BY T.Id";

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            tags.Add(new Tag
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ColorHex = reader.GetString(2),
                Emoji = reader.IsDBNull(3) ? null : reader.GetString(3),
                FileCount = reader.GetInt32(4)
            });
        }

        return tags;
    }

    public void Add(string name, string colorHex, string? emoji)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Tags (Name, ColorHex, Emoji) VALUES (@Name, @Color, @Emoji)";
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

        _ = cmd.Parameters.AddWithValue("@Id", tag.Id);
        _ = cmd.Parameters.AddWithValue("@Name", tag.Name);
        _ = cmd.Parameters.AddWithValue("@Color", tag.ColorHex);
        _ = cmd.Parameters.AddWithValue("@Emoji", (object?)tag.Emoji ?? DBNull.Value);

        try { _ = cmd.ExecuteNonQuery(); } catch { /* Ignore name collision */ }
    }

    public void Delete(long id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tags WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id);
        _ = cmd.ExecuteNonQuery();
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
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }

    /// <summary>
    /// Tags associated with this file. Populated on-demand via GetFilesWithTags().
    /// </summary>
    public List<Tag> Tags { get; set; } = [];
}
```

---

### `Tagium.Core\Models\Tag.cs`

```csharp
namespace Tagium.Core.Models;

public class Tag
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string ColorHex { get; set; } = "#757575";
    public string? Emoji { get; set; }

    // Computed property for UI convenience, populated by join queries
    public int FileCount { get; set; }

    public override string ToString()
    {
        return Name;
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

public record RelocateResult(RelocateResultStatus Status, FileEntry? ConflictingFile);

public record AddFileDetailedResult(AddFileResult Status, FileEntry? Entry);

public class LibraryService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly FileSearchRepository _searchRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SettingsManager _settingsManager;
    private readonly UndoService _undoService;

    public event Action? LibraryChanged;
    public event Action<int, int, int>? ImportCompleted; // added, duplicates, errors
    public event Action<int, int>? ImportProgress; // processed, total

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

    public bool CanUndo => _undoService.CanUndo;

    public void Undo()
    {
        _undoService.Undo();
    }

    // --- Files ---

    public List<FileEntry> GetFiles(string? searchText = null, bool missingOnly = false)
    {
        SortBy sort = _settingsManager.Current.FileSortBy;
        SortDirection dir = _settingsManager.Current.FileSortDirection;

        return missingOnly
            ? _fileRepo.GetFilesWithTags(sort, dir, true)
                .Where(f => f.IsMissing)
                .ToList()
            : string.IsNullOrWhiteSpace(searchText)
            ? _fileRepo.GetFilesWithTags(sort, dir)
            : _searchRepo.Search(searchText, sort, dir);
    }

    public FileEntry? GetFileById(long id)
    {
        return _fileRepo.GetById(id);
    }

    public bool AddFile(string path)
    {
        return AddFileDetailed(path).Status == AddFileResult.Success;
    }

    public async Task<AddFileDetailedResult> AddFileDetailedAsync(string path, bool suppressNotification = false)
    {
        string? hash = await FileHasher.ComputeSha256Async(path);
        if (hash is null)
        {
            return new AddFileDetailedResult(AddFileResult.Error, null);
        }

        // Check for existing by hash first to return the entry
        FileEntry? existing = _fileRepo.GetByHash(hash);
        if (existing is not null)
        {
            return new AddFileDetailedResult(AddFileResult.Duplicate, existing);
        }

        AddFileResult result = _fileRepo.Add(path, hash);

        if (result == AddFileResult.Success)
        {
            if (!suppressNotification)
            {
                LibraryChanged?.Invoke();
            }
            // Fetch the newly added entry
            FileEntry? newEntry = _fileRepo.GetByHash(hash);
            return new AddFileDetailedResult(AddFileResult.Success, newEntry);
        }

        return new AddFileDetailedResult(result, null);
    }

    // Kept for backward compatibility
    public AddFileDetailedResult AddFileDetailed(string path, bool suppressNotification = false)
    {
        return AddFileDetailedAsync(path, suppressNotification).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Imports multiple files asynchronously with progress reporting and non-blocking I/O.
    /// </summary>
    public async Task ImportBatchAsync(string[] paths)
    {
        if (paths.Length == 0)
        {
            ImportCompleted?.Invoke(0, 0, 0);
            return;
        }

        const int MaxParallelism = 4; // Prevent disk overload
        using var semaphore = new SemaphoreSlim(MaxParallelism);

        var tasks = paths.Select(async (path, index) =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!File.Exists(path))
                {
                    return (Status: AddFileResult.Error, Entry: null);
                }

                var result = await AddFileDetailedAsync(path, suppressNotification: true);

                // Report progress every 10 files to reduce UI churn
                if ((index + 1) % 10 == 0 || index == paths.Length - 1)
                {
                    ImportProgress?.Invoke(index + 1, paths.Length);
                }

                return (result.Status, result.Entry);
            }
            finally
            {
                _ = semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int added = results.Count(r => r.Status == AddFileResult.Success);
        int duplicates = results.Count(r => r.Status == AddFileResult.Duplicate);
        int errors = results.Count(r => r.Status == AddFileResult.Error);

        if (added > 0)
        {
            LibraryChanged?.Invoke();
        }

        ImportCompleted?.Invoke(added, duplicates, errors);
    }

    // Kept for backward compatibility if needed, but redirects to async
    public void ImportBatch(string[] paths)
    {
        _ = ImportBatchAsync(paths);
    }

    public RelocateResult RelocateFile(long fileId, string newPath)
    {
        if (!File.Exists(newPath))
        {
            return new RelocateResult(RelocateResultStatus.NotFound, null);
        }

        // This is a single file operation, async not critical
        string? newHash = FileHasher.ComputeSha256Async(newPath).GetAwaiter().GetResult();
        if (newHash is null)
        {
            return new RelocateResult(RelocateResultStatus.Error, null);
        }

        // Check if the new file is already in the database
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

        // Regenerate thumbnail for the new path
        _ = _thumbnailService.GenerateThumbnailAsync(file);

        LibraryChanged?.Invoke();
        return new RelocateResult(RelocateResultStatus.Success, null);
    }

    public void RemoveFile(long fileId)
    {
        _fileRepo.Remove(fileId);
        LibraryChanged?.Invoke();
    }

    public void RemoveFiles(IEnumerable<long> fileIds)
    {
        if (!fileIds.Any())
        {
            return;
        }

        foreach (long id in fileIds)
        {
            _fileRepo.Remove(id);
        }
        LibraryChanged?.Invoke();
    }

    public void AddTagToFile(long fileId, long tagId)
    {
        // Record which files didn't have this tag before adding
        List<long> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
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

    public void AddTagToFiles(IEnumerable<long> fileIds, long tagId)
    {
        List<long> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        // Determine which files actually receive the tag (didn't have it before)
        List<long> modifiedFiles = [];
        foreach (long id in ids)
        {
            List<long> existing = _fileTagRepo.GetTagIdsForFile(id);
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

    public void RemoveTagFromFile(long fileId, long tagId)
    {
        // Record if file had this tag before removing
        List<long> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
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

    public void RemoveTagFromFiles(IEnumerable<long> fileIds, long tagId)
    {
        List<long> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        // Determine which files actually lose the tag (had it before)
        List<long> modifiedFiles = [];
        foreach (long id in ids)
        {
            List<long> existing = _fileTagRepo.GetTagIdsForFile(id);
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

    // --- Tags ---

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public List<Tag> GetTags(TagSortBy sortBy, SortDirection direction)
    {
        return _tagRepo.GetAll(sortBy, direction);
    }

    public List<Tag> GetTagsForFile(long fileId)
    {
        List<long> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        return _tagRepo.GetTagsByIds(tagIds);
    }

    public List<Tag> GetCommonTags(IEnumerable<long> fileIds)
    {
        List<long> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        // Start with tags of first file
        HashSet<long> commonIds = _fileTagRepo.GetTagIdsForFile(ids[0]).ToHashSet();

        // Intersect with remaining files
        for (int i = 1; i < ids.Count; i++)
        {
            List<long> tags = _fileTagRepo.GetTagIdsForFile(ids[i]);
            commonIds.IntersectWith(tags);

            // Optimization: If intersection is empty, no need to continue
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

    public void DeleteTag(long id)
    {
        _tagRepo.Delete(id);
        LibraryChanged?.Invoke();
    }
}
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
using System.Drawing;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ThumbnailService
{
    private readonly string _cacheDir;
    private readonly bool _hasFFmpeg;

    private readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp"
    };

    private readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mkv",
        ".mov",
        ".avi",
        ".wmv",
        ".webm"
    };

    public ThumbnailService()
    {
        _cacheDir = AppDataHelper.GetThumbnailCacheDirectory();

        // Check for FFmpeg binaries
        string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        string ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");

        _hasFFmpeg = File.Exists(ffmpegPath) && File.Exists(ffprobePath);

        if (!_hasFFmpeg)
        {
            Console.WriteLine(
                "[ThumbnailService] WARNING: ffmpeg.exe or ffprobe.exe not found." +
                "Video thumbnails will use placeholders.");
            return;
        }

        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = AppContext.BaseDirectory });
    }

    public string GetThumbnailPath(long fileId)
    {
        return Path.Combine(_cacheDir, $"{fileId}.png");
    }

    public bool HasThumbnail(long fileId)
    {
        return File.Exists(GetThumbnailPath(fileId));
    }

    public async Task GenerateThumbnailAsync(FileEntry file, int size = 128)
    {
        string destPath = GetThumbnailPath(file.Id);
        if (File.Exists(destPath))
        {
            return;
        }

        if (!File.Exists(file.Path))
        {
            return;
        }

        string ext = Path.GetExtension(file.Path);

        try
        {
            if (_imageExtensions.Contains(ext))
            {
                await GenerateImageThumbnailAsync(file.Path, destPath, size);
            }
            else if (_videoExtensions.Contains(ext))
            {
                if (_hasFFmpeg)
                {
                    await GenerateVideoThumbnailAsync(file.Path, destPath, size);
                }
                else
                {
                    GeneratePlaceholder(destPath, size, "VIDEO", SKColors.DarkSlateBlue);
                }
            }
            else
            {
                GeneratePlaceholder(destPath, size, ext.TrimStart('.').ToUpper(), SKColors.Gray);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Error for {file.Path}: {ex.Message}");
            GeneratePlaceholder(destPath, size, "ERR", SKColors.DarkRed);
        }
    }

    private static async Task GenerateImageThumbnailAsync(string sourcePath, string destPath, int size)
    {
        using SKBitmap? original = await Task.Run(() => SKBitmap.Decode(sourcePath));
        if (original is null)
        {
            return;
        }

        SaveResized(original, destPath, size);
    }

    private static async Task GenerateVideoThumbnailAsync(string sourcePath, string destPath, int size)
    {
        try
        {
            IMediaAnalysis analysis = await FFProbe.AnalyseAsync(sourcePath);
            TimeSpan duration = analysis.Duration;

            TimeSpan captureTime = duration.TotalSeconds > 10
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromSeconds(duration.TotalSeconds * 0.1);

            string tempFrame = Path.GetTempFileName() + ".png";

            _ = await FFMpeg.SnapshotAsync(sourcePath, tempFrame, new Size(size, size), captureTime);

            if (!File.Exists(tempFrame))
            {
                return;
            }

            using SKBitmap? original = await Task.Run(() => SKBitmap.Decode(tempFrame));

            if (original is not null)
            {
                SaveResized(original, destPath, size);
            }

            File.Delete(tempFrame);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] FFMpeg error: {ex.Message}");
            GeneratePlaceholder(destPath, size, "VIDEO", SKColors.DarkSlateBlue);
        }
    }

    [Obsolete]
    private static void GeneratePlaceholder(string destPath, int size, string text, SKColor color)
    {
        using SKBitmap bitmap = new(size, size);
        using SKCanvas canvas = new(bitmap);

        canvas.Clear(color);

        using SKPaint textPaint = new()
        {
            Color = SKColors.White,
            TextSize = size / 4,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        float x = size / 2;
        float y = (size / 2) + (textPaint.TextSize / 2) - 5;

        canvas.DrawText(text, x, y, textPaint);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 85);
        using FileStream stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }

    [Obsolete]
    private static void SaveResized(SKBitmap original, string destPath, int size)
    {
        float ratio = (float)Math.Min((double)size / original.Width, (double)size / original.Height);
        int w = (int)(original.Width * ratio);
        int h = (int)(original.Height * ratio);

        SKImageInfo info = new(w, h);
        using SKBitmap? resized = original.Resize(info, SKFilterQuality.Medium);

        if (resized is null)
        {
            return;
        }

        using SKImage image = SKImage.FromBitmap(resized);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 85);
        using FileStream stream = File.OpenWrite(destPath);

        data.SaveTo(stream);
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
        Dictionary<long, string> tagLookup = data.Tags.ToDictionary(t => t.Id, t => t.Name);
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
            List<long> tagIds = _fileTagRepo.GetTagIdsForFile(f.Id);

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
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public List<long> TagIds { get; set; } = [];
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
    private readonly DatabaseManager _targetDb;
    private readonly FileRepository _targetFileRepo;
    private readonly TagRepository _targetTagRepo;
    private readonly FileTagRepository _targetFileTagRepo;

    public DatabaseMergeService(
        DatabaseManager targetDb,
        FileRepository targetFileRepo,
        TagRepository targetTagRepo,
        FileTagRepository targetFileTagRepo)
    {
        _targetDb = targetDb;
        _targetFileRepo = targetFileRepo;
        _targetTagRepo = targetTagRepo;
        _targetFileTagRepo = targetFileTagRepo;
    }

    public MergeResult Merge(string sourceDbPath)
    {
        MergeResult result = new();

        // Connect to source DB manually
        string sourceConnString = $"Data Source={sourceDbPath}";
        using SqliteConnection sourceConn = new(sourceConnString);
        sourceConn.Open();

        // 1. Load Source Data
        List<Tag> sourceTags = LoadTags(sourceConn);
        List<FileEntry> sourceFiles = LoadFiles(sourceConn);
        Dictionary<long, List<long>> sourceFileTagMap = LoadFileTags(sourceConn);

        // 2. Load Target Data
        Dictionary<string, Tag> targetTags = _targetTagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FileEntry> targetFiles = _targetFileRepo.GetAll(SortBy.Path, SortDirection.Asc)
            .Where(f => f.Hash is not null)
            .ToDictionary(f => f.Hash!);

        // 3. Merge Tags
        Dictionary<long, long> tagIdMap = MergeTags(result, sourceTags, targetTags);

        // 4. Merge Files & Associations
        MergeFilesAndAssociations(result, sourceFiles, sourceFileTagMap, targetFiles, tagIdMap);

        return result;
    }

    private void MergeFilesAndAssociations(
        MergeResult result,
        List<FileEntry> sourceFiles,
        Dictionary<long, List<long>> sourceFileTagMap,
        Dictionary<string, FileEntry> targetFiles,
        Dictionary<long, long> tagIdMap)
    {
        foreach (FileEntry sFile in sourceFiles)
        {
            if (sFile.Hash is null)
            {
                continue;
            }

            long targetFileId;

            if (targetFiles.TryGetValue(sFile.Hash, out FileEntry? existingFile))
            {
                targetFileId = existingFile.Id;
            }
            else
            {
                // Add File
                _ = _targetFileRepo.Add(sFile.Path, sFile.Hash);
                // Re-fetch to get ID. Optimization: Repo.Add should return ID/Entry.
                // Fallback:
                FileEntry? added = _targetFileRepo.GetByHash(sFile.Hash);
                if (added is null)
                {
                    continue; // Failed to add
                }

                targetFileId = added.Id;
                result.NewFiles++;
            }

            // Merge Associations
            if (!sourceFileTagMap.TryGetValue(sFile.Id, out List<long>? sTagIds))
            {
                continue;
            }

            foreach (long sTagId in sTagIds)
            {
                if (!tagIdMap.TryGetValue(sTagId, out long targetTagId))
                {
                    continue;
                }
                // Repository handles "INSERT OR IGNORE"
                _targetFileTagRepo.AddTagToFile(targetFileId, targetTagId);
                // We assume success, hard to count actual "new" without checking first.
                // We'll just increment. Real logic would check existence.
                result.NewAssociations++;
            }
        }
    }

    private Dictionary<long, long> MergeTags(MergeResult result, List<Tag> sourceTags, Dictionary<string, Tag> targetTags)
    {
        Dictionary<long, long> tagIdMap = []; // SourceId -> TargetId

        foreach (Tag sTag in sourceTags)
        {
            if (targetTags.TryGetValue(sTag.Name, out Tag? existing))
            {
                tagIdMap[sTag.Id] = existing.Id;
            }
            else
            {
                _targetTagRepo.Add(sTag.Name, sTag.ColorHex, sTag.Emoji);
                // Need to fetch the new ID. For simplicity, we reload tags or assume Add returns ID.
                // Current repo doesn't return ID. Let's re-fetch.
                Tag newTag = _targetTagRepo.GetAll().First(t => t.Name == sTag.Name);
                targetTags[newTag.Name] = newTag;
                tagIdMap[sTag.Id] = newTag.Id;
                result.NewTags++;
            }
        }

        return tagIdMap;
    }

    // Mini-loaders for the external DB connection
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
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                ColorHex = r.GetString(2),

                Emoji = r.IsDBNull(3)
                    ? null
                    : r.GetString(3)
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
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                Name = r.GetString(2),
                Hash = r.IsDBNull(3) ? null : r.GetString(3),
                DateAdded = r.GetInt64(4),
                IsMissing = r.GetBoolean(5)
            });
        }

        return list;
    }

    private static Dictionary<long, List<long>> LoadFileTags(SqliteConnection conn)
    {
        Dictionary<long, List<long>> map = [];

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileId, TagId FROM FileTags";

        using SqliteDataReader r = cmd.ExecuteReader();

        while (r.Read())
        {
            long fid = r.GetInt64(0);
            long tid = r.GetInt64(1);

            if (!map.TryGetValue(fid, out List<long>? value))
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

### `Tagium.Core\Services\LibraryItengrity\IntegrityScanResult.cs`

```csharp
namespace Tagium.Core.Services.LibraryItengrity;

public class IntegrityScanResult
{
    public int FilesScanned { get; set; }
    public int NewlyMissing { get; set; }
    public int NewlyFound { get; set; }
}
```

---

### `Tagium.Core\Services\LibraryItengrity\LibraryIntegrityService.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services.LibraryItengrity;

public class LibraryIntegrityService
{
    private readonly FileRepository _fileRepo;

    public LibraryIntegrityService(FileRepository fileRepo)
    {
        _fileRepo = fileRepo;
    }

    public async Task<IntegrityScanResult> ScanAsync()
    {
        // Get all files sorted by path for potential optimization, though mostly IO bound
        List<FileEntry> allFiles = _fileRepo.GetAll(SortBy.Path, SortDirection.Asc);
        IntegrityScanResult result = new() { FilesScanned = allFiles.Count };

        List<long> toMarkMissing = [];
        List<long> toMarkFound = [];

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
    private readonly long _tagId;
    private readonly List<long> _fileIds;
    private readonly bool _wasAdding;

    public TagAssignmentUndoCommand(
        FileTagRepository fileTagRepo,
        Action onChanged,
        long tagId,
        IEnumerable<long> fileIds,
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
        foreach (long fileId in _fileIds)
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
            .Center();

        app.Run();
    }
}
```

---

### `Tagium.Photino.Blazor\Tagium.Photino.Blazor.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
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
@using Tagium.Core.Services
@using Tagium.Photino.Blazor.Components.Modals
@using Tagium.Photino.Blazor.Components.Shared
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject FileDialogService FileDialog
@inject ModalService ModalService
@inject AppState AppState
@inject SearchInput SearchInput
@implements IDisposable

<div class="toolbar" @onkeydown="OnKeyDown" tabindex="-1">
    <button class="btn btn-primary" @onclick="ImportFiles">Add Files</button>
    <button class="btn @(AppState.CurrentView == AppView.Tags ? "active" : "")"
            @onclick="ToggleTagView">
        Tags
    </button>
    <button class="btn" @onclick="OpenDataTools">Data Tools</button>

    <div class="icon-actions">
        <IconButton Title="Settings" OnClick="OpenPreferences">⚙</IconButton>
        <IconButton Title="Reload Data" OnClick="() => Library.Refresh()">↻</IconButton>
    </div>
</div>

@code {
    private void OpenDataTools() =>
        ModalService.Show("Data Management", @<DataManagementModal />);

    private void OpenPreferences() =>
        ModalService.Show("Preferences", @<PreferencesModal />);
}
```

---

### `Tagium.Photino.Blazor\Components\AppToolbar.razor.cs`

```csharp
using Microsoft.AspNetCore.Components.Web;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    private async void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.CtrlKey || e.MetaKey)
        {
            switch (e.Key.ToLower())
            {
                case "f":
                    await SearchInput.FocusAsync();
                    break;
                case "t":
                    ToggleTagView();
                    break;
                case "r":
                    Library.Refresh();
                    break;
                case "z":
                    AppState.Undo();
                    break;
            }
        }
    }

    private async Task ImportFiles()
    {
        var paths = FileDialogService.OpenFiles();
        if (paths is { Length: > 0 })
        {
            await Library.ImportBatchAsync(paths);
        }
    }

    private void ToggleTagView()
    {
        AppState.SwitchView(
            AppState.CurrentView == AppView.Tags ? AppView.Files : AppView.Tags
        );
    }

    public void Dispose() { }
}
```

---

### `Tagium.Photino.Blazor\Components\AppToolbar.razor.css`

```css
.toolbar {
    height: var(--toolbar-height);
    border-bottom: 1px solid var(--border-color);
    display: flex;
    align-items: center;
    padding: 0 var(--space-sm);
    background: var(--panel-bg);
    gap: var(--space-xs);
    user-select: none;
    flex-shrink: 0;
}

.icon-actions {
    display: flex;
    gap: 2px;
    margin-left: auto;
}

.btn {
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-color);
    padding: 3px 10px;
    border-radius: 3px;
    cursor: pointer;
    font-size: var(--font-size-xs);
    transition: background 0.1s;
}

    .btn:hover {
        background: var(--surface-hover);
        border-color: var(--border-color);
    }

    .btn.active {
        background: rgba(255, 255, 255, 0.1);
        color: white;
        border-color: var(--border-color);
    }

.btn-primary {
    background: var(--primary-color);
    color: white;
    border-color: var(--primary-color);
    font-weight: 500;
}

    .btn-primary:hover {
        background: var(--primary-hover);
        border-color: var(--primary-hover);
    }
```

---

### `Tagium.Photino.Blazor\Components\FileGrid.razor`

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

<div id="file-grid-container"
     class="grid-container"
     style="--thumb-size: @(Settings.Current.ThumbnailSize)px">

    @foreach (var file in Files)
    {
        bool isSelected = AppState.SelectedFileIds.Contains(file.Id);
        <div class="file-card @(isSelected ? "selected" : "")"
             data-id="@file.Id"
             @onclick="e => OnCardClick(e, file.Id)"
             @ondblclick="e => OnCardDoubleClick(e, file)"
             @onclick:stopPropagation="true">

            <div class="thumbnail">
                <img src="tagium://thumbnail/@file.Id"
                     alt="@file.Name"
                     onerror="this.style.display='none'; this.parentElement.classList.add('broken');" />
                <div class="placeholder-icon">📷</div>
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
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileGrid
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

    private void OnCardClick(MouseEventArgs e, long id)
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
    public void SelectFile(long id)
    {
        AppState.SelectFile(id, false, false);
    }

    [JSInvokable]
    public void SelectFiles(long[] ids)
    {
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

        long id = AppState.SelectedFileIds.First();
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

### `Tagium.Photino.Blazor\Components\FileGrid.razor.css`

```css
.grid-container {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    align-content: flex-start;
    height: 100%;
    overflow-y: auto;
    padding: 10px;
    position: relative;
    background-color: var(--bg-color);
}

.file-card {
    background: var(--surface-bg);
    border: 1px solid var(--border-color);
    border-radius: 4px;
    display: flex;
    flex-direction: column;
    cursor: pointer;
    user-select: none;
    transition: border-color 0.1s; /* Removed width/height transition for instant responsiveness */
    /* Dynamic Sizing via CSS Variable */
    width: var(--thumb-size);
    height: calc(var(--thumb-size) + 30px);
}

    .file-card:hover {
        border-color: #666;
        background: var(--panel-bg);
    }

    .file-card.selected {
        border-color: var(--primary-color);
        box-shadow: 0 0 0 2px rgba(66, 165, 245, 0.3);
    }

.thumbnail {
    flex: 1;
    background: #000;
    overflow: hidden;
    display: flex;
    align-items: center;
    justify-content: center;
    position: relative;
}

    .thumbnail img {
        width: 100%;
        height: 100%;
        object-fit: contain;
        position: relative;
        z-index: 2;
    }

.placeholder-icon {
    position: absolute;
    font-size: 2em;
    color: var(--border-color);
    z-index: 1;
}

.thumbnail.broken .placeholder-icon {
    color: var(--text-muted);
}

.filename {
    height: 30px;
    line-height: 30px;
    padding: 0 5px;
    font-size: 12px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    background: var(--surface-bg);
    color: var(--text-color);
    border-top: 1px solid var(--border-color);
}
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor`

```
@namespace Tagium.Photino.Blazor.Components
@inherits LayoutComponentBase
@using Tagium.Core.Services
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Components.Modals
@using Tagium.Photino.Blazor.Components.Views
@using Tagium.Photino.Blazor.Components.Shared
@using Tagium.Photino.Blazor.Components.Inspectors
@using Tagium.Photino.Blazor.Services.Toasting
@inject LibraryService Library
@inject SettingsManager Settings
@inject ModalService ModalService
@inject ToastService Toast
@inject AppState AppState
@inject IJSRuntime JS

<FileDropZone>
    <div class="app-window">
        <!-- Top Menu Bar -->
        <AppToolbar />

        <!-- Main Workspace (Sidebars + Content) -->
        <div class="workspace-layout">
            @if (AppState.CurrentView == AppView.Files)
            {
                <aside id="sidebar-panel" class="left-panel">
                    <TagSidebar />
                </aside>

                <div id="splitter-left" class="splitter"></div>
            }

            <main class="center-panel">
                @if (AppState.CurrentView == AppView.Files)
                {
                    <FilesViewToolbar />
                }

                <div class="content-area">
                    <ErrorBoundary>
                        <ChildContent>
                            @if (AppState.CurrentView == AppView.Files)
                            {
                                <FileGrid />
                            }
                            else if (AppState.CurrentView == AppView.Tags)
                            {
                                <TagManagerView />
                            }
                        </ChildContent>
                        <ErrorContent Context="ex">
                            <div class="error-boundary-fallback">
                                <h3>⚠️ Something went wrong</h3>
                                <p>@ex.Message</p>
                                <button class="btn btn-primary" @onclick="ReloadCurrentView">Reload</button>
                            </div>
                        </ErrorContent>
                    </ErrorBoundary>
                </div>
            </main>

            @if (AppState.CurrentView == AppView.Files)
            {
                <div id="splitter-right" class="splitter"></div>

                <aside id="inspector-panel" class="right-panel">
                    <FileInspector />
                </aside>
            }
        </div>

        <ModalHost />
        <ToastContainer />
        @if (_importInProgress)
        {
            <div class="import-progress-overlay">
                <div class="import-progress-bar" style="width: @_importProgress%"></div>
                <span class="import-progress-text">Importing... @_importProcessed/@_importTotal</span>
            </div>
        }
    </div>
</FileDropZone>
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor.cs`

```csharp
using Microsoft.JSInterop;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class MainLayout
{
    private bool _importInProgress = false;
    private int _importProcessed = 0;
    private int _importTotal = 0;
    private double _importProgress = 0;

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
        Library.ImportCompleted += ShowImportStatus;
        Library.ImportProgress += UpdateImportProgress;
        AppState.OnChange += StateHasChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        await JS.InvokeVoidAsync(
            "layoutResizer.init",
            "sidebar-panel",
            "splitter-left",
            "inspector-panel",
            "splitter-right");
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
        Library.ImportCompleted -= ShowImportStatus;
        Library.ImportProgress -= UpdateImportProgress;
        AppState.OnChange -= StateHasChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor.css`

```css
/* Main Window Shell (Column: Toolbar | Workspace) */
.app-window {
    display: flex;
    flex-direction: column;
    height: 100vh;
    width: 100vw;
    overflow: hidden;
}

/* Workspace Row (Sidebar | Content | Inspector) */
.workspace-layout {
    display: flex;
    flex: 1; /* Fills remaining height after Toolbar */
    flex-direction: row;
    overflow: hidden;
    position: relative;
    background: var(--bg-color);
}

.left-panel {
    width: 250px;
    background: var(--panel-bg);
    display: flex;
    flex-direction: column;
    flex-shrink: 0;
    border-right: 1px solid var(--border-color);
}

.center-panel {
    flex: 1;
    display: flex;
    flex-direction: column;
    background: var(--bg-color);
    min-width: 0; /* Prevents flex items from overflowing horizontally */
}

.right-panel {
    width: 300px;
    background: var(--panel-bg);
    display: flex; /* Ensure inner content can fill height */
    flex-direction: column;
    flex-shrink: 0;
    border-left: 1px solid var(--border-color);
}

.content-area {
    flex: 1;
    overflow: hidden;
    position: relative;
    display: flex;
    flex-direction: column;
}

/* Splitter Styles */
.splitter {
    width: 4px;
    background: var(--bg-color);
    cursor: col-resize;
    flex-shrink: 0;
    position: relative;
    z-index: 30; /* Draw above toolbar during resize */
    transition: background 0.2s;
    margin-left: -2px;
    margin-right: -2px;
}

    .splitter:hover, .splitter.active {
        background: var(--primary-color);
    }

/* Error Boundary Fallback */
.error-boundary-fallback {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100%;
    padding: var(--space-md);
    color: var(--text-muted);
    text-align: center;
}

    .error-boundary-fallback h3 {
        margin: 0 0 var(--space-sm) 0;
        color: var(--danger-color);
    }

    .error-boundary-fallback p {
        margin: 0 0 var(--space-md) 0;
        font-size: var(--font-size-sm);
    }

    .error-boundary-fallback .btn {
        background: var(--primary-color);
        border: none;
        color: white;
        padding: var(--space-xs) var(--space-sm);
        border-radius: 4px;
        cursor: pointer;
        font-size: var(--font-size-sm);
    }

        .error-boundary-fallback .btn:hover {
            background: var(--primary-hover);
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

### `Tagium.Photino.Blazor\Components\ModalHost.razor.css`

```css
.modal-backdrop {
        position: fixed;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
        background: rgba(0,0,0,0.7);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 1000;
    }

    .modal-container {
        background: #313131;
        border-radius: 8px;
        width: 500px;
        max-width: 90%;
        max-height: 80vh;
        display: flex;
        flex-direction: column;
        box-shadow: 0 4px 10px rgba(0,0,0,0.5);
    }

    .modal-header {
        padding: 15px;
        border-bottom: 1px solid #444;
        display: flex;
        justify-content: space-between;
        align-items: center;
    }

        .modal-header h3 {
            margin: 0;
            font-size: 1.2em;
            color: #eee;
        }

    .close-btn {
        background: none;
        border: none;
        color: #aaa;
        font-size: 1.5em;
        cursor: pointer;
    }

        .close-btn:hover {
            color: #fff;
        }

    .modal-body {
        padding: 15px;
        overflow-y: auto;
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

### `Tagium.Photino.Blazor\Components\TagSidebar.razor.css`

```css
/* ::deep combinator pierces CSS isolation */
::deep .sidebar-container {
    display: flex;
    flex-direction: column;
    height: 100%;
    color: var(--text-color);
}

::deep .tag-list {
    flex: 1;
    overflow-y: auto;
    padding: var(--space-xs);
}

/* ====== Pill Mode (Modern) ====== */
::deep .tag-item.pill {
    padding: 3px 8px;
    cursor: pointer;
    display: flex;
    align-items: center;
    border-radius: 12px;
    margin-bottom: 3px;
    color: var(--text-color);
    transition: background-color 0.1s, border-color 0.1s, color 0.1s;
    user-select: none;
    font-size: var(--font-size-sm);
    line-height: 1;
    border: 1px dashed;
}

    ::deep .tag-item.pill:hover {
        background: var(--surface-bg);
    }

    ::deep .tag-item.pill.active {
        color: white;
    }

    ::deep .tag-item.pill .count {
        font-size: 0.8em;
        margin-left: auto;
    }

    ::deep .tag-item.pill .item-emoji {
        margin-right: 6px;
    }

/* ====== Legacy Mode (Minimal Circles) ====== */
::deep .tag-item.legacy {
    padding: 3px 8px;
    cursor: pointer;
    display: flex;
    align-items: center;
    border-radius: 4px;
    margin-bottom: 2px;
    color: var(--text-color);
    transition: background-color 0.1s;
    user-select: none;
    font-size: var(--font-size-sm);
    line-height: 1;
    border: none;
}

    ::deep .tag-item.legacy:hover {
        background: var(--surface-bg);
    }

    ::deep .tag-item.legacy.active {
        background: var(--primary-color);
        color: white;
    }

    ::deep .tag-item.legacy .count {
        font-size: 0.8em;
        color: var(--text-muted);
        margin-left: auto;
    }

    ::deep .tag-item.legacy .tag-emoji {
        margin-right: 8px;
        width: 1.2em;
        text-align: center;
        font-size: var(--font-size-sm);
    }

/* ====== Shared Styles ====== */
::deep .system-item {
    border: none !important;
    font-size: var(--font-size-sm);
    line-height: 1;
    padding: 3px 8px;
    margin-bottom: 2px;
    display: flex;
    align-items: center;
    border-radius: 4px;
    cursor: pointer;
    user-select: none;
    transition: background-color 0.1s;
}

    ::deep .system-item.active {
        background: var(--primary-color) !important;
        color: white !important;
    }

::deep .icon {
    margin-right: 6px;
    display: inline-block;
    width: 1.2em;
    text-align: center;
    font-size: 1em;
}

::deep hr {
    border: 0;
    border-top: 1px solid var(--border-color);
    margin: var(--space-xs) 0;
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

    // State
    public AppView CurrentView { get; private set; } = AppView.Files;
    public string SearchText { get; private set; } = "";
    public bool IsMissingOnly { get; private set; }
    public HashSet<long> SelectedFileIds { get; private set; } = [];

    // Events
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

    public void SelectFile(long id, bool multiSelect, bool rangeSelect)
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

    public void SetSelection(IEnumerable<long> ids)
    {
        SelectedFileIds.Clear();

        foreach (long id in ids)
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

namespace Tagium.Photino.Blazor.Setup;

public static class SchemeHandlers
{
    // We need access to the repository to look up file paths by ID.
    // Since this is a static callback, we need a reference to the ServiceProvider.
    public static IServiceProvider? ServiceProvider { get; set; }

    public static Stream HandleTagiumScheme(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Uri uri = new(url);

        // 1. Thumbnail Handler
        if (uri.Host.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return HandleThumbnail(uri, out contentType);
        }

        // 2. Preview Handler
        return uri.Host.Equals("preview", StringComparison.OrdinalIgnoreCase) ? HandlePreview(uri, out contentType) : Stream.Null;
    }

    private static Stream HandleThumbnail(Uri uri, out string contentType)
    {
        contentType = "image/png";

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return Stream.Null;
        }

        string cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
        string path = Path.Combine(cacheDir, $"{fileId}.png");

        if (!File.Exists(path))
        {
            return null!; // 404
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static Stream HandlePreview(Uri uri, out string contentType)
    {
        contentType = "application/octet-stream";

        if (ServiceProvider is null)
        {
            return Stream.Null;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return Stream.Null;
        }

        // Resolve the file path from the database
        var fileRepo = ServiceProvider.GetRequiredService<FileRepository>();
        FileEntry? entry = fileRepo.GetById(fileId);

        if (entry is null || !File.Exists(entry.Path))
        {
            return null!; // 404
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
            ".mkv" => "video/x-matroska", // Browsers struggle with this, but WebView might try
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
using Tagium.Core.Services.LibraryItengrity;
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

### `Tagium.Photino.Blazor\wwwroot\index.html`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Tagium</title>
    <base href="/" />
    <link href="css/app.css" rel="stylesheet" />
    <!-- Required for Blazor CSS Isolation (MainLayout.razor.css, etc.) -->
    <link href="Tagium.styles.css" rel="stylesheet" />
    <style>
        html, body {
            height: 100%;
            margin: 0;
            overflow: hidden;
        }

        #app {
            height: 100%;
        }

        .drag-over::after {
            content: 'Drop files to add';
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            background: rgba(66, 165, 245, 0.3);
            border: 4px dashed #42a5f5;
            display: flex;
            justify-content: center;
            align-items: center;
            font-size: 2em;
            color: white;
            text-shadow: 0 2px 4px rgba(0,0,0,0.5);
            z-index: 9999;
            pointer-events: none;
            box-sizing: border-box;
        }

        /* Utility class used by layout.js during drag */
        .no-select {
            user-select: none;
        }
    </style>
</head>
<body>
    <div id="app">Loading...</div>
    <script src="_framework/blazor.webview.js"></script>
    <script src="js/dragdrop.js"></script>
    <script src="js/grid.js"></script>
    <script src="js/layout.js"></script>
</body>
</html>
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
                <button class="btn" @onclick="UpdateView">Retry</button>
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

    protected override void OnInitialized()
    {
        AppState.OnSelectionChange += OnSelectionChanged;
        Library.LibraryChanged += OnLibraryChanged;
        UpdateView();
    }

    private void OnSelectionChanged()
    {
        _ = InvokeAsync(UpdateView);
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(UpdateView);
    }

    private void UpdateView()
    {
        HasRenderError = false;
        try
        {
            int count = AppState.SelectedFileIds.Count;

            if (count == 0)
            {
                SelectionMode = InspectorMode.None;
                SelectedFile = null;
            }
            else if (count == 1)
            {
                long id = AppState.SelectedFileIds.First();
                SelectedFile = Library.GetFileById(id);

                if (SelectedFile is not null)
                {
                    SelectionMode = InspectorMode.Single;
                    AssignedTags = Library.GetTagsForFile(id);
                    AvailableTags = GetAvailableTags(AssignedTags);
                }
                else
                {
                    // Stale ID, treat as none
                    SelectionMode = InspectorMode.None;
                }
            }
            else
            {
                SelectionMode = InspectorMode.Multi;
                SelectedFile = null;
                CommonTags = Library.GetCommonTags(AppState.SelectedFileIds);
                AvailableTags = GetAvailableTags(CommonTags);
            }

            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Inspector] Critical Error: {ex}");
            HasRenderError = true;
            StateHasChanged();
        }
    }

    private List<Tag> GetAvailableTags(List<Tag> existingTags)
    {
        List<Tag> allTags = Library.GetTags();
        HashSet<long> existingIds = [.. existingTags.Select(t => t.Id)];
        return [.. allTags.Where(t => !existingIds.Contains(t.Id))];
    }

    public void Dispose()
    {
        AppState.OnSelectionChange -= OnSelectionChanged;
        Library.LibraryChanged -= OnLibraryChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\FileInspector.razor.css`

```css
.inspector-container {
    display: flex;
    flex-direction: column;
    height: 100%;
    background-color: var(--panel-bg);
    color: var(--text-color);
    border-left: 1px solid var(--border-color);
    min-width: 0; /* Allow container to shrink below intrinsic width */
}

.inspector-header {
    padding: 8px var(--space-sm);
    background: var(--surface-bg);
    border-bottom: 1px solid var(--border-color);
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    flex-shrink: 0;
}

.title {
    font-weight: 600;
    font-size: var(--font-size-xs);
    text-transform: uppercase;
    color: var(--text-muted);
}

.badge {
    font-size: var(--font-size-xs);
    background: rgba(255, 255, 255, 0.1);
    padding: 2px 6px;
    border-radius: 4px;
    color: var(--text-muted);
}

.inspector-content {
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden; /* Explicitly prevent horizontal scrolling */
    padding: var(--space-sm);
    position: relative;
    min-width: 0; /* Critical: allows flex item to shrink below content size */
}

.state-message {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: var(--text-muted);
    text-align: center;
    opacity: 0.7;
}

    .state-message .icon {
        font-size: 1.5em;
        margin-bottom: 8px;
    }

    .state-message.error {
        color: var(--danger-color);
        opacity: 1;
    }

.btn {
    background: var(--surface-bg);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    padding: 3px 8px;
    border-radius: 4px;
    cursor: pointer;
    margin-top: 8px;
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
@using Tagium.Core.Models
@using Tagium.Core.Services
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Components.Shared
@inject LibraryService Library
@inject AppState AppState

<h3>Bulk Edit</h3>
<p class="summary-text">@AppState.SelectedFileIds.Count items selected.</p>

<div class="actions-row">
    <button class="btn btn-danger" @onclick="RemoveMultipleFiles">Remove All Selected</button>
</div>

<hr />

<h4>Common Tags</h4>
<p class="help-text">Click to remove from all selected files.</p>
<div class="tags-container">
    @if (CommonTags.Any())
    {
        @foreach (var tag in CommonTags)
        {
            <span class="tag-pill"
                  style="border-color: @tag.ColorHex"
                  @onclick="() => BulkRemoveTag(tag.Id)">
                <TagIcon Tag="@tag" ShowIcon="false" />
                @tag.Name <span class="remove">&times;</span>
            </span>
        }
    }
    else
    {
        <span class="empty-tag">No common tags.</span>
    }
</div>

<hr />

<h4>Add Tag</h4>
<p class="help-text">Click to add to all selected files.</p>
<div class="tags-container available">
    @foreach (var tag in AvailableTags)
    {
        <span class="tag-pill add"
              style="border-color: @tag.ColorHex"
              @onclick="() => BulkAddTag(tag.Id)">
            <TagIcon Tag="@tag" ShowIcon="false" />
            @tag.Name
        </span>
    }
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

    private void BulkAddTag(long tagId)
    {
        Library.AddTagToFiles(AppState.SelectedFileIds, tagId);
    }

    private void BulkRemoveTag(long tagId)
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

### `Tagium.Photino.Blazor\Components\Inspectors\MultiFileInspector.razor.css`

```css
.summary-text {
    color: var(--text-muted);
    margin-bottom: 8px;
    font-size: var(--font-size-xs);
}

.help-text {
    font-size: var(--font-size-xs);
    color: var(--text-muted);
    margin: -4px 0 8px 0;
}

.actions-row {
    display: flex;
    gap: 4px;
    margin-bottom: 10px;
}

.btn {
    background: var(--surface-bg);
    color: var(--text-color);
    border: 1px solid var(--border-color);
    padding: 3px 8px;
    border-radius: 4px;
    cursor: pointer;
    font-size: var(--font-size-xs);
    flex: 1;
}

    .btn:hover {
        background: var(--border-color);
    }

    .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
    }

.btn-danger {
    background: var(--danger-color);
    border-color: var(--danger-color);
    color: white;
}

    .btn-danger:hover {
        background: var(--danger-hover);
    }

.tags-container {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
}

.empty-tag {
    color: var(--text-muted);
    font-style: italic;
    font-size: var(--font-size-xs);
}

.tag-pill {
    background: var(--surface-bg);
    padding: 3px 6px; /* Increased from 2px for better optical centering */
    border-radius: 12px;
    font-size: var(--font-size-xs);
    cursor: pointer;
    display: flex;
    align-items: center;
    user-select: none;
    border: 1px solid var(--border-color);
    transition: background-color 0.1s;
    line-height: 1; /* Force consistent baseline */
}

    .tag-pill:hover {
        background: var(--border-color);
    }

    .tag-pill .remove {
        margin-left: 4px;
        color: var(--danger-color);
        font-weight: bold;
        display: flex; /* Ensure perfect vertical alignment */
        align-items: center;
        line-height: 1;
    }

    .tag-pill.add {
        background: transparent;
        border-style: dashed;
        opacity: 0.8;
    }

        .tag-pill.add:hover {
            background: var(--surface-bg);
            border-style: solid;
            opacity: 1;
        }

/* Subtle separator styling */
hr {
    border: 0;
    height: 1px;
    background: rgba(255, 255, 255, 0.08);
    margin: 10px 0;
}

/* Natural, readable section headings */
h4 {
    font-size: var(--font-size-sm);
    font-weight: normal;
    color: var(--text-muted);
    margin: 0 0 8px 0;
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\SingleFileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors
@using Tagium.Core.Models
@using Tagium.Core.Services
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Components.Modals
@using Tagium.Photino.Blazor.Components.Shared
@inject LibraryService Library
@inject SystemLauncherService Launcher
@inject FileDialogService FileDialog
@inject AppState AppState
@inject ModalService ModalService

<div class="preview-container">
    @if (!File.IsMissing)
    {
        @if (IsImage)
        {
            <img src="tagium://preview/@File.Id" class="preview-media" />
        }
        else if (IsVideo)
        {
            <video src="tagium://preview/@File.Id" class="preview-media" controls autoplay muted loop></video>
        }
        else
        {
            <div class="preview-placeholder">📄</div>
        }
    }
    else
    {
        <div class="preview-placeholder error">❓</div>
    }
</div>

@if (File.IsMissing)
{
    <div class="missing-warning">
        <h4>⚠️ File Missing</h4>
        <p>This file was not found at its original path.</p>
        <button class="btn btn-primary btn-small" @onclick="RelocateFile">Relocate...</button>
    </div>
}

<div class="actions-row">
    <button class="btn" @onclick="OpenFile" disabled="@File.IsMissing">Open</button>
    <button class="btn" @onclick="ShowInExplorer" disabled="@File.IsMissing">Folder</button>
    <button class="btn btn-danger" @onclick="RemoveFile">Remove</button>
</div>

<div class="prop-row">
    <label>Name</label>
    <span>@File.Name</span>
</div>
<div class="prop-row">
    <label>Path</label>
    <span class="path">@File.Path</span>
</div>

<hr />

<h4>Tags</h4>
<div class="tags-container">
    @foreach (var tag in AssignedTags)
    {
        <span class="tag-pill"
              style="border-color: @tag.ColorHex"
              @onclick="() => RemoveTag(tag.Id)">
            <TagIcon Tag="@tag" ShowIcon="false" />
            @tag.Name <span class="remove">&times;</span>
        </span>
    }
</div>

<hr />

<h4>Add Tag</h4>
<div class="tags-container available">
    @foreach (var tag in AvailableTags)
    {
        <span class="tag-pill add"
              style="border-color: @tag.ColorHex"
              @onclick="() => AddTag(tag.Id)">
            <TagIcon Tag="@tag" ShowIcon="false" />
            @tag.Name
        </span>
    }
</div>

@code {
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

    private void AddTag(long tagId)
    {
        Library.AddTagToFile(File.Id, tagId);
    }

    private void RemoveTag(long tagId)
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

    private void RelocateFile()
    {
        var files = FileDialogService.OpenFiles();
        if (files is not null && files.Length > 0)
        {
            var result = Library.RelocateFile(File.Id, files[0]);

            if (result.Status == RelocateResultStatus.Duplicate && result.ConflictingFile is not null)
            {
                ModalService.Show("Duplicate File Found", @<DuplicateFileModal ExistingFile="result.ConflictingFile" />);
            }
        }
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\SingleFileInspector.razor.css`

```css
.preview-container {
    width: 100%;
    max-width: 100%; /* Prevent overflow in constrained flex containers */
    aspect-ratio: 16 / 9;
    background: #000;
    border-radius: 6px;
    margin-bottom: 10px;
    overflow: hidden;
    display: flex;
    justify-content: center;
    align-items: center;
    border: 1px solid var(--border-color);
}

.preview-media {
    max-width: 100%;
    max-height: 100%;
    object-fit: contain;
}

.preview-placeholder {
    font-size: 2em;
    color: var(--border-color);
}

    .preview-placeholder.error {
        color: var(--danger-color);
    }

.prop-row {
    margin-bottom: 8px;
}

    .prop-row label {
        display: block;
        font-size: var(--font-size-xs);
        color: var(--text-muted);
    }

.path {
    word-break: break-all;
    font-size: var(--font-size-xs);
    font-family: monospace;
    color: var(--text-muted);
}

.actions-row {
    display: flex;
    gap: 4px;
    margin-bottom: 10px;
}

.btn {
    background: var(--surface-bg);
    color: var(--text-color);
    border: 1px solid var(--border-color);
    padding: 3px 8px;
    border-radius: 4px;
    cursor: pointer;
    font-size: var(--font-size-xs);
    flex: 1;
}

    .btn:hover {
        background: var(--border-color);
    }

    .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
    }

.btn-danger {
    background: var(--danger-color);
    border-color: var(--danger-color);
    color: white;
}

    .btn-danger:hover {
        background: var(--danger-hover);
    }

.btn-primary {
    background: var(--primary-color);
    border-color: var(--primary-color);
    color: white;
}

    .btn-primary:hover {
        background: var(--primary-hover);
    }

.btn-small {
    font-size: var(--font-size-xs);
    padding: 2px 6px;
}

.missing-warning {
    background: rgba(198, 40, 40, 0.15);
    border: 1px solid var(--danger-color);
    padding: 8px;
    border-radius: 4px;
    margin-bottom: 10px;
}

    .missing-warning h4 {
        margin: 0 0 4px 0;
        color: var(--danger-color);
        font-size: var(--font-size-sm);
    }

    .missing-warning p {
        margin: 0 0 8px 0;
        font-size: var(--font-size-xs);
        color: var(--text-muted);
    }

.tags-container {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
}

.tag-pill {
    background: var(--surface-bg);
    padding: 3px 6px; /* Increased from 2px for better optical centering */
    border-radius: 12px;
    font-size: var(--font-size-xs);
    cursor: pointer;
    display: flex;
    align-items: center;
    user-select: none;
    border: 1px solid var(--border-color);
    transition: background-color 0.1s;
    line-height: 1; /* Force consistent baseline */
}

    .tag-pill:hover {
        background: var(--border-color);
    }

    .tag-pill .remove {
        margin-left: 4px;
        color: var(--danger-color);
        font-weight: bold;
        display: flex; /* Ensure perfect vertical alignment */
        align-items: center;
        line-height: 1;
    }

    .tag-pill.add {
        background: transparent;
        border-style: dashed;
        opacity: 0.8;
    }

        .tag-pill.add:hover {
            background: var(--surface-bg);
            border-style: solid;
            opacity: 1;
        }

/* Subtle separator styling */
hr {
    border: 0;
    height: 1px;
    background: rgba(255, 255, 255, 0.08);
    margin: 10px 0;
}

/* Natural, readable section headings */
h4 {
    font-size: var(--font-size-sm);
    font-weight: normal;
    color: var(--text-muted);
    margin: 0 0 8px 0;
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
@using Tagium.Core.Services.LibraryItengrity
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
using Tagium.Core.Services.LibraryItengrity;
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
            // This is a risky operation while the app is running, but standard for this simple app type.
            // SQLite usually allows it if no write transaction is active.
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

### `Tagium.Photino.Blazor\Components\Modals\DataManagementModal.razor.css`

```css
.data-tools {
        padding: 5px;
    }

    .section {
        margin-bottom: 15px;
    }

    h4 {
        margin: 0 0 5px 0;
        color: #eee;
    }

    .desc {
        font-size: 0.9em;
        color: #aaa;
        margin: 0 0 10px 0;
    }

    .btn {
        background: #444;
        border: 1px solid #555;
        color: white;
        padding: 6px 12px;
        border-radius: 4px;
        cursor: pointer;
    }

        .btn:hover {
            background: #555;
        }

        .btn:disabled {
            opacity: 0.5;
            cursor: not-allowed;
        }

    .btn-danger {
        background: #c62828;
        border-color: #b71c1c;
    }

        .btn-danger:hover {
            background: #d32f2f;
        }

    .btn-row {
        display: flex;
        gap: 10px;
    }

    .result-box {
        margin-top: 10px;
        padding: 8px;
        background: #222;
        border: 1px solid #333;
        border-radius: 4px;
        font-size: 0.9em;
        color: #ccc;
    }

    hr {
        border: 0;
        border-top: 1px solid #444;
        margin: 15px 0;
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
        // Clear filters to ensure the file is visible
        AppState.SetSearch("");
        AppState.SetMissingOnly(false);

        // Select the file
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

### `Tagium.Photino.Blazor\Components\Modals\DuplicateFileModal.razor.css`

```css
.duplicate-modal p {
        color: #eee;
        margin-top: 0;
    }

    .file-path {
        background: #222;
        padding: 10px;
        border: 1px solid #444;
        border-radius: 4px;
        font-family: monospace;
        word-break: break-all;
        margin: 15px 0;
        color: #aaa;
    }

    .footer {
        display: flex;
        justify-content: flex-end;
        gap: 10px;
        margin-top: 20px;
    }

    .btn {
        padding: 6px 15px;
        border-radius: 4px;
        border: none;
        cursor: pointer;
        background: #444;
        color: white;
    }

        .btn:hover {
            background: #555;
        }

    .btn-primary {
        background: #42a5f5;
    }

        .btn-primary:hover {
            background: #1e88e5;
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

### `Tagium.Photino.Blazor\Components\Modals\PreferencesModal.razor.css`

```css
.radio-group {
        display: flex;
        flex-direction: column;
        gap: 8px;
        margin-top: 10px;
    }

        .radio-group label {
            display: flex;
            align-items: center;
            gap: 8px;
            cursor: pointer;
            user-select: none;
        }

    .hint {
        font-size: 0.85em;
        color: #888;
        margin: -5px 0 10px 0;
    }

    .btn {
        background: #444;
        color: white;
        border: none;
        padding: 6px 12px;
        border-radius: 4px;
        cursor: pointer;
    }

        .btn:hover {
            background: #555;
        }
```

---

### `Tagium.Photino.Blazor\Components\Shared\FileDropZone.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Services
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject FileImportService FileImportService
@inject SettingsManager Settings

<div class="drop-zone-wrapper @(Settings.Current.EmojiPreference == EmojiSet.Apple ? "use-apple-emojis" : "")"
     @ondragenter="OnDragEnter"
     @ondragleave="OnDragLeave">

    @ChildContent

    <div class="drag-overlay @(_isDragging ? "active" : "")">
        <div class="drag-message">
            <div class="drag-icon">📂</div>
            <h2>@(_isProcessing ? "Processing..." : "Drop files to import")</h2>
        </div>
        @if (_isDragging || _isProcessing)
        {
            <InputFile OnChange="HandleFileDrop" multiple class="drag-input" />
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Shared\FileDropZone.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
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

        await Library.ImportBatchAsync(importedPaths);
    }

    public void Dispose()
    {
        Settings.SettingsChanged -= StateHasChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\FileDropZone.razor.css`

```css
.drop-zone-wrapper {
        position: relative;
        width: 100%;
        height: 100%;
        overflow: hidden;
    }

    .drag-overlay {
        position: absolute;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
        background: rgba(0, 0, 0, 0.85);
        z-index: 9999;
        display: flex;
        justify-content: center;
        align-items: center;
        opacity: 0;
        pointer-events: none;
        transition: opacity 0.2s;
    }

        .drag-overlay.active {
            opacity: 1;
            pointer-events: auto;
        }

    .drag-input {
        position: absolute;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
        opacity: 0;
        cursor: copy;
    }

    .drag-message {
        text-align: center;
        color: white;
    }

    .drag-icon {
        font-size: 3em;
        color: var(--primary-color);
        margin-bottom: 20px;
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

### `Tagium.Photino.Blazor\Components\Shared\IconButton.razor.css`

```css
.icon-button {
    background: transparent;
    border: none;
    color: var(--text-muted);
    font-size: 14px;
    width: var(--toolbar-icon-size);
    height: var(--toolbar-icon-size);
    display: flex;
    align-items: center;
    justify-content: center;
    border-radius: 3px;
    cursor: pointer;
    transition: color 0.1s, background-color 0.1s;
}

    .icon-button:hover {
        color: var(--text-color);
        background: var(--surface-hover);
    }

    .icon-button:disabled {
        opacity: 0.5;
        cursor: not-allowed;
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

### `Tagium.Photino.Blazor\Components\Shared\SearchInput.razor.css`

```css
.search-input {
    width: 100%;
    /* Ensure padding doesn't overflow width */
    box-sizing: border-box;
    /* Visual Styles moved from Toolbar */
    background: var(--bg-color);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    padding: 6px var(--space-sm);
    border-radius: 15px;
    font-size: var(--font-size-sm);
    transition: border-color 0.2s, background-color 0.2s;
}

    .search-input:focus {
        outline: none;
        border-color: var(--primary-color);
        background: #000;
    }

    .search-input::placeholder {
        color: var(--text-muted);
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

### `Tagium.Photino.Blazor\Components\Shared\SortControls.razor.css`

```css
.control-group {
    display: flex;
    align-items: center;
    gap: var(--space-xs);
}

select {
    background: var(--bg-color);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    padding: 2px 6px;
    border-radius: 3px;
    font-size: var(--font-size-sm);
    height: 24px;
    outline: none;
}

    select:hover {
        border-color: #555;
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

### `Tagium.Photino.Blazor\Components\Shared\TagIcon.razor.css`

```css
.tag-emoji {
        display: inline-block;
        width: 1.2em;
        text-align: center;
        margin-right: 6px;
        line-height: 1;
        font-size: 1.1em;
    }

    .tag-color {
        display: inline-block;
        width: 8px;
        height: 8px;
        border-radius: 50%;
        margin-right: 8px;
        flex-shrink: 0;
        border: 1px solid rgba(255,255,255,0.1);
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

### `Tagium.Photino.Blazor\Components\Shared\ThumbnailSizeSlider.razor.css`

```css
.control-group {
    display: flex;
    align-items: center;
    gap: var(--space-xs);
}

input[type=range] {
    -webkit-appearance: none;
    width: 100px;
    background: transparent;
    margin: 0;
    height: 20px;
    cursor: pointer;
    vertical-align: middle;
}

    input[type=range]::-webkit-slider-runnable-track {
        width: 100%;
        height: 4px;
        cursor: pointer;
        background: #444;
        border-radius: 2px;
    }

    input[type=range]::-webkit-slider-thumb {
        -webkit-appearance: none;
        height: 12px;
        width: 12px;
        border-radius: 50%;
        background: var(--primary-color);
        margin-top: -4px;
        cursor: pointer;
        box-shadow: 0 0 2px rgba(0,0,0,0.5);
        transition: transform 0.1s;
    }

        input[type=range]::-webkit-slider-thumb:hover {
            transform: scale(1.2);
            background: var(--primary-hover);
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
}
```

---

### `Tagium.Photino.Blazor\Components\Views\FilesViewToolbar.razor.css`

```css
.files-view-toolbar {
    display: flex;
    align-items: center;
    padding: var(--space-sm) var(--space-md);
    background: var(--panel-bg);
    border-bottom: 1px solid var(--border-color);
    gap: var(--space-sm);
    flex-shrink: 0;
    position: relative;
    z-index: 20;
}

.search-container {
    flex: 1;
    min-width: 0;
}

.view-options-wrapper {
    position: relative;
    display: flex;
    align-items: center;
}

.btn-icon {
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-muted);
    width: 30px;
    height: 30px;
    border-radius: 4px;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 1.2em;
    transition: background 0.1s;
}

    .btn-icon:hover {
        background: var(--surface-hover);
        color: var(--text-color);
    }

    .btn-icon.active {
        background: var(--surface-hover);
        color: var(--primary-color);
        border-color: var(--border-color);
    }

/* Dropdown Menu */
.view-menu-dropdown {
    position: absolute;
    top: 100%;
    right: 0;
    margin-top: 8px;
    background: var(--panel-bg);
    border: 1px solid var(--border-color);
    border-radius: 6px;
    padding: 10px;
    box-shadow: 0 4px 12px rgba(0,0,0,0.5);
    z-index: 1001;
    min-width: 200px;
    animation: fadeIn 0.1s ease-out;
}

.view-menu-overlay {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 100vh;
    z-index: 1000;
}

.menu-section {
    display: flex;
    flex-direction: column;
    gap: 5px;
}

    .menu-section label {
        font-size: 0.8em;
        color: var(--text-muted);
        font-weight: 600;
        text-transform: uppercase;
    }

hr {
    border: 0;
    border-top: 1px solid var(--border-color);
    margin: 10px 0;
}

@keyframes fadeIn {
    from {
        opacity: 0;
        transform: translateY(-5px);
    }

    to {
        opacity: 1;
        transform: translateY(0);
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
    private long? ActiveColorPickerId = null;

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

    private void DeleteTag(long id)
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

    private void ToggleColorPicker(long id)
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

### `Tagium.Photino.Blazor\Components\Views\TagManagerView.razor.css`

```css
.tag-manager-view {
    height: 100%;
    display: flex;
    flex-direction: column;
    padding: var(--space-md);
    background: var(--bg-color);
    color: var(--text-color);
}

.header-row {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: var(--space-sm);
    border-bottom: 1px solid var(--border-color);
    padding-bottom: var(--space-sm);
}

    .header-row h3 {
        margin: 0;
        color: var(--text-color);
        font-size: var(--font-size-sm);
    }

.add-row {
    display: flex;
    gap: var(--space-xs);
}

    .add-row input {
        padding: 4px var(--space-xs);
        background: var(--surface-bg);
        border: 1px solid var(--border-color);
        color: var(--text-color);
        border-radius: 4px;
        flex: 1;
        font-size: var(--font-size-sm);
    }

        .add-row input:focus {
            outline: none;
            border-color: var(--primary-color);
        }

.btn-primary {
    background: var(--primary-color);
    border: none;
    color: white;
    padding: 0 12px;
    border-radius: 4px;
    cursor: pointer;
    font-size: var(--font-size-xs);
    height: 24px;
}

    .btn-primary:hover {
        background: var(--primary-hover);
    }

.sort-controls {
    display: flex;
    gap: var(--space-xs);
    margin-bottom: var(--space-sm);
    padding: var(--space-xs);
    background: var(--panel-bg);
    border-radius: 4px;
}

    .sort-controls select {
        background: var(--bg-color);
        border: 1px solid var(--border-color);
        color: var(--text-color);
        padding: 2px 6px;
        border-radius: 3px;
        font-size: var(--font-size-sm);
        outline: none;
    }

        .sort-controls select:hover {
            border-color: #555;
        }

.tag-list {
    flex: 1;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: 2px;
}

.tag-row {
    display: flex;
    align-items: center;
    padding: 4px var(--space-sm);
    background: var(--surface-bg);
    border-radius: 4px;
    gap: var(--space-xs);
    transition: background 0.1s;
}

    .tag-row:hover {
        background: var(--panel-bg);
    }

.color-picker-wrapper {
    position: relative;
    display: flex;
    align-items: center;
}

.color-circle {
    width: 18px;
    height: 18px;
    border-radius: 50%;
    border: 2px solid var(--border-color);
    cursor: pointer;
    display: block;
}

.color-popup {
    position: absolute;
    top: 25px;
    left: 0;
    background: var(--panel-bg);
    border: 1px solid var(--border-color);
    padding: 6px;
    z-index: 1000;
    border-radius: 6px;
    width: 140px;
    box-shadow: 0 4px 12px rgba(0,0,0,0.5);
}

.color-grid {
    display: flex;
    flex-wrap: wrap;
    gap: 3px;
    position: relative;
    z-index: 2;
}

.swatch {
    width: 16px;
    height: 16px;
    border-radius: 3px;
    cursor: pointer;
    border: 1px solid rgba(255,255,255,0.1);
}

    .swatch:hover {
        border-color: white;
        transform: scale(1.1);
    }

.overlay {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 100vh;
    z-index: 1;
}

.emoji-input {
    width: 24px;
    text-align: center;
    background: var(--bg-color);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    border-radius: 4px;
    padding: 2px 4px;
    font-size: var(--font-size-sm);
}

    .emoji-input:focus {
        outline: none;
        border-color: var(--primary-color);
    }

.name-input {
    flex: 1;
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-color);
    padding: 3px var(--space-xs);
    border-radius: 4px;
    font-size: var(--font-size-sm);
}

    .name-input:focus {
        background: var(--bg-color);
        border-color: var(--border-color);
    }

.count {
    color: var(--text-muted);
    font-size: var(--font-size-xs);
    width: 60px;
    text-align: right;
}

.btn-icon.delete {
    background: none;
    border: none;
    color: var(--danger-color);
    font-size: 1.2em;
    cursor: pointer;
    padding: 0 var(--space-xs);
    line-height: 1;
    display: flex;
    align-items: center;
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

### `Tagium.Photino.Blazor\wwwroot\css\app.css`

```css
:root {
    /* Global Color Palette */
    --bg-color: #181818;
    --panel-bg: #202020;
    --surface-bg: #2D2D2D;
    --surface-hover: rgba(255, 255, 255, 0.05);
    --border-color: #383838;
    --text-color: #E0E0E0;
    --text-muted: #9E9E9E;
    --primary-color: #42a5f5;
    --primary-hover: #1e88e5;
    --danger-color: #d32f2f; /* Vibrant red */
    --danger-hover: #b71c1c; /* Deeper red on hover */
    /* Spacing & Sizing Tokens */
    --space-xs: 4px;
    --space-sm: 8px;
    --space-md: 16px;
    --toolbar-height: 38px;
    --toolbar-icon-size: 28px;
    --font-size-xs: 11px;
    --font-size-sm: 12px;
    /* Responsive Breakpoints */
    --breakpoint-sm: 700px;
}

/* Rest of file remains unchanged */
@font-face {
    font-family: 'Apple Emoji';
    src: url('../fonts/AppleColorEmoji.ttf');
}

body {
    font-family: 'Segoe UI', 'Segoe UI Emoji', sans-serif;
    background-color: var(--bg-color);
    color: var(--text-color);
    margin: 0;
    padding: 0;
    height: 100%;
    overflow: hidden;
}

#app {
    height: 100%;
}

.drag-over::after {
    content: 'Drop files to add';
    position: absolute;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    background: rgba(66, 165, 245, 0.3);
    border: 4px dashed #42a5f5;
    display: flex;
    justify-content: center;
    align-items: center;
    font-size: 2em;
    color: white;
    text-shadow: 0 2px 4px rgba(0,0,0,0.5);
    z-index: 9999;
    pointer-events: none;
    box-sizing: border-box;
}

.no-select {
    user-select: none;
}

/* Custom Scrollbar Styling (WebKit) */
::-webkit-scrollbar {
    width: 12px;
    height: 12px;
}

::-webkit-scrollbar-track {
    background: var(--bg-color);
}

::-webkit-scrollbar-thumb {
    background: var(--border-color);
    border-radius: 6px;
    border: 3px solid var(--bg-color);
}

    ::-webkit-scrollbar-thumb:hover {
        background: var(--text-muted);
    }

    ::-webkit-scrollbar-thumb:active {
        background: var(--primary-color);
    }

::-webkit-scrollbar-corner {
    background: var(--bg-color);
}

/* Import Progress Overlay */
.import-progress-overlay {
    position: fixed;
    bottom: 60px;
    left: 20px;
    right: 20px;
    height: 4px;
    background: var(--border-color);
    border-radius: 2px;
    z-index: 9998;
}

.import-progress-bar {
    height: 100%;
    background: var(--primary-color);
    border-radius: 2px;
    transition: width 0.2s ease;
}

.import-progress-text {
    position: absolute;
    top: -20px;
    left: 0;
    font-size: var(--font-size-xs);
    color: var(--text-muted);
}
```

---

### `Tagium.Photino.Blazor\wwwroot\js\dragdrop.js`

```javascript
window.dragDrop = {
    init: (dotNetHelper) => {
        const dropZone = document.getElementById('app');

        // Prevent default browser actions (like opening the file in the tab)
        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, preventDefaults, false);
        });

        function preventDefaults(e) {
            e.preventDefault();
            e.stopPropagation();
        }

        // Highlight visual feedback
        ['dragenter', 'dragover'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => dropZone.classList.add('drag-over'), false);
        });

        ['dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => dropZone.classList.remove('drag-over'), false);
        });

        // We do NOT handle the data transfer here.
        // The C# Native Window Handler catches the files before JS sees them.
    }
};
```

---

### `Tagium.Photino.Blazor\wwwroot\js\grid.js`

```javascript
window.gridInteractions = {
    dotNetRef: null,
    containerId: '',
    selectionBox: null,
    isSelecting: false,
    startX: 0,
    startY: 0,

    init: function (dotNetRef, containerId) {
        this.dotNetRef = dotNetRef;
        this.containerId = containerId;

        // Create selection box element if it doesn't exist
        if (!document.getElementById('selection-box')) {
            this.selectionBox = document.createElement('div');
            this.selectionBox.id = 'selection-box';
            this.selectionBox.style.position = 'absolute';
            this.selectionBox.style.border = '1px solid #42a5f5';
            this.selectionBox.style.backgroundColor = 'rgba(66, 165, 245, 0.2)';
            this.selectionBox.style.display = 'none';
            this.selectionBox.style.zIndex = '9999';
            this.selectionBox.style.pointerEvents = 'none';
            document.body.appendChild(this.selectionBox);
        } else {
            this.selectionBox = document.getElementById('selection-box');
        }

        // Attach global events
        document.addEventListener('keydown', this.handleKeyDown.bind(this));

        const container = document.getElementById(containerId);
        if (container) {
            container.addEventListener('mousedown', this.handleMouseDown.bind(this));
        }
    },

    handleKeyDown: function (e) {
        // Ignore if focus is in an input
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;

        if (e.key === 'Delete') {
            this.dotNetRef.invokeMethodAsync('OnDeleteKey');
        } else if (e.key === 'Enter') {
            this.dotNetRef.invokeMethodAsync('OnEnterKey');
        } else if (e.key.startsWith('Arrow')) {
            e.preventDefault();
            this.navigate(e.key);
        } else if (e.key === 'a' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            this.dotNetRef.invokeMethodAsync('SelectAll');
        }
    },

    navigate: function (direction) {
        const items = Array.from(document.querySelectorAll('.file-card'));
        if (items.length === 0) return;

        const selected = document.querySelector('.file-card.selected');
        let index = selected ? items.indexOf(selected) : -1;

        if (index === -1) {
            index = 0; // Default to first
        } else {
            const containerWidth = document.getElementById(this.containerId).clientWidth;
            const itemWidth = items[0].getBoundingClientRect().width + 10; // + gap
            const cols = Math.floor(containerWidth / itemWidth) || 1;

            if (direction === 'ArrowRight') index++;
            else if (direction === 'ArrowLeft') index--;
            else if (direction === 'ArrowDown') index += cols;
            else if (direction === 'ArrowUp') index -= cols;
        }

        // Clamp
        if (index < 0) index = 0;
        if (index >= items.length) index = items.length - 1;

        // Get ID
        const id = items[index].getAttribute('data-id');
        items[index].scrollIntoView({ block: 'nearest' });

        this.dotNetRef.invokeMethodAsync('SelectFile', parseInt(id));
    },

    handleMouseDown: function (e) {
        // Ignore clicks on items (handled by Blazor onclick)
        if (e.target.closest('.file-card')) return;

        // Detect if click is on scrollbar
        // If the target is the container itself, check if click is outside client area
        if (e.target.id === this.containerId) {
            const rect = e.target.getBoundingClientRect();
            const isVerticalScrollbar = e.clientX > rect.left + e.target.clientWidth;
            const isHorizontalScrollbar = e.clientY > rect.top + e.target.clientHeight;

            if (isVerticalScrollbar || isHorizontalScrollbar) {
                return;
            }
        }

        e.preventDefault();
        this.isSelecting = true;
        this.startX = e.pageX;
        this.startY = e.pageY;

        this.selectionBox.style.display = 'block';
        this.updateBox(e.pageX, e.pageY);

        document.addEventListener('mousemove', this.boundMouseMove = this.handleMouseMove.bind(this));
        document.addEventListener('mouseup', this.boundMouseUp = this.handleMouseUp.bind(this));
    },

    handleMouseMove: function (e) {
        if (!this.isSelecting) return;
        this.updateBox(e.pageX, e.pageY);
    },

    updateBox: function (currentX, currentY) {
        const left = Math.min(this.startX, currentX);
        const top = Math.min(this.startY, currentY);
        const width = Math.abs(currentX - this.startX);
        const height = Math.abs(currentY - this.startY);

        this.selectionBox.style.left = left + 'px';
        this.selectionBox.style.top = top + 'px';
        this.selectionBox.style.width = width + 'px';
        this.selectionBox.style.height = height + 'px';
    },

    handleMouseUp: function (e) {
        this.isSelecting = false;
        this.selectionBox.style.display = 'none';

        document.removeEventListener('mousemove', this.boundMouseMove);
        document.removeEventListener('mouseup', this.boundMouseUp);

        // Calculate intersection
        const boxRect = {
            left: Math.min(this.startX, e.pageX),
            top: Math.min(this.startY, e.pageY),
            right: Math.max(this.startX, e.pageX),
            bottom: Math.max(this.startY, e.pageY)
        };

        const ids = [];
        document.querySelectorAll('.file-card').forEach(item => {
            const rect = item.getBoundingClientRect();
            // Adjust rect to page coordinates
            const pageRect = {
                left: rect.left + window.scrollX,
                top: rect.top + window.scrollY,
                right: rect.right + window.scrollX,
                bottom: rect.bottom + window.scrollY
            };

            const intersect = !(boxRect.right < pageRect.left ||
                boxRect.left > pageRect.right ||
                boxRect.bottom < pageRect.top ||
                boxRect.top > pageRect.bottom);

            if (intersect) {
                ids.push(parseInt(item.getAttribute('data-id')));
            }
        });

        if (ids.length > 0) {
            this.dotNetRef.invokeMethodAsync('SelectFiles', ids);
        } else {
            this.dotNetRef.invokeMethodAsync('ClearSelection');
        }
    }
};
```

---

### `Tagium.Photino.Blazor\wwwroot\js\layout.js`

```javascript
window.layoutResizer = {
    init: function (leftPanelId, leftSplitterId, rightPanelId, rightSplitterId) {
        const leftPanel = document.getElementById(leftPanelId);
        const rightPanel = document.getElementById(rightPanelId);
        const leftSplitter = document.getElementById(leftSplitterId);
        const rightSplitter = document.getElementById(rightSplitterId);

        if (leftSplitter && leftPanel) {
            setupResizer(leftSplitter, leftPanel, 'left');
        }

        if (rightSplitter && rightPanel) {
            setupResizer(rightSplitter, rightPanel, 'right');
        }

        function setupResizer(splitter, panel, side) {
            splitter.addEventListener('mousedown', function (e) {
                e.preventDefault();
                
                // Add active class for styling
                splitter.classList.add('active');
                document.body.style.cursor = 'col-resize';
                document.body.classList.add('no-select'); // Prevent text selection during drag

                function onMouseMove(e) {
                    if (side === 'left') {
                        // Left panel width is simply mouse X
                        // Clamp between 150px and 500px
                        const newWidth = Math.min(Math.max(e.clientX, 150), 600);
                        panel.style.width = newWidth + 'px';
                    } else {
                        // Right panel width is Window Width - Mouse X
                        // Clamp between 200px and 600px
                        const newWidth = Math.min(Math.max(window.innerWidth - e.clientX, 200), 800);
                        panel.style.width = newWidth + 'px';
                    }
                }

                function onMouseUp() {
                    splitter.classList.remove('active');
                    document.body.style.cursor = '';
                    document.body.classList.remove('no-select');
                    
                    document.removeEventListener('mousemove', onMouseMove);
                    document.removeEventListener('mouseup', onMouseUp);
                }

                document.addEventListener('mousemove', onMouseMove);
                document.addEventListener('mouseup', onMouseUp);
            });
        }
    }
};
```

---

