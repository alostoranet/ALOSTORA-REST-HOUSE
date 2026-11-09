using Alostora.RestHouse;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class LibraryServiceRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alostora-library-registration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public LibraryServiceRegistrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "resthouse.db");
    }

    [Fact]
    public void RegisterFilesAddsMultipleExternalFilesAndKeepsStableIds()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var firstFolder = Path.Combine(_root, "first-source");
        var secondFolder = Path.Combine(_root, "second-source");
        Directory.CreateDirectory(firstFolder);
        Directory.CreateDirectory(secondFolder);

        var firstPath = Path.Combine(firstFolder, "first.mp4");
        var secondPath = Path.Combine(secondFolder, "second.mkv");
        var firstBytes = new byte[] { 1, 2, 3, 4 };
        var secondBytes = new byte[] { 5, 6, 7, 8, 9 };
        File.WriteAllBytes(firstPath, firstBytes);
        File.WriteAllBytes(secondPath, secondBytes);

        using var library = new LibraryService(libraryRoot, _databasePath);

        Assert.Equal(2, library.RegisterFiles(new[] { firstPath, secondPath, firstPath }));

        var firstSnapshot = library.List();
        Assert.Equal(2, firstSnapshot.Count);
        var first = Assert.Single(firstSnapshot, x => x.FileName == "first.mp4");
        var second = Assert.Single(firstSnapshot, x => x.FileName == "second.mkv");
        Assert.Equal(Path.GetFullPath(firstPath), Path.GetFullPath(library.Resolve(first.Id)!.FullPath));
        Assert.Equal(Path.GetFullPath(secondPath), Path.GetFullPath(library.Resolve(second.Id)!.FullPath));
        Assert.Equal(firstBytes, File.ReadAllBytes(firstPath));
        Assert.Equal(secondBytes, File.ReadAllBytes(secondPath));

        Assert.Equal(2, library.RegisterFiles(new[] { firstPath, secondPath }));
        var secondSnapshot = library.List();
        Assert.Equal(2, secondSnapshot.Count);
        Assert.Equal(first.Id, Assert.Single(secondSnapshot, x => x.FileName == "first.mp4").Id);
        Assert.Equal(second.Id, Assert.Single(secondSnapshot, x => x.FileName == "second.mkv").Id);
    }

    [Fact]
    public void RegisterFilesRejectsWholeSelectionBeforePersistingUnsupportedFile()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var sourceFolder = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceFolder);

        var validPath = Path.Combine(sourceFolder, "valid.mp4");
        var unsupportedPath = Path.Combine(sourceFolder, "notes.txt");
        File.WriteAllBytes(validPath, new byte[] { 1, 2, 3 });
        File.WriteAllText(unsupportedPath, "not a video");

        using var library = new LibraryService(libraryRoot, _databasePath);

        Assert.Throws<InvalidOperationException>(() => library.RegisterFiles(new[] { validPath, unsupportedPath }));
        Assert.Empty(library.List());
        Assert.True(File.Exists(validPath));
        Assert.True(File.Exists(unsupportedPath));
    }

    [Fact]
    public void RegisterFilesRejectsMissingFileBeforePersistingValidSelection()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var sourceFolder = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceFolder);

        var validPath = Path.Combine(sourceFolder, "valid.mp4");
        var missingPath = Path.Combine(sourceFolder, "missing.mp4");
        File.WriteAllBytes(validPath, new byte[] { 1, 2, 3 });

        using var library = new LibraryService(libraryRoot, _databasePath);

        Assert.Throws<InvalidOperationException>(() => library.RegisterFiles(new[] { validPath, missingPath }));
        Assert.Empty(library.List());
        Assert.True(File.Exists(validPath));
        Assert.False(File.Exists(missingPath));
    }

    [Fact]
    public void RegisterFilesRejectsEmptyVideoFileBeforePersistingSelection()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var sourceFolder = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceFolder);

        var validPath = Path.Combine(sourceFolder, "valid.mp4");
        var emptyPath = Path.Combine(sourceFolder, "empty.mp4");
        File.WriteAllBytes(validPath, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(emptyPath, Array.Empty<byte>());

        using var library = new LibraryService(libraryRoot, _databasePath);

        Assert.Throws<InvalidOperationException>(() => library.RegisterFiles(new[] { validPath, emptyPath }));
        Assert.Empty(library.List());
        Assert.True(File.Exists(validPath));
        Assert.True(File.Exists(emptyPath));
        Assert.Equal(0, new FileInfo(emptyPath).Length);
    }

    [Fact]
    public void RegisterFilesRejectsEmptySelection()
    {
        using var library = new LibraryService(Path.Combine(_root, "library"), _databasePath);

        Assert.Throws<ArgumentException>(() => library.RegisterFiles(Array.Empty<string>()));
        Assert.Empty(library.List());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
