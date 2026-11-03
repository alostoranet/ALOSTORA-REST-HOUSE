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

        var series = Assert.Single(folders.Where(x => x.FullPath == seriesFolder));
        var season = Assert.Single(folders.Where(x => x.FullPath == seasonFolder));
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

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
