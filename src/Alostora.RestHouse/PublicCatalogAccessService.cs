using Microsoft.Data.Sqlite;
using System.IO;

namespace Alostora.RestHouse;

/// <summary>
/// Enforces publication rules at the HTTP boundary. Hidden library files are not
/// discoverable or streamable unless their movie/category or series/category is published.
/// </summary>
public sealed class PublicCatalogAccessService
{
    private readonly string _connectionString;

    public PublicCatalogAccessService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 10,
            Pooling = true
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public bool IsVideoPublished(string videoId)
    {
        if (!IsValidVideoId(videoId)) return false;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              EXISTS(
                SELECT 1
                FROM VideoMetadata vm
                JOIN CatalogCategories child ON child.CategoryId=vm.CategoryId
                JOIN CatalogCategories parent ON parent.CategoryId=child.ParentId
                WHERE vm.VideoId=$id
                  AND vm.IsPublished=1
                  AND parent.IsPublished=1
                  AND parent.ContentKind='Group'
                  AND child.IsPublished=1
                  AND vm.Kind<>'Series'
                  AND child.ContentKind IN ('Movie','Other')
              )
              OR
              EXISTS(
                SELECT 1
                FROM SeriesEpisodes episode
                JOIN SeriesSeasons season ON season.SeasonId=episode.SeasonId
                JOIN SeriesCatalog series ON series.SeriesId=season.SeriesId
                JOIN CatalogCategories child ON child.CategoryId=series.CategoryId
                JOIN CatalogCategories parent ON parent.CategoryId=child.ParentId
                WHERE episode.VideoId=$id
                  AND series.IsPublished=1
                  AND child.IsPublished=1
                  AND child.ContentKind='Series'
                  AND parent.IsPublished=1
                  AND parent.ContentKind='Group'
              )
            """;
        command.Parameters.AddWithValue("$id", videoId);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public IReadOnlySet<string> GetPublishedVideoIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vm.VideoId
            FROM VideoMetadata vm
            JOIN CatalogCategories child ON child.CategoryId=vm.CategoryId
            JOIN CatalogCategories parent ON parent.CategoryId=child.ParentId
            WHERE vm.IsPublished=1
              AND parent.IsPublished=1
              AND parent.ContentKind='Group'
              AND child.IsPublished=1
              AND vm.Kind<>'Series'
              AND child.ContentKind IN ('Movie','Other')
            UNION
            SELECT episode.VideoId
            FROM SeriesEpisodes episode
            JOIN SeriesSeasons season ON season.SeasonId=episode.SeasonId
            JOIN SeriesCatalog series ON series.SeriesId=season.SeriesId
            JOIN CatalogCategories child ON child.CategoryId=series.CategoryId
            JOIN CatalogCategories parent ON parent.CategoryId=child.ParentId
            WHERE series.IsPublished=1
              AND child.IsPublished=1
              AND child.ContentKind='Series'
              AND parent.IsPublished=1
              AND parent.ContentKind='Group'
            """;

        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public IReadOnlySet<string> GetPublishedSeriesIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT series.SeriesId
            FROM SeriesCatalog series
            JOIN CatalogCategories child ON child.CategoryId=series.CategoryId
            JOIN CatalogCategories parent ON parent.CategoryId=child.ParentId
            WHERE series.IsPublished=1
              AND child.IsPublished=1
              AND child.ContentKind='Series'
              AND parent.IsPublished=1
              AND parent.ContentKind='Group'
            """;

        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static bool IsValidVideoId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 24 &&
        value.All(Uri.IsHexDigit);
}
