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
        using SqliteConnection conn = _db.GetConnection();

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
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO LibraryRoots (Id, Path, IsEnabled, LastScanTime) VALUES (@Id, @Path, 1, NULL)";
        _ = cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.ExecuteNonQuery();
    }

    public void Update(LibraryRoot root)
    {
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE LibraryRoots SET IsEnabled = @IsEnabled, LastScanTime = @LastScanTime WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", root.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@IsEnabled", root.IsEnabled ? 1 : 0);
        _ = cmd.Parameters.AddWithValue("@LastScanTime", (object?)root.LastScanTime ?? DBNull.Value);
        _ = cmd.ExecuteNonQuery();
    }

    public void Delete(Guid id, string path)
    {
        using SqliteConnection conn = _db.GetConnection();
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
                cmd.CommandText = $"DELETE FROM cache.Files WHERE Path = @Path OR SUBSTR(Path, 1, LENGTH(@Path) + 1) = @Path || '{Path.DirectorySeparatorChar}'";
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