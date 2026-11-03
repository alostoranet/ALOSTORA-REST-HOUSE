using Microsoft.Data.Sqlite;
using System.IO;

namespace Alostora.RestHouse;

public sealed record CatalogCategory(string CategoryId,string? ParentId,string Title,string ContentKind,string? PosterPath,bool IsPublished,string CreatedUtc);
public sealed record SaveCatalogCategoryRequest(string? CategoryId,string? ParentId,string Title,string ContentKind,string? PosterPath,bool IsPublished);

public sealed class CatalogCategoryService
{
    private readonly string _connectionString;
    public CatalogCategoryService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString=new SqliteConnectionStringBuilder{DataSource=databasePath,ForeignKeys=true,DefaultTimeout=10,Pooling=true}.ToString();
        using var c=new SqliteConnection(_connectionString);c.Open();
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
        CREATE TABLE IF NOT EXISTS CatalogCategories(
          CategoryId TEXT PRIMARY KEY, ParentId TEXT NULL REFERENCES CatalogCategories(CategoryId) ON DELETE CASCADE,
          Title TEXT NOT NULL, ContentKind TEXT NOT NULL DEFAULT 'Group', PosterPath TEXT NULL,
          IsPublished INTEGER NOT NULL DEFAULT 0, CreatedUtc TEXT NOT NULL,
          UNIQUE(ParentId,Title)
        );
        CREATE INDEX IF NOT EXISTS IX_CatalogCategories_Parent ON CatalogCategories(ParentId);
        """;
        cmd.ExecuteNonQuery();
        using var fk=c.CreateCommand();fk.CommandText="PRAGMA foreign_keys=ON";fk.ExecuteNonQuery();
    }
    private SqliteConnection Open(){var c=new SqliteConnection(_connectionString);c.Open();return c;}
    public IReadOnlyList<CatalogCategory> List(bool publishedOnly=false)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText = publishedOnly
            ? """
              SELECT c.CategoryId,c.ParentId,c.Title,c.ContentKind,c.PosterPath,c.IsPublished,c.CreatedUtc
              FROM CatalogCategories c
              WHERE c.IsPublished=1
                AND (c.ParentId IS NULL OR EXISTS(
                  SELECT 1 FROM CatalogCategories p
                  WHERE p.CategoryId=c.ParentId AND p.IsPublished=1 AND p.ContentKind='Group'
                ))
              ORDER BY c.CreatedUtc DESC,c.Title COLLATE NOCASE
              """
            : """
              SELECT CategoryId,ParentId,Title,ContentKind,PosterPath,IsPublished,CreatedUtc
              FROM CatalogCategories
              ORDER BY CreatedUtc DESC,Title COLLATE NOCASE
              """;
        using var r=cmd.ExecuteReader();var rows=new List<CatalogCategory>();
        while(r.Read())rows.Add(new(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetInt32(5)!=0,r.GetString(6)));
        return rows;
    }
    public CatalogCategory? Get(string id)
    {
        using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT CategoryId,ParentId,Title,ContentKind,PosterPath,IsPublished,CreatedUtc FROM CatalogCategories WHERE CategoryId=$id";cmd.Parameters.AddWithValue("$id",id);
        using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetInt32(5)!=0,r.GetString(6)):null;
    }
    public void Save(SaveCatalogCategoryRequest x)
    {
        if(string.IsNullOrWhiteSpace(x.Title)||x.Title.Trim().Length>100)throw new ArgumentException("اسم القسم مطلوب ولا يتجاوز 100 حرف");
        if(x.ContentKind is not ("Group" or "Movie" or "Series" or "Other"))throw new ArgumentException("نوع القسم غير صالح");
        if(x.ContentKind=="Group"&&x.ParentId is not null)throw new ArgumentException("القسم الرئيسي لا يمكن أن يكون تابعاً لقسم آخر");
        if(x.ContentKind!="Group"&&string.IsNullOrWhiteSpace(x.ParentId))throw new ArgumentException("اختر القسم الرئيسي أولاً");
        using var c=Open();
        using var transaction=c.BeginTransaction();
        if(x.ParentId is null && x.ContentKind=="Group" && !x.IsPublished && !string.IsNullOrWhiteSpace(x.CategoryId))
        {
            using var publishedChildren=c.CreateCommand();
            publishedChildren.Transaction=transaction;
            publishedChildren.CommandText="SELECT COUNT(*) FROM CatalogCategories WHERE ParentId=$id AND IsPublished=1";
            publishedChildren.Parameters.AddWithValue("$id",x.CategoryId.Trim());
            if(Convert.ToInt32(publishedChildren.ExecuteScalar())>0)
                throw new InvalidOperationException("لا يمكن إلغاء نشر القسم الرئيسي قبل إلغاء نشر أقسامه الفرعية.");
        }
        using var cmd=c.CreateCommand();
        cmd.Transaction=transaction;
        var id=string.IsNullOrWhiteSpace(x.CategoryId)?Guid.NewGuid().ToString("N"):x.CategoryId.Trim();
        cmd.CommandText="""
        INSERT INTO CatalogCategories(CategoryId,ParentId,Title,ContentKind,PosterPath,IsPublished,CreatedUtc)
        VALUES($id,$parent,$title,$kind,$poster,$published,$created)
        ON CONFLICT(CategoryId) DO UPDATE SET ParentId=excluded.ParentId,Title=excluded.Title,ContentKind=excluded.ContentKind,
        PosterPath=COALESCE(excluded.PosterPath,CatalogCategories.PosterPath),IsPublished=excluded.IsPublished
        """;
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$parent",(object?)x.ParentId??DBNull.Value);
        cmd.Parameters.AddWithValue("$title",x.Title.Trim());cmd.Parameters.AddWithValue("$kind",x.ContentKind);
        cmd.Parameters.AddWithValue("$poster",(object?)x.PosterPath??DBNull.Value);cmd.Parameters.AddWithValue("$published",x.IsPublished?1:0);
        cmd.Parameters.AddWithValue("$created",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();transaction.Commit();
    }
    public void SetPoster(string id,string path)
    {
        using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE CatalogCategories SET PosterPath=$path WHERE CategoryId=$id";
        cmd.Parameters.AddWithValue("$path",path);cmd.Parameters.AddWithValue("$id",id);if(cmd.ExecuteNonQuery()==0)throw new KeyNotFoundException("القسم غير موجود");
    }
    public void Delete(string id)
    {
        using var c=Open();
        using var transaction=c.BeginTransaction();
        using (var exists=c.CreateCommand())
        {
            exists.Transaction=transaction;
            exists.CommandText="SELECT COUNT(*) FROM CatalogCategories WHERE CategoryId=$id";
            exists.Parameters.AddWithValue("$id",id);
            if(Convert.ToInt32(exists.ExecuteScalar())==0)throw new KeyNotFoundException("القسم غير موجود");
        }

        // Delete catalog metadata and series links for the complete category branch.
        // Physical video files are intentionally never touched.
        if(TableExists(c,transaction,"VideoMetadata"))
        {
            using var videos=c.CreateCommand();
            videos.Transaction=transaction;
            videos.CommandText="""
                DELETE FROM VideoMetadata
                WHERE CategoryId IN (SELECT CategoryId FROM CatalogCategories WHERE CategoryId=$id OR ParentId=$id)
                   OR VideoId IN (
                     SELECT se.VideoId
                     FROM SeriesEpisodes se
                     JOIN SeriesSeasons ss ON ss.SeasonId=se.SeasonId
                     JOIN SeriesCatalog sc ON sc.SeriesId=ss.SeriesId
                     WHERE sc.CategoryId IN (SELECT CategoryId FROM CatalogCategories WHERE CategoryId=$id OR ParentId=$id)
                   )
                   OR VideoId IN (
                     SELECT sd.VideoId
                     FROM SeriesDirectVideos sd
                     JOIN SeriesCatalog sc ON sc.SeriesId=sd.SeriesId
                     WHERE sc.CategoryId IN (SELECT CategoryId FROM CatalogCategories WHERE CategoryId=$id OR ParentId=$id)
                   );
                """;
            videos.Parameters.AddWithValue("$id",id);
            // Older or partially initialized databases may not have series tables yet.
            if(TableExists(c,transaction,"SeriesCatalog") &&
               TableExists(c,transaction,"SeriesSeasons") &&
               TableExists(c,transaction,"SeriesEpisodes") &&
               TableExists(c,transaction,"SeriesDirectVideos"))
                videos.ExecuteNonQuery();
            else
            {
                using var simple=c.CreateCommand();
                simple.Transaction=transaction;
                simple.CommandText="DELETE FROM VideoMetadata WHERE CategoryId IN (SELECT CategoryId FROM CatalogCategories WHERE CategoryId=$id OR ParentId=$id)";
                simple.Parameters.AddWithValue("$id",id);
                simple.ExecuteNonQuery();
            }
        }

        if(TableExists(c,transaction,"SeriesCatalog"))
        {
            using var shows=c.CreateCommand();
            shows.Transaction=transaction;
            shows.CommandText="DELETE FROM SeriesCatalog WHERE CategoryId IN (SELECT CategoryId FROM CatalogCategories WHERE CategoryId=$id OR ParentId=$id)";
            shows.Parameters.AddWithValue("$id",id);
            shows.ExecuteNonQuery();
        }

        using var cmd=c.CreateCommand();
        cmd.Transaction=transaction;
        cmd.CommandText="DELETE FROM CatalogCategories WHERE CategoryId=$id";
        cmd.Parameters.AddWithValue("$id",id);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    private static bool TableExists(SqliteConnection connection,SqliteTransaction transaction,string table)
    {
        using var cmd=connection.CreateCommand();
        cmd.Transaction=transaction;
        cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name",table);
        return Convert.ToInt32(cmd.ExecuteScalar())>0;
    }
}
