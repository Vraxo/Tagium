using Dapper;
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

        DynamicParameters dynamicParams = new();
        foreach (var (name, value) in compiled.Parameters)
        {
            dynamicParams.Add(name, value);
        }

        using SqliteConnection conn = _db.GetConnection();
        _logger.LogDebug("Executing SQL Query: {Sql}", compiled.Sql);

        List<FileEntry> files = conn.Query<FileEntry>(compiled.Sql, dynamicParams).ToList();

        _logger.LogInformation("Database query execution completed, returned {Count} items", files.Count);
        return files;
    }
}