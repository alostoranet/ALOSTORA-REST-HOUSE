using System.Security.Cryptography;
using System.Text;

namespace Alostora.RestHouse;

// لا نرسل المسارات الفعلية من القرص إلى أجهزة المشتركين.
public sealed record LibraryVideo(string Id, string Title, string FileName, string Url, long SizeBytes);
public sealed record ResolvedVideo(string FullPath, string ContentType);

public sealed class LibraryService
{
    private readonly string _root;
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
    }

    public IReadOnlyList<LibraryVideo> List() =>
        Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories)
            .Where(path => ContentTypes.ContainsKey(Path.GetExtension(path)))
            .Select(path =>
            {
                var relative = Path.GetRelativePath(_root, path);
                var id = CreateId(relative);
                return new LibraryVideo(id, Path.GetFileNameWithoutExtension(path),
                    Path.GetFileName(path), "/media/" + id, new FileInfo(path).Length);
            })
            .OrderBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public ResolvedVideo? Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 24 || id.Any(c => !Uri.IsHexDigit(c)))
            return null;

        foreach (var path in Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories))
        {
            if (!ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
                continue;
            var relative = Path.GetRelativePath(_root, path);
            if (CreateId(relative).Equals(id, StringComparison.OrdinalIgnoreCase))
                return new ResolvedVideo(path, contentType);
        }
        return null;
    }

    private static string CreateId(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24].ToLowerInvariant();
    }
}
