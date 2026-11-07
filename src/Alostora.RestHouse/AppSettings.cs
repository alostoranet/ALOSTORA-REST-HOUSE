using System.IO;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record AppSettings(string LibraryPath, string PostersPath, string? FfmpegPath = null, int Port = 8097)
{
    public static AppSettings Load(string root)
    {
        var defaults = new AppSettings(
            Path.Combine(root, "Library"),
            Path.Combine(root, "posters"),
            null,
            8097);
        var path = Path.Combine(root, "settings.json");
        if (!File.Exists(path)) return defaults;

        AppSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        }
        catch
        {
            return defaults;
        }

        if (loaded is null || string.IsNullOrWhiteSpace(loaded.LibraryPath) ||
            string.IsNullOrWhiteSpace(loaded.PostersPath))
            return defaults;

        string? ffmpeg = null;
        if (!string.IsNullOrWhiteSpace(loaded.FfmpegPath))
        {
            try { ffmpeg = Path.GetFullPath(loaded.FfmpegPath); }
            catch { ffmpeg = null; }
        }

        var port = loaded.Port is >= 1024 and <= 65535 ? loaded.Port : 8097;
        try
        {
            return new AppSettings(
                Path.GetFullPath(loaded.LibraryPath),
                Path.GetFullPath(loaded.PostersPath),
                ffmpeg,
                port);
        }
        catch
        {
            return defaults;
        }
    }

    public static void Save(string root, string libraryPath, string postersPath)
    {
        var current = Load(root);
        SaveAll(root, libraryPath, postersPath, current.FfmpegPath, current.Port);
    }

    public static void SaveFfmpegPath(string root, string? ffmpegPath)
    {
        var current = Load(root);
        SaveAll(root, current.LibraryPath, current.PostersPath, ffmpegPath, current.Port);
    }

    public static void SavePort(string root, int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var current = Load(root);
        SaveAll(root, current.LibraryPath, current.PostersPath, current.FfmpegPath, port);
    }

    private static void SaveAll(string root, string libraryPath, string postersPath, string? ffmpegPath, int port)
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "settings.json");
        var temporary = destination + ".tmp";
        var settings = new AppSettings(
            Path.GetFullPath(libraryPath),
            Path.GetFullPath(postersPath),
            string.IsNullOrWhiteSpace(ffmpegPath) ? null : Path.GetFullPath(ffmpegPath),
            port);
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, destination, overwrite: true);
    }
}
