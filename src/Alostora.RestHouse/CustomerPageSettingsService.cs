using System.IO;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record CustomerPageSettings(
    bool ShowCatalog = true,
    bool ShowContinueWatching = true,
    bool ShowAudio = true,
    bool ShowBooks = true,
    bool ShowApps = true,
    bool ShowContact = true,
    bool ShowWelcome = true,
    bool ShowAnnouncement = true,
    bool ShowDownloads = true,
    bool ShowImageAds = true,
    string? LiveBroadcastUrl = null);

public sealed class CustomerPageSettingsService
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public CustomerPageSettingsService(string root)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "customer-page-settings.json");
    }

    public CustomerPageSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new CustomerPageSettings();
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<CustomerPageSettings>(json)
                    ?? new CustomerPageSettings();
                using var document = JsonDocument.Parse(json);
                var hasDownloadsSetting = document.RootElement.ValueKind == JsonValueKind.Object &&
                    (document.RootElement.TryGetProperty(nameof(CustomerPageSettings.ShowDownloads), out _) ||
                     document.RootElement.TryGetProperty("showDownloads", out _));
                var hasImageAdsSetting = document.RootElement.ValueKind == JsonValueKind.Object &&
                    (document.RootElement.TryGetProperty(nameof(CustomerPageSettings.ShowImageAds), out _) ||
                     document.RootElement.TryGetProperty("showImageAds", out _));
                return settings with
                {
                    ShowDownloads = hasDownloadsSetting ? settings.ShowDownloads : true,
                    ShowImageAds = hasImageAdsSetting ? settings.ShowImageAds : true
                };
            }
            catch
            {
                return new CustomerPageSettings();
            }
        }
    }

    public CustomerPageSettings Save(CustomerPageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            return settings;
        }
    }
}
