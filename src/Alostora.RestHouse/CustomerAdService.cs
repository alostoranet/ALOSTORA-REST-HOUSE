using System.IO;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record CustomerAdItem(
    string Id, string FileName, string Title, string LinkUrl,
    int DurationSeconds, int SortOrder, bool IsActive, DateTime CreatedUtc);

public sealed record CustomerAdUpdateRequest(string Title, string LinkUrl, int DurationSeconds, bool IsActive);
public sealed record CustomerAdVisibilityRequest(bool IsActive);
public sealed record CustomerAdMoveRequest(string Direction);

public sealed class CustomerAdService
{
    public const long MaximumImageBytes = 5L * 1024 * 1024;
    private readonly string _imageDirectory;
    private readonly string _metadataPath;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public CustomerAdService(string applicationRoot, string postersDirectory)
    {
        Directory.CreateDirectory(applicationRoot);
        _imageDirectory = Path.Combine(Path.GetFullPath(postersDirectory), "customer-ads");
        Directory.CreateDirectory(_imageDirectory);
        _metadataPath = Path.Combine(applicationRoot, "customer-ads.json");
    }

    public IReadOnlyList<CustomerAdItem> List(bool activeOnly = false)
    {
        lock (_gate)
        {
            var items = LoadLocked().OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedUtc).ToArray();
            return activeOnly ? items.Where(x => x.IsActive).ToArray() : items;
        }
    }

    public CustomerAdItem Add(Stream image, string originalFileName, string title, string linkUrl, int durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (durationSeconds is < 5 or > 120) throw new ArgumentException("مدة ظهور الإعلان يجب أن تكون بين 5 و120 ثانية.");
        title = (title ?? "").Trim();
        if (title.Length > 120) throw new ArgumentException("عنوان الإعلان بحد أقصى 120 حرفاً.");
        linkUrl = ValidateLink(linkUrl);
        var extension = Path.GetExtension(originalFileName ?? "").ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new ArgumentException("الصيغ المدعومة للإعلانات: JPG وPNG وWebP فقط.");

        using var buffer = new MemoryStream();
        image.CopyTo(buffer);
        if (buffer.Length is <= 0 or > MaximumImageBytes)
            throw new ArgumentException("حجم صورة الإعلان يجب ألا يتجاوز 5 ميجابايت.");
        var bytes = buffer.ToArray();
        if (!HasValidSignature(bytes, extension))
            throw new ArgumentException("محتوى الصورة لا يطابق امتداد الملف. اختر صورة JPG أو PNG أو WebP سليمة.");

        var id = Guid.NewGuid().ToString("N");
        var fileName = id + extension;
        var target = Path.Combine(_imageDirectory, fileName);
        var temporary = target + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: false);
            lock (_gate)
            {
                var items = LoadLocked();
                if (items.Count >= 50) throw new ArgumentException("يمكن إدارة 50 إعلاناً مصوراً كحد أقصى.");
                var item = new CustomerAdItem(id, fileName, title, linkUrl, durationSeconds,
                    items.Count == 0 ? 1 : items.Max(x => x.SortOrder) + 1, true, DateTime.UtcNow);
                items.Add(item);
                SaveLocked(items);
                return item;
            }
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            throw;
        }
    }

    public CustomerAdItem Update(string id, CustomerAdUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DurationSeconds is < 5 or > 120) throw new ArgumentException("مدة ظهور الإعلان يجب أن تكون بين 5 و120 ثانية.");
        var title = (request.Title ?? "").Trim();
        if (title.Length > 120) throw new ArgumentException("عنوان الإعلان بحد أقصى 120 حرفاً.");
        var link = ValidateLink(request.LinkUrl);
        lock (_gate)
        {
            var items = LoadLocked();
            var index = items.FindIndex(x => x.Id == id);
            if (index < 0) throw new KeyNotFoundException("الإعلان غير موجود.");
            var updated = items[index] with
            {
                Title = title,
                LinkUrl = link,
                DurationSeconds = request.DurationSeconds,
                IsActive = request.IsActive
            };
            items[index] = updated;
            SaveLocked(items);
            return updated;
        }
    }

    public bool SetVisibility(string id, bool isActive)
    {
        lock (_gate)
        {
            var items = LoadLocked();
            var index = items.FindIndex(x => x.Id == id);
            if (index < 0) return false;
            items[index] = items[index] with { IsActive = isActive };
            SaveLocked(items);
            return true;
        }
    }

    public bool Move(string id, string direction)
    {
        if (direction is not ("up" or "down")) throw new ArgumentException("اتجاه الترتيب غير صالح.");
        lock (_gate)
        {
            var items = LoadLocked().OrderBy(x => x.SortOrder).ToList();
            var index = items.FindIndex(x => x.Id == id);
            if (index < 0) return false;
            var target = direction == "up" ? index - 1 : index + 1;
            if (target < 0 || target >= items.Count) return true;
            (items[index], items[target]) = (items[target], items[index]);
            for (var i = 0; i < items.Count; i++) items[i] = items[i] with { SortOrder = i + 1 };
            SaveLocked(items);
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var items = LoadLocked();
            var item = items.FirstOrDefault(x => x.Id == id);
            if (item is null) return false;
            items.Remove(item);
            for (var i = 0; i < items.Count; i++) items[i] = items.OrderBy(x => x.SortOrder).ElementAt(i) with { SortOrder = i + 1 };
            SaveLocked(items);
            var path = SafeImagePath(item.FileName);
            if (path is not null && File.Exists(path)) File.Delete(path);
            return true;
        }
    }

    public bool TryGetImage(string id, out string path, out string contentType)
    {
        lock (_gate)
        {
            var item = LoadLocked().FirstOrDefault(x => x.Id == id);
            var resolved = item is null ? null : SafeImagePath(item.FileName);
            if (resolved is not null && File.Exists(resolved))
            {
                path = resolved;
                contentType = Path.GetExtension(resolved).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                return true;
            }
        }
        path = "";
        contentType = "application/octet-stream";
        return false;
    }

    private List<CustomerAdItem> LoadLocked()
    {
        try
        {
            if (!File.Exists(_metadataPath)) return new List<CustomerAdItem>();
            var rows = JsonSerializer.Deserialize<List<CustomerAdItem>>(File.ReadAllText(_metadataPath), JsonOptions)
                ?? new List<CustomerAdItem>();
            return rows.Where(x => Guid.TryParseExact(x.Id, "N", out _) &&
                                   Path.GetFileName(x.FileName) == x.FileName &&
                                   x.FileName.StartsWith(x.Id, StringComparison.OrdinalIgnoreCase) &&
                                   x.DurationSeconds is >= 5 and <= 120 &&
                                   x.SortOrder > 0).ToList();
        }
        catch { return new List<CustomerAdItem>(); }
    }

    private void SaveLocked(List<CustomerAdItem> items)
    {
        var temporary = _metadataPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, JsonOptions));
        File.Move(temporary, _metadataPath, overwrite: true);
    }

    private string? SafeImagePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) return null;
        var path = Path.GetFullPath(Path.Combine(_imageDirectory, fileName));
        return path.StartsWith(_imageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? path : null;
    }

    private static string ValidateLink(string? value)
    {
        var link = (value ?? "").Trim();
        if (link.Length == 0) return "";
        if (link.Length > 1000 || !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("رابط الإعلان يجب أن يبدأ بـ http:// أو https://.");
        return uri.AbsoluteUri;
    }

    private static bool HasValidSignature(byte[] bytes, string extension)
    {
        if (extension is ".jpg" or ".jpeg")
            return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        if (extension == ".png")
            return bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (extension == ".webp")
            return bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                   bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
        return false;
    }
}
