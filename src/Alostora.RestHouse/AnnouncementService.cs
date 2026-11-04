using System.IO;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record RestHouseAnnouncement(string Title, string Message, bool IsActive, string Kind, DateTime UpdatedUtc);

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
                    return new("", "", false, "notice", DateTime.UtcNow);
                var value = JsonSerializer.Deserialize<RestHouseAnnouncement>(File.ReadAllText(_path));
                return value is null ? new("", "", false, "notice", DateTime.UtcNow) : Clean(value);
            }
            catch
            {
                return new("", "", false, "notice", DateTime.UtcNow);
            }
        }
    }

    public RestHouseAnnouncement Save(RestHouseAnnouncement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var clean = Clean(value) with { UpdatedUtc = DateTime.UtcNow };
        if (clean.Title.Length > 120 || clean.Message.Length > 1000)
            throw new ArgumentException("العنوان بحد أقصى 120 حرفاً والتنبيه بحد أقصى 1000 حرف.");
        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(clean, JsonOptions));
            File.Move(temp, _path, true);
        }
        return clean;
    }

    private static RestHouseAnnouncement Clean(RestHouseAnnouncement value) =>
        new((value.Title ?? "").Trim(), (value.Message ?? "").Trim(), value.IsActive,
            value.Kind is "ad" or "notice" ? value.Kind : "notice", value.UpdatedUtc);
}
