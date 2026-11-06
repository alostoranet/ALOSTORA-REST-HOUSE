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
    public void SelectingAnExternalFolderRegistersOnlyTheFilesTheUserChecked()
    {
        var mediaRoot = Path.Combine(_root, "media");
        var externalFolder = Path.Combine(_root, "external-selection");
        Directory.CreateDirectory(externalFolder);
        var first = Path.Combine(externalFolder, "first.mp4");
        var selected = Path.Combine(externalFolder, "selected.mkv");
        var third = Path.Combine(externalFolder, "third.avi");
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(selected, [4, 5, 6]);
        File.WriteAllBytes(third, [7, 8, 9]);

        using var library = new LibraryService(mediaRoot, _databasePath);
        var added = library.RegisterSelectedFiles(externalFolder, new[] { selected });

        Assert.Equal(1, added);
        var videos = library.List();
        var onlyVideo = Assert.Single(videos);
        Assert.Equal("selected.mkv", onlyVideo.FileName);
        Assert.DoesNotContain(videos, video => video.FileName == "first.mp4" || video.FileName == "third.avi");
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(selected));
        Assert.True(File.Exists(third));
    }

    [Fact]
    public void PreviewFolderFindsNestedVideosWithoutRegisteringThemInTheLibrary()
    {
        var mediaRoot = Path.Combine(_root, "media");
        var externalFolder = Path.Combine(_root, "external-preview");
        var nestedFolder = Path.Combine(externalFolder, "season-1", "disc-1");
        Directory.CreateDirectory(nestedFolder);
        var first = Path.Combine(externalFolder, "episode-01.mp4");
        var nested = Path.Combine(nestedFolder, "episode-02.mkv");
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(nested, [4, 5, 6]);
        File.WriteAllText(Path.Combine(nestedFolder, "readme.txt"), "not a video");

        using var library = new LibraryService(mediaRoot, _databasePath);
        var preview = library.PreviewFolder(externalFolder);

        Assert.Equal(2, preview.Count);
        Assert.Contains(preview, x => x.FullPath == first);
        Assert.Contains(preview, x => x.FullPath == nested);
        Assert.Empty(library.List());
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(nested));
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
