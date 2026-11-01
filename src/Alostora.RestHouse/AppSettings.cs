using System.IO;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record AppSettings(string LibraryPath, string PostersPath)
{
    public static AppSettings Load(string root)
    {
        var defaults = new AppSettings(
            Path.Combine(root, "Library"),
            Path.Combine(root, "posters"));
        var path = Path.Combine(root, "settings.json");
        if (!File.Exists(path)) return defaults;

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            if (loaded is null || string.IsNullOrWhiteSpace(loaded.LibraryPath) ||
                string.IsNullOrWhiteSpace(loaded.PostersPath))
                return defaults;

            return new AppSettings(
                Path.GetFullPath(loaded.LibraryPath),
                Path.GetFullPath(loaded.PostersPath));
        }
        catch
        {
            return defaults;
        }
    }

    public static void Save(string root, string libraryPath, string postersPath)
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "settings.json");
        var temporary = destination + ".tmp";
        var settings = new AppSettings(
            Path.GetFullPath(libraryPath),
            Path.GetFullPath(postersPath));
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, destination, overwrite: true);
    }
}
