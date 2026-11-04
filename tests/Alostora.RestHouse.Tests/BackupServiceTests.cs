using Alostora.RestHouse;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root;

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void BackupAndRestorePreserveLocalBrandingAndAnnouncements()
    {
        var database = Path.Combine(_root, "resthouse.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Sample(Id INTEGER PRIMARY KEY); INSERT INTO Sample DEFAULT VALUES;";
            command.ExecuteNonQuery();
        }

        var posters = Path.Combine(_root, "posters");
        Directory.CreateDirectory(posters);
        File.WriteAllText(Path.Combine(_root, "profile.json"), "{\"RestHouseName\":\"قبل النسخة\"}");
        File.WriteAllText(Path.Combine(_root, "announcement.json"), "{\"Title\":\"تنبيه قديم\"}");
        var archive = Path.Combine(_root, "Backups", "test.zip");
        BackupService.CreateArchive(database, posters, archive);

        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.Contains(zip.Entries, x => x.FullName == "profile.json");
            Assert.Contains(zip.Entries, x => x.FullName == "announcement.json");
        }

        File.WriteAllText(Path.Combine(_root, "profile.json"), "{\"RestHouseName\":\"بعد النسخة\"}");
        File.WriteAllText(Path.Combine(_root, "announcement.json"), "{\"Title\":\"تنبيه جديد\"}");
        BackupService.RestoreArchive(database, posters, archive, _root);

        Assert.Contains("قبل النسخة", File.ReadAllText(Path.Combine(_root, "profile.json")));
        Assert.Contains("تنبيه قديم", File.ReadAllText(Path.Combine(_root, "announcement.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
