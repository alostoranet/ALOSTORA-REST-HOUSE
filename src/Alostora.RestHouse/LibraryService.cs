using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Alostora.RestHouse;

// لا نرسل المسارات الفعلية من القرص إلى أجهزة المشتركين.
public sealed record LibraryVideo(string Id, string Title, string FileName, string Url, long SizeBytes);
public sealed record ResolvedVideo(string FullPath, string ContentType);

public sealed class LibraryService : IDisposable
{
    private readonly string _root;
    private readonly string _connectionString;
    private readonly object _sync = new();
    private readonly FileSystemWatcher _watcher;
    private LibraryVideo[]? _videos;
    private Dictionary<string, ResolvedVideo>? _resolved;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".webm"] = "video/webm"
    };

    public LibraryService(string root, string databasePath)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 10,
            Pooling = true
        }.ToString();

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS LibraryFileIds(
                    RelativePath TEXT PRIMARY KEY COLLATE NOCASE,
                    VideoId TEXT NOT NULL UNIQUE,
                    UpdatedUtc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Created += Invalidate;
        _watcher.Changed += Invalidate;
        _watcher.Deleted += Invalidate;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += (_, _) => Invalidate();
    }

    public IReadOnlyList<LibraryVideo> List()
    {
        EnsureIndex();
        lock (_sync)
            return _videos!.ToArray();
    }

    public ResolvedVideo? Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 24 || id.Any(c => !Uri.IsHexDigit(c)))
            return null;

        EnsureIndex();
        lock (_sync)
            return _resolved!.TryGetValue(id, out var video) ? video : null;
    }

    private void EnsureIndex()
    {
        lock (_sync)
        {
            if (_videos is not null && _resolved is not null)
                return;

            var videos = new List<LibraryVideo>();
            var resolved = new Dictionary<string, ResolvedVideo>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var path in Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories))
            {
                if (!ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
                    continue;

                var relative = NormalizeRelativePath(Path.GetRelativePath(_root, path));
                var id = GetOrCreateId(connection, transaction, relative);
                var file = new FileInfo(path);
                videos.Add(new LibraryVideo(id, file.Name[..^file.Extension.Length],
                    file.Name, "/media/" + id, file.Length));
                resolved[id] = new ResolvedVideo(path, contentType);
            }

            transaction.Commit();
            _videos = videos
                .OrderBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _resolved = resolved;
        }
    }

    private static string GetOrCreateId(SqliteConnection connection, SqliteTransaction transaction, string relativePath)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT VideoId FROM LibraryFileIds WHERE RelativePath=$path";
            find.Parameters.AddWithValue("$path", relativePath);
            var existing = find.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;
        }

        // Keep the legacy path-derived ID when first registering a file. This
        // preserves all existing metadata, episode links and watch-progress rows.
        var id = CreateId(relativePath);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO LibraryFileIds(RelativePath,VideoId,UpdatedUtc) VALUES($path,$id,$updated)";
        insert.Parameters.AddWithValue("$path", relativePath);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return id;
    }

    private void OnRenamed(object? sender, RenamedEventArgs e)
    {
        try
        {
            var oldRelative = NormalizeRelativePath(Path.GetRelativePath(_root, e.OldFullPath));
            var newRelative = NormalizeRelativePath(Path.GetRelativePath(_root, e.FullPath));
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            // Handle both a single file rename and a directory move. Existing IDs
            // are retained, so metadata, series episode links and watch progress survive.
            using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = """
                SELECT RelativePath, VideoId FROM LibraryFileIds
                WHERE RelativePath=$old OR substr(RelativePath,1,length($prefix))=$prefix
                """;
            find.Parameters.AddWithValue("$old", oldRelative);
            find.Parameters.AddWithValue("$prefix", oldRelative.TrimEnd('/') + "/");
            var mappings = new List<(string Path, string Id)>();
            using (var reader = find.ExecuteReader())
                while (reader.Read())
                    mappings.Add((reader.GetString(0), reader.GetString(1)));

            foreach (var mapping in mappings)
            {
                var suffix = mapping.Path.Length == oldRelative.Length ? "" : mapping.Path[oldRelative.Length..];
                var target = newRelative + suffix;
                using var exists = connection.CreateCommand();
                exists.Transaction = transaction;
                exists.CommandText = "SELECT 1 FROM LibraryFileIds WHERE RelativePath=$path";
                exists.Parameters.AddWithValue("$path", target);
                if (exists.ExecuteScalar() is not null)
                    continue;

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE LibraryFileIds SET RelativePath=$new,UpdatedUtc=$updated WHERE RelativePath=$old";
                update.Parameters.AddWithValue("$new", target);
                update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$old", mapping.Path);
                update.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            // A file-system notification must never crash the media server.
        }
        finally
        {
            Invalidate();
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private void Invalidate(object? sender, FileSystemEventArgs e) => Invalidate();

    private void Invalidate()
    {
        lock (_sync)
        {
            _videos = null;
            _resolved = null;
        }
    }

    private static string CreateId(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24].ToLowerInvariant();
    }

    public void Dispose() => _watcher.Dispose();
}
