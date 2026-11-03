using Dapper;
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
        using SqliteConnection conn = _db.GetConnection();
        return conn.Query<LibraryRoot>("SELECT Id, Path, IsEnabled, LastScanTime FROM LibraryRoots ORDER BY Path ASC").ToList();
    }

    public void Add(string path)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("INSERT OR IGNORE INTO LibraryRoots (Id, Path, IsEnabled, LastScanTime) VALUES (@Id, @Path, 1, NULL)", new { Id = Guid.NewGuid(), Path = path });
    }

    public void Update(LibraryRoot root)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("UPDATE LibraryRoots SET IsEnabled = @IsEnabled, LastScanTime = @LastScanTime WHERE Id = @Id", root);
    }

    public void Delete(Guid id, string path)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteTransaction trans = conn.BeginTransaction();

        try
        {
            conn.Execute("DELETE FROM LibraryRoots WHERE Id = @Id", new { Id = id }, trans);
            conn.Execute($"DELETE FROM cache.Files WHERE Path = @Path OR SUBSTR(Path, 1, LENGTH(@Path) + 1) = @Path || '{Path.DirectorySeparatorChar}'", new { Path = path }, trans);
            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }
}