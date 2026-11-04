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
    bool ShowAnnouncement = true);

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
                return JsonSerializer.Deserialize<CustomerPageSettings>(File.ReadAllText(_path))
                    ?? new CustomerPageSettings();
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
