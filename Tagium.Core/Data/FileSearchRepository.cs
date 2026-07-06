using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class FileSearchRepository
{
    private readonly DatabaseManager _db;
    private readonly SearchQueryCompiler _compiler;
    private readonly ILogger<FileSearchRepository> _logger;

    public FileSearchRepository(DatabaseManager db, SearchQueryCompiler compiler, ILogger<FileSearchRepository> logger)
    {
        _db = db;
        _compiler = compiler;
        _logger = logger;
    }

    public List<FileEntry> Search(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
        using LogTiming timer = LogTiming.StartDebug(_logger, "Search database execution for compiled query: {Query}", searchText);

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        CompiledQuery compiled = _compiler.Compile(searchText, sortBy, sortDirection);

        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = compiled.Sql;

        foreach (var (name, value) in compiled.Parameters)
        {
            _ = cmd.Parameters.AddWithValue(name, value);
        }

        _logger.LogDebug("Executing SQL Query: {Sql}", cmd.CommandText);

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

        _logger.LogInformation("Database query execution completed, returned {Count} items", files.Count);
        return files;
    }
}