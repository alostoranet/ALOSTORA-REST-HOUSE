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
    public void RegisterSelectedFilesIndexesOnlyChosenVideosFromFolder()
    {
        using var library = new LibraryService(Path.Combine(_root, "media"), _databasePath);
        var folder = Path.Combine(_root, "external-selection");
        Directory.CreateDirectory(folder);
        var selectedPath = Path.Combine(folder, "selected.mp4");
        var ignoredPath = Path.Combine(folder, "not-selected.mkv");
        File.WriteAllBytes(selectedPath, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(ignoredPath, new byte[] { 4, 5, 6 });

        Assert.Equal(1, library.RegisterSelectedFiles(folder, new[] { selectedPath }));

        var indexed = Assert.Single(library.List());
        Assert.Equal("selected", indexed.Title);
        Assert.True(File.Exists(selectedPath));
        Assert.True(File.Exists(ignoredPath));
        Assert.DoesNotContain(library.List(), x => x.Title == "not-selected");
    }

    [Fact]
    public void RemovingSelectedVideoFromLibraryNeverDeletesOriginalFile()
    {
        using var library = new LibraryService(Path.Combine(_root, "media"), _databasePath);
        var externalFolder = Path.Combine(_root, "external");
        Directory.CreateDirectory(externalFolder);
        var videoPath = Path.Combine(externalFolder, "keep-this.mp4");
        File.WriteAllBytes(videoPath, new byte[] { 10, 20, 30, 40 });

        Assert.Equal(1, library.RegisterFiles(new[] { videoPath }));
        var video = Assert.Single(library.List());

        library.RemoveFromLibrary(video.Id);

        Assert.True(File.Exists(videoPath));
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, File.ReadAllBytes(videoPath));
        Assert.Empty(library.List());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
