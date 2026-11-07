using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alostora-backup-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "resthouse.db");
    private string PostersPath => Path.Combine(_root, "posters");
    private string ArchivePath => Path.Combine(_root, "backup.zip");

    private void CreateDatabase()
    {
        Directory.CreateDirectory(_root);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS BackupProbe (Id INTEGER PRIMARY KEY);";
        command.ExecuteNonQuery();
        Directory.CreateDirectory(PostersPath);
    }

    [Fact]
    public void BackupIncludesLocalAnnouncementConfiguration()
    {
        CreateDatabase();
        const string announcement = "{\"title\":\"تنبيه\",\"message\":\"رسالة محفوظة\",\"isActive\":true}";
        File.WriteAllText(Path.Combine(_root, "announcement.json"), announcement);

        BackupService.CreateArchive(DatabasePath, PostersPath, ArchivePath);

        using var archive = ZipFile.OpenRead(ArchivePath);
        var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("announcement.json"));
        using var reader = new StreamReader(entry.Open());
        Assert.Equal(announcement, reader.ReadToEnd());
    }

    [Fact]
    public void RestoreRestoresAnnouncementFromBackup()
    {
        CreateDatabase();
        const string original = "{\"title\":\"قبل النسخ\",\"message\":\"النص الأصلي\",\"isActive\":true}";
        File.WriteAllText(Path.Combine(_root, "announcement.json"), original);
        BackupService.CreateArchive(DatabasePath, PostersPath, ArchivePath);
        File.WriteAllText(Path.Combine(_root, "announcement.json"), "{\"title\":\"تغيير مؤقت\"}");

        BackupService.RestoreArchive(DatabasePath, PostersPath, ArchivePath, _root);

        Assert.Equal(original, File.ReadAllText(Path.Combine(_root, "announcement.json")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
