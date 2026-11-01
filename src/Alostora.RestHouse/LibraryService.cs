using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Alostora.RestHouse;

public sealed record LibraryVideo(string Id, string Title, string FileName, string Url, long SizeBytes);
public sealed record ResolvedVideo(string FullPath, string ContentType);
public sealed record SubtitleTrack(string FileName, string Label, string Language, string Url);

public sealed class LibraryService : IDisposable
{
    private readonly string _root;
    private readonly string _connectionString;
    private readonly object _sync = new();
    private readonly List<FileSystemWatcher> _watchers = new();
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
                CREATE TABLE IF NOT EXISTS LibraryScanRoots(
                    FolderPath TEXT PRIMARY KEY COLLATE NOCASE,
                    AddedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ExternalLibraryFiles(
                    FullPath TEXT PRIMARY KEY COLLATE NOCASE,
                    VideoId TEXT NOT NULL UNIQUE,
                    UpdatedUtc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        _watcher = CreateWatcher(_root, true);
        _watcher.Renamed += OnRenamed;
        _watchers.Add(_watcher);
        foreach (var folder in GetScanRoots())
            AddWatcher(folder);
    }

    private FileSystemWatcher CreateWatcher(string folder, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Created += Invalidate;
        watcher.Changed += Invalidate;
        watcher.Deleted += Invalidate;
        watcher.Error += (_, _) => Invalidate();
        return watcher;
    }

    public IReadOnlyList<LibraryVideo> List()
    {
        EnsureIndex();
        lock (_sync)
            return _videos!.ToArray();
    }

    public IReadOnlyList<LibraryVideo> ListFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return Array.Empty<LibraryVideo>();
        var root = Path.GetFullPath(folderPath);
        return List().Where(video =>
        {
            var resolved = Resolve(video.Id);
            if (resolved is null) return false;
            var relative = Path.GetRelativePath(root, Path.GetFullPath(resolved.FullPath));
            return relative == "." || (!Path.IsPathRooted(relative) &&
                relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }).ToArray();
    }

    public IReadOnlyList<SubtitleTrack> ListSubtitles(string videoId)
    {
        var video = Resolve(videoId);
        if (video is null) return Array.Empty<SubtitleTrack>();
        var directory = Path.GetDirectoryName(video.FullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<SubtitleTrack>();

        var stem = Path.GetFileNameWithoutExtension(video.FullPath);
        var tracks = new List<SubtitleTrack>();
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch { return tracks; }

        foreach (var path in candidates)
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (new FileInfo(path).Length > 2 * 1024 * 1024) continue;
                var suffix = name.Length == stem.Length ? "" : name[(stem.Length + 1)..];
                var language = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                var label = LanguageLabel(language, suffix, extension);
                tracks.Add(new SubtitleTrack(Path.GetFileName(path), label, language,
                    "/api/subtitles/" + videoId + "/" + Uri.EscapeDataString(Path.GetFileName(path))));
            }
            catch { }
        }
        return tracks.OrderBy(x => x.Language == "ar" ? 0 : 1)
            .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public string? ReadSubtitle(string videoId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            return null;
        var video = Resolve(videoId);
        if (video is null) return null;
        var directory = Path.GetDirectoryName(video.FullPath);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        if (!ListSubtitles(videoId).Any(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            return null;
        var extension = Path.GetExtension(fullPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length > 2 * 1024 * 1024) return null;
        var bytes = File.ReadAllBytes(fullPath);
        var content = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
            ? Encoding.Unicode.GetString(bytes)
            : bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
                ? Encoding.BigEndianUnicode.GetString(bytes)
                : new UTF8Encoding(false, false).GetString(bytes);
        return extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
            ? ConvertSrtToVtt(content)
            : EnsureVttHeader(content);
    }

    private static string LanguageLabel(string language, string suffix, string extension)
    {
        var known = language.ToLowerInvariant() switch
        {
            "ar" or "ara" => "العربية",
            "en" or "eng" => "English",
            "fr" or "fra" => "Français",
            "tr" or "tur" => "Türkçe",
            "es" or "spa" => "Español",
            "de" or "deu" => "Deutsch",
            _ => ""
        };
        if (!string.IsNullOrEmpty(known)) return known;
        return string.IsNullOrEmpty(suffix) ? "ترجمة" : suffix + " (" + extension.TrimStart('.').ToUpperInvariant() + ")";
    }

    private static string EnsureVttHeader(string text)
    {
        var clean = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        return clean.StartsWith("WEBVTT", StringComparison.Ordinal) ? clean : "WEBVTT\n\n" + clean;
    }

    private static string ConvertSrtToVtt(string text)
    {
        var clean = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = clean.Split('\n');
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("-->"))
                output.AppendLine(trimmed.Replace(',', '.'));
            else
                output.AppendLine(line);
        }
        return output.ToString();
    }

    public ResolvedVideo? Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 24 || id.Any(c => !Uri.IsHexDigit(c)))
            return null;

        EnsureIndex();
        lock (_sync)
            return _resolved!.TryGetValue(id, out var video) ? video : null;
    }

    // Registers an additional folder without moving or copying its files.
    // Its full path is persisted so it is available after application restarts.
    public void EnsureFolderRegistered(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;
        var full = Path.GetFullPath(folderPath);
        if (IsInsideRoot(full)) return;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM LibraryScanRoots WHERE FolderPath=$path";
        command.Parameters.AddWithValue("$path", full);
        if (command.ExecuteScalar() is null)
            RegisterFolder(full);
        else
            AddWatcher(full);
    }

    public int RegisterFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("مجلد الفيديو غير موجود أو لا يمكن الوصول إليه.");

        var full = Path.GetFullPath(folderPath);
        if (IsInsideRoot(full))
        {
            Invalidate();
            return Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
                .Count(path => ContentTypes.ContainsKey(Path.GetExtension(path)));
        }

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO LibraryScanRoots(FolderPath,AddedUtc) VALUES($path,$utc)";
            command.Parameters.AddWithValue("$path", full);
            command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        AddWatcher(full);
        Invalidate();
        return Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
            .Count(path => ContentTypes.ContainsKey(Path.GetExtension(path)));
    }

    private bool IsInsideRoot(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        return relative == "." || (!Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private string[] GetScanRoots()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FolderPath FROM LibraryScanRoots";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (Directory.Exists(path) && !IsInsideRoot(path))
                result.Add(path);
        }
        return result.ToArray();
    }

    private void AddWatcher(string folder)
    {
        lock (_sync)
        {
            if (_watchers.Any(w => string.Equals(w.Path.TrimEnd(Path.DirectorySeparatorChar),
                folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                return;
            try
            {
                var watcher = CreateWatcher(folder, true);
                watcher.Renamed += OnExternalRenamed;
                _watchers.Add(watcher);
            }
            catch
            {
                // An inaccessible optional folder must not prevent the server from starting.
            }
        }
    }

    private void EnsureIndex()
    {
        lock (_sync)
        {
            if (_videos is not null && _resolved is not null)
                return;

            var videos = new List<LibraryVideo>();
            var resolved = new Dictionary<string, ResolvedVideo>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var roots = new[] { _root }.Concat(GetScanRoots()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var scanRoot in roots)
            {
                if (!Directory.Exists(scanRoot)) continue;
                IEnumerable<string> paths;
                try { paths = Directory.EnumerateFiles(scanRoot, "*.*", SearchOption.AllDirectories).ToArray(); }
                catch { continue; }

                foreach (var path in paths)
                {
                    if (!ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
                        continue;
                    var fullPath = Path.GetFullPath(path);
                    if (!seen.Add(fullPath)) continue;

                    var id = IsInsideRoot(fullPath)
                        ? GetOrCreateId(connection, transaction, NormalizeRelativePath(Path.GetRelativePath(_root, fullPath)))
                        : GetOrCreateExternalId(connection, transaction, fullPath);
                    var file = new FileInfo(fullPath);
                    videos.Add(new LibraryVideo(id, file.Name[..^file.Extension.Length],
                        file.Name, "/media/" + id, file.Length));
                    resolved[id] = new ResolvedVideo(fullPath, contentType);
                }
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

    private static string GetOrCreateExternalId(SqliteConnection connection, SqliteTransaction transaction, string fullPath)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT VideoId FROM ExternalLibraryFiles WHERE FullPath=$path";
            find.Parameters.AddWithValue("$path", fullPath);
            var existing = find.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
        }

        var id = CreateId("external:" + fullPath);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO ExternalLibraryFiles(FullPath,VideoId,UpdatedUtc) VALUES($path,$id,$updated)";
        insert.Parameters.AddWithValue("$path", fullPath);
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
                while (reader.Read()) mappings.Add((reader.GetString(0), reader.GetString(1)));

            foreach (var mapping in mappings)
            {
                var suffix = mapping.Path.Length == oldRelative.Length ? "" : mapping.Path[oldRelative.Length..];
                var target = newRelative + suffix;
                using var exists = connection.CreateCommand();
                exists.Transaction = transaction;
                exists.CommandText = "SELECT 1 FROM LibraryFileIds WHERE RelativePath=$path";
                exists.Parameters.AddWithValue("$path", target);
                if (exists.ExecuteScalar() is not null) continue;

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
        catch { }
        finally { Invalidate(); }
    }

    private void OnExternalRenamed(object? sender, RenamedEventArgs e)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ExternalLibraryFiles SET FullPath=$new,UpdatedUtc=$utc WHERE FullPath=$old";
            command.Parameters.AddWithValue("$new", Path.GetFullPath(e.FullPath));
            command.Parameters.AddWithValue("$old", Path.GetFullPath(e.OldFullPath));
            command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        catch { }
        finally { Invalidate(); }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private void Invalidate(object? sender, FileSystemEventArgs e) => Invalidate();
    private void Invalidate()
    {
        lock (_sync) { _videos = null; _resolved = null; }
    }

    private static string CreateId(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24].ToLowerInvariant();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
    }
}