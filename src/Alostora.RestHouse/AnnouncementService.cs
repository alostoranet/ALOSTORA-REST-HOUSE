using System.IO;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record RestHouseAnnouncement(
    string Title,
    string Message,
    bool IsActive,
    string Kind,
    DateTime UpdatedUtc,
    int SpeedSeconds = 18);

public sealed class AnnouncementService
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AnnouncementService(string root)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "announcement.json");
    }

    public RestHouseAnnouncement Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return Empty();
                var value = JsonSerializer.Deserialize<RestHouseAnnouncement>(File.ReadAllText(_path));
                return value is null ? Empty() : Clean(value);
            }
            catch
            {
                return Empty();
            }
        }
    }

    public RestHouseAnnouncement Save(RestHouseAnnouncement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var title = (value.Title ?? string.Empty).Trim();
        var message = (value.Message ?? string.Empty).Trim();
        if (title.Length > 120 || message.Length > 1000)
            throw new ArgumentException("العنوان بحد أقصى 120 حرفاً ونص الإعلان بحد أقصى 1000 حرف.");
        if (value.IsActive && string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("اكتب نص الإعلان قبل تشغيل الشريط.");
        var clean = new RestHouseAnnouncement(
            title,
            message,
            value.IsActive,
            value.Kind is "ad" or "notice" ? value.Kind : "notice",
            DateTime.UtcNow,
            Math.Clamp(value.SpeedSeconds, 5, 60));

        lock (_gate)
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(clean, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        return clean;
    }

    private static RestHouseAnnouncement Clean(RestHouseAnnouncement value) =>
        new(
            (value.Title ?? string.Empty).Trim(),
            (value.Message ?? string.Empty).Trim(),
            value.IsActive,
            value.Kind is "ad" or "notice" ? value.Kind : "notice",
            value.UpdatedUtc,
            Math.Clamp(value.SpeedSeconds, 5, 60));

    private static RestHouseAnnouncement Empty() =>
        new(string.Empty, string.Empty, false, "notice", DateTime.UtcNow, 18);
}
