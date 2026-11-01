using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Alostora.RestHouse;

// لا نرسل المسارات الفعلية من القرص إلى أجهزة المشتركين.
public sealed record LibraryVideo(string Id, string Title, string FileName, string Url, long SizeBytes);
public sealed record ResolvedVideo(string FullPath, string ContentType);

public sealed class LibraryService : IDisposable
{
    private readonly string _root;
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

    public LibraryService(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);

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
        _watcher.Renamed += Invalidate;
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

            foreach (var path in Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories))
            {
                if (!ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
                    continue;

                var relative = Path.GetRelativePath(_root, path);
                var id = CreateId(relative);
                var file = new FileInfo(path);
                videos.Add(new LibraryVideo(id, file.Name[..^file.Extension.Length],
                    file.Name, "/media/" + id, file.Length));
                resolved[id] = new ResolvedVideo(path, contentType);
            }

            _videos = videos
                .OrderBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _resolved = resolved;
        }
    }

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
