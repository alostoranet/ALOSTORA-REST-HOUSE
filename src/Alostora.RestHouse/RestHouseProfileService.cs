using System.IO;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record RestHouseProfile(
    string RestHouseName,
    string NetworkName,
    string? LogoPath);

public sealed class RestHouseProfileService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public RestHouseProfileService(string root)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "profile.json");
    }

    public RestHouseProfile Load()
    {
        try
        {
            if (!File.Exists(_path))
                return Defaults;
            var value = JsonSerializer.Deserialize<RestHouseProfile>(File.ReadAllText(_path));
            if (value is null)
                return Defaults;
            return new RestHouseProfile(
                Clean(value.RestHouseName, 100) ?? Defaults.RestHouseName,
                Clean(value.NetworkName, 100) ?? Defaults.NetworkName,
                Clean(value.LogoPath, 240));
        }
        catch
        {
            return Defaults;
        }
    }

    public void Save(RestHouseProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var restHouseName = Clean(profile.RestHouseName, 100);
        var networkName = Clean(profile.NetworkName, 100);
        var logoPath = Clean(profile.LogoPath, 240);
        if (restHouseName is null)
            throw new ArgumentException("اسم الاستراحة مطلوب ولا يتجاوز 100 حرف.");
        if (networkName is null)
            throw new ArgumentException("اسم الشبكة مطلوب ولا يتجاوز 100 حرف.");

        var value = new RestHouseProfile(restHouseName, networkName, logoPath);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = value.Trim();
        return cleaned.Length <= maxLength ? cleaned : null;
    }

    private static RestHouseProfile Defaults =>
        new("استراحة الأسطورة", "شبكة الأسطورة نت", null);
}
