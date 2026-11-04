using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class LibraryServiceFolderTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;

    public LibraryServiceFolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-library-folders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "library.db");
    }

    [Fact]
    public void CreatesAndListsNestedManagedFolders()
    {
        using var library = new LibraryService(Path.Combine(_root, "media"), _databasePath);

        var seriesFolder = library.CreateFolder(null, "المسلسلات");
        var seasonFolder = library.CreateFolder(seriesFolder, "المسلسل الأول");
        var folders = library.ListFolders();

        var series = Assert.Single(folders, x => x.FullPath == seriesFolder);
        var season = Assert.Single(folders, x => x.FullPath == seasonFolder);
        Assert.True(series.IsManaged);
        Assert.True(season.IsManaged);
        Assert.Equal(seriesFolder, season.ParentPath);
    }

    [Fact]
    public void RejectsPathTraversalAndFolderCreationOutsideManagedLibrary()
    {
        using var library = new LibraryService(Path.Combine(_root, "media"), _databasePath);
        var externalFolder = Path.Combine(_root, "external");
        Directory.CreateDirectory(externalFolder);

        Assert.Throws<ArgumentException>(() => library.CreateFolder(null, ".."));
        Assert.Throws<InvalidOperationException>(() => library.CreateFolder(externalFolder, "new-folder"));
        Assert.False(Directory.Exists(Path.Combine(externalFolder, "new-folder")));
    }


    [Fact]
    public void UnregistersSelectedExternalFolderWithoutDeletingOriginalVideos()
    {
        using var library = new LibraryService(Path.Combine(_root, "media"), _databasePath);
        var externalFolder = Path.Combine(_root, "external");
        Directory.CreateDirectory(externalFolder);
        var videoPath = Path.Combine(externalFolder, "episode.mp4");
        File.WriteAllBytes(videoPath, new byte[] { 0, 1, 2, 3, 4 });

        Assert.Equal(1, library.RegisterSelectedFiles(externalFolder, new[] { videoPath }));
        Assert.Contains(library.ListFolders(), x => x.FullPath == externalFolder && !x.IsManaged);
        Assert.Single(library.List());

        Assert.Equal(1, library.UnregisterFolder(externalFolder));

        Assert.True(File.Exists(videoPath));
        Assert.Empty(library.List());
        Assert.DoesNotContain(library.ListFolders(), x => x.FullPath == externalFolder);
    }

    [Fact]
    public void CannotUnregisterTheManagedLibraryRoot()
    {
        var mediaRoot = Path.Combine(_root, "media");
        using var library = new LibraryService(mediaRoot, _databasePath);

        Assert.Throws<InvalidOperationException>(() => library.UnregisterFolder(mediaRoot));
        Assert.True(Directory.Exists(mediaRoot));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
