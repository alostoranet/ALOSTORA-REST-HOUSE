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


    [Fact]
    public void ScanIsIdempotentAndUpdatesExistingCatalogEntry()
    {
        var library = new MultiMediaLibraryService(Path.Combine(_root, "media"), _databasePath);
        var audio = Path.Combine(_root, "media", "Audio");
        var file = Path.Combine(audio, "first-name.mp3");
        File.WriteAllBytes(file, [1, 2, 3]);

        Assert.Equal(1, library.Scan("Audio"));
        var first = Assert.Single(library.List("Audio"));
        File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
        File.Move(file, Path.Combine(audio, "renamed.mp3"));

        Assert.Equal(1, library.Scan("Audio"));
        var second = Assert.Single(library.List("Audio"));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("renamed", second.Title);
        Assert.Equal(5, second.SizeBytes);
    }

    [Fact]
    public void RejectsCatalogPathsThatEscapeLibraryRoot()
    {
        var libraryRoot = Path.Combine(_root, "media");
        var library = new MultiMediaLibraryService(libraryRoot, _databasePath);
        var book = Path.Combine(libraryRoot, "Books", "safe.pdf");
        File.WriteAllText(book, "safe");
        library.Scan("Books");
        var item = Assert.Single(library.List("Books"));

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MediaLibraryItems SET RelativePath='../outside.pdf' WHERE Id=$id";
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();

        Assert.Null(library.ResolveById(item.Id));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
