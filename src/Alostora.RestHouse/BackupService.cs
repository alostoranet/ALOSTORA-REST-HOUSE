using Microsoft.Data.Sqlite;
using System.IO;
using System.IO.Compression;

namespace Alostora.RestHouse;

public static class BackupService
{
    public static void CreateArchive(string databasePath, string postersDirectory, string archivePath)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("قاعدة البيانات غير موجودة.", databasePath);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "AlostoraRestHouseBackup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var backupDatabase = Path.Combine(tempDirectory, "resthouse.db");
            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared
            };
            var destinationBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = backupDatabase,
                Mode = SqliteOpenMode.ReadWriteCreate
            };

            using (var source = new SqliteConnection(sourceBuilder.ToString()))
            using (var destination = new SqliteConnection(destinationBuilder.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            if (Directory.Exists(postersDirectory))
            {
                var targetPosters = Path.Combine(tempDirectory, "posters");
                Directory.CreateDirectory(targetPosters);
                foreach (var file in Directory.EnumerateFiles(postersDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    var target = Path.Combine(targetPosters, Path.GetFileName(file));
                    File.Copy(file, target, overwrite: true);
                }
            }

            File.WriteAllText(
                Path.Combine(tempDirectory, "backup-info.txt"),
                "ALOSTORA REST HOUSE backup\nCreated UTC: " + DateTime.UtcNow.ToString("O") +
                "\nContains: SQLite database and poster images. Video files are not included.");

            var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
            if (!string.IsNullOrEmpty(archiveDirectory))
                Directory.CreateDirectory(archiveDirectory);

            if (File.Exists(archivePath))
                File.Delete(archivePath);

            ZipFile.CreateFromDirectory(tempDirectory, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch { /* لا نُفشل النسخة الاحتياطية بسبب تعذر حذف الملفات المؤقتة. */ }
        }
    }
}
