using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class MultiMediaLibraryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alostora-multimedia-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public MultiMediaLibraryServiceTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "resthouse.db");
    }

    [Fact]
    public void CreatesSeparateLibrariesAndIndexesOnlySupportedFiles()
    {
        var library = new MultiMediaLibraryService(Path.Combine(_root, "media"), _databasePath);
        var audio = Path.Combine(_root, "media", "Audio");
        var books = Path.Combine(_root, "media", "Books");
        var apps = Path.Combine(_root, "media", "Apps");
        Assert.True(Directory.Exists(audio));
        Assert.True(Directory.Exists(books));
        Assert.True(Directory.Exists(apps));

        File.WriteAllBytes(Path.Combine(audio, "song.mp3"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(audio, "notes.docx"), "not audio");
        File.WriteAllBytes(Path.Combine(books, "guide.pdf"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(apps, "client.apk"), [7, 8, 9]);

        Assert.Equal(1, library.Scan("Audio"));
        Assert.Equal(1, library.Scan("Books"));
        Assert.Equal(1, library.Scan("Apps"));
        Assert.Equal(3, library.List().Count);
        Assert.Single(library.List("Audio"));
        Assert.Equal("song", Assert.Single(library.List("Audio")).Title);
        Assert.EndsWith(".mp3", library.ResolveById(Assert.Single(library.List("Audio")).Id));
    }

    [Fact]
    public void RejectsUnknownKindsAndDoesNotReturnMissingFiles()
    {
        var library = new MultiMediaLibraryService(Path.Combine(_root, "media"), _databasePath);
        Assert.Throws<ArgumentException>(() => library.Scan("Videos"));

        var file = Path.Combine(_root, "media", "Books", "gone.epub");
        File.WriteAllText(file, "book");
        library.Scan("Books");
        File.Delete(file);
        Assert.Empty(library.List("Books"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
