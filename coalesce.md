### `Tagium.Core\Tagium.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.1" />
    <PackageReference Include="SkiaSharp" Version="3.119.1" />
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

        string schema = @"
            CREATE TABLE IF NOT EXISTS Tags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
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

    public void Add(string path, string hash)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Files (Path, Name, Hash, DateAdded) 
            VALUES (@Path, @Name, @Hash, strftime('%s','now'))";

        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.Parameters.AddWithValue("@Name", Path.GetFileName(path));
        _ = cmd.Parameters.AddWithValue("@Hash", hash);
        try { _ = cmd.ExecuteNonQuery(); } catch { /* Ignore duplicates */ }
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

namespace Tagium.Core.Data;

public class TagRepository
{
    private readonly DatabaseManager _db;

    public TagRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<Tag> GetAll()
    {
        List<Tag> tags = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount
            FROM Tags T
            LEFT JOIN FileTags FT ON T.Id = FT.TagId
            GROUP BY T.Id
            ORDER BY T.Name";

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

    public void Delete(long id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tags WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id);
        _ = cmd.ExecuteNonQuery();
    }

    // Future: Update methods
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

    // Loaded on demand
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
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class LibraryService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ThumbnailService _thumbnailService;

    // Events for UI reactivity
    public event Action? LibraryChanged;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ThumbnailService thumbnailService)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _thumbnailService = thumbnailService;
    }

    public List<FileEntry> GetFiles(SortBy sortBy, SortDirection direction)
    {
        var files = _fileRepo.GetAll(sortBy, direction);
        // Optimization: Don't load tags for grid view unless necessary?
        // For now, simple approach:
        return files;
    }

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public void AddFile(string path)
    {
        // Simple hash placeholder for now
        string hash = Guid.NewGuid().ToString();
        _fileRepo.Add(path, hash);
        LibraryChanged?.Invoke();
    }

    public void EnsureThumbnails(IEnumerable<FileEntry> files)
    {
        foreach (var file in files)
        {
            if (_thumbnailService.HasThumbnail(file.Id))
            {
                continue;
            }
            _ = _thumbnailService.GenerateThumbnailAsync(file).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    // Optional: granular update event
                }
            });
        }
    }
}
```

---

### `Tagium.Core\Services\ThumbnailService.cs`

```csharp
using SkiaSharp;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ThumbnailService
{
    private readonly string _cacheDir;

    public ThumbnailService()
    {
        _cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
    }

    public string GetThumbnailPath(long fileId)
    {
        return Path.Combine(_cacheDir, $"{fileId}.png");
    }

    public bool HasThumbnail(long fileId)
    {
        return File.Exists(GetThumbnailPath(fileId));
    }

    [Obsolete]
    public async Task GenerateThumbnailAsync(FileEntry file, int size = 128)
    {
        string destPath = GetThumbnailPath(file.Id);
        if (File.Exists(destPath))
        {
            return;
        }

        // Run on thread pool to avoid blocking UI
        await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(file.Path))
                {
                    return;
                }

                using SKBitmap? original = SKBitmap.Decode(file.Path);

                if (original is null)
                {
                    return;
                }

                // Resize
                SKImageInfo info = new(size, size);
                using SKBitmap? resized = original.Resize(info, SKFilterQuality.Medium);

                if (resized is null)
                {
                    return;
                }

                using SKImage image = SKImage.FromBitmap(resized);
                using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);

                using FileStream stream = File.OpenWrite(destPath);
                data.SaveTo(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Thumbnail] Error generating for {file.Path}: {ex.Message}");
            }
        });
    }
}
```

---

### `Tagium.Core\Settings\EmojiSet.cs`

```csharp
namespace Tagium.Core.Settings;

public enum EmojiSet { SystemDefault, Apple }
```

---

### `Tagium.Core\Settings\SortBy.cs`

```csharp
namespace Tagium.Core.Settings;

public enum SortBy { Path, Name, DateAdded }
```

---

### `Tagium.Core\Settings\SortDirection.cs`

```csharp
namespace Tagium.Core.Settings;

public enum SortDirection { Asc, Desc }
```

---

### `Tagium.Core\Settings\TagDisplayMode.cs`

```csharp
namespace Tagium.Core.Settings;

public enum TagDisplayMode { ColorCircle, Emoji }
```

---

### `Tagium.Core\Settings\TagiumSettings.cs`

```csharp
namespace Tagium.Core.Settings;

public class TagiumSettings
{
    public TagDisplayMode TagDisplay { get; set; } = TagDisplayMode.ColorCircle;
    public EmojiSet EmojiPreference { get; set; } = EmojiSet.SystemDefault;
    public int ThumbnailSize { get; set; } = 120;
    public SortBy FileSortBy { get; set; } = SortBy.DateAdded;
    public SortDirection FileSortDirection { get; set; } = SortDirection.Desc;
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
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

        // Register Services
        ServiceRegistrar.ConfigureServices(appBuilder.Services);

        // Register Root Component
        // "App" is now explicitly in the Tagium.Photino.Blazor namespace (via @namespace in razor file)
        // This guarantees it is found regardless of file folder structure.
        appBuilder.RootComponents.Add<App>("#app");

        var app = appBuilder.Build();

        // Custom Scheme for Thumbnails
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
    <PackageReference Include="Photino.Blazor" Version="3.2.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
    <PackageReference Include="NativeFileDialogs.Net" Version="1.2.1" />
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

### `Tagium.Photino.Blazor\Components\MainLayout.razor`

```
@inherits LayoutComponentBase
@namespace Tagium.Photino.Blazor.Components

<div class="app-container">
    <div class="sidebar">
        <h3>Tagium</h3>
        <!-- Navigation will be added here -->
    </div>
    <div class="main-content">
        @Body
    </div>
</div>

<style>
    body, html {
        margin: 0;
        padding: 0;
        height: 100%;
        font-family: 'Segoe UI', sans-serif;
        background: #252525;
        color: #fff;
    }

    .app-container {
        display: flex;
        height: 100vh;
        overflow: hidden;
    }

    .sidebar {
        width: 250px;
        background: #313131;
        padding: 10px;
        display: flex;
        flex-direction: column;
        border-right: 1px solid #444;
    }

    .main-content {
        flex: 1;
        padding: 20px;
        overflow-y: auto;
    }
</style>
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
@using Tagium.Core.Models
@using Tagium.Core.Services
```

---

### `Tagium.Photino.Blazor\Setup\SchemeHandlers.cs`

```csharp
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Setup;

public static class SchemeHandlers
{
    public static Stream HandleTagiumScheme(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Uri uri = new(url);

        // Format: tagium://thumbnail/{fileId}
        if (!uri.Host.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return new MemoryStream();
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');

        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return new MemoryStream();
        }
        // We need to resolve the service from the app container. 
        // Since this handler is static and outside DI scope, we rely on the fact 
        // that Photino doesn't easily expose the provider here. 
        // However, we can access singleton services if we store the provider in Program. 
        // For simplified migration, we will instantiate a new ThumbnailService helper or 
        // assume the cache path logic is static/safe.

        // Better approach: Re-instantiate the path helper since it's stateless file IO.
        ThumbnailService thumbnailService = new();
        string path = thumbnailService.GetThumbnailPath(fileId);

        if (!File.Exists(path))
        {
            return new MemoryStream();
        }

        contentType = "image/png";
        return File.OpenRead(path);
    }
}
```

---

### `Tagium.Photino.Blazor\Setup\ServiceRegister.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Setup;

public static class ServiceRegistrar
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Data Layer
        _ = services.AddSingleton<DatabaseManager>();
        _ = services.AddSingleton<TagRepository>();
        _ = services.AddSingleton<FileRepository>();
        _ = services.AddSingleton<FileTagRepository>();

        // Service Layer
        _ = services.AddSingleton<ThumbnailService>();
        _ = services.AddSingleton<LibraryService>();
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
    <style>
        /* Base styles if needed */
    </style>
</head>
<body>
    <div id="app">Loading...</div>
    <script src="_framework/blazor.webview.js"></script>
</body>
</html>
```

---

