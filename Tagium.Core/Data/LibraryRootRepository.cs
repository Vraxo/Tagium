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
        cmd.CommandText = "SELECT Id, Path, IsEnabled, LastScanTime FROM LibraryRoots";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            roots.Add(new LibraryRoot
            {
                Id = Guid.Parse(reader.GetString(0)),
                Path = reader.GetString(1),
                IsEnabled = reader.GetBoolean(2),
                LastScanTime = reader.IsDBNull(3) ? null : reader.GetInt64(3)
            });
        }
        return roots;
    }

    public void Add(string path)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO LibraryRoots (Id, Path) VALUES (@Id, @Path)";
        _ = cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.ExecuteNonQuery();
    }

    public void Remove(Guid id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LibraryRoots WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public void UpdateStatus(Guid id, bool isEnabled)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE LibraryRoots SET IsEnabled = @Enabled WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Enabled", isEnabled ? 1 : 0);
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }
}