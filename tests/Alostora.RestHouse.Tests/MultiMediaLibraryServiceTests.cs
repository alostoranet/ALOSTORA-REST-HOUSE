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

    [Fact]
    public void SupportsMultipleIndependentRootsForEachMediaKind()
    {
        var mediaRoot = Path.Combine(_root, "media");
        var library = new MultiMediaLibraryService(mediaRoot, _databasePath);
        var externalAudio = Path.Combine(_root, "drive-d", "Music");
        var externalBooks = Path.Combine(_root, "drive-e", "Books");
        Directory.CreateDirectory(externalAudio);
        Directory.CreateDirectory(externalBooks);
        File.WriteAllBytes(Path.Combine(externalAudio, "concert.flac"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(externalBooks, "manual.epub"), [5, 6, 7]);

        var audioRoot = library.AddRoot("Audio", externalAudio);
        var booksRoot = library.AddRoot("Books", externalBooks);

        Assert.False(audioRoot.IsDefault);
        Assert.False(booksRoot.IsDefault);
        Assert.Equal(1, library.Scan("Audio"));
        Assert.Equal(1, library.Scan("Books"));
        Assert.Contains(library.List("Audio"), x => x.Title == "concert" && x.SizeBytes == 4);
        Assert.Contains(library.List("Books"), x => x.Title == "manual" && x.SizeBytes == 3);
        Assert.Contains(library.ListRoots("Audio"), x => x.Id == audioRoot.Id && x.Path == externalAudio);
        Assert.Equal(2, library.GetStorageSummary().IndexedFiles);
    }

    [Fact]
    public void RemovingExternalRootUnlinksItWithoutDeletingOriginalFiles()
    {
        var mediaRoot = Path.Combine(_root, "media");
        var library = new MultiMediaLibraryService(mediaRoot, _databasePath);
        var external = Path.Combine(_root, "external-audio");
        Directory.CreateDirectory(external);
        var song = Path.Combine(external, "song.mp3");
        File.WriteAllBytes(song, [1, 2, 3]);
        var root = library.AddRoot("Audio", external);
        library.Scan("Audio");

        Assert.True(library.RemoveRoot(root.Id));
        Assert.True(File.Exists(song));
        Assert.DoesNotContain(library.List("Audio"), x => x.FileName == "song.mp3");
        Assert.False(library.RemoveRoot(library.ListRoots("Audio").Single(x => x.IsDefault).Id));
    }

    [Fact]
    public void RejectsOverlappingRootsToAvoidDuplicateCatalogEntries()
    {
        var mediaRoot = Path.Combine(_root, "media");
        var library = new MultiMediaLibraryService(mediaRoot, _databasePath);
        var external = Path.Combine(_root, "external");
        var nested = Path.Combine(external, "nested");
        Directory.CreateDirectory(nested);

        library.AddRoot("Audio", external);
        Assert.Throws<ArgumentException>(() => library.AddRoot("Audio", nested));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
