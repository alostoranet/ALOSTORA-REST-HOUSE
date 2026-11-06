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
        var id=string.IsNullOrWhiteSpace(x.CategoryId)?Guid.NewGuid().ToString("N"):x.CategoryId.Trim();
        var parentId=string.IsNullOrWhiteSpace(x.ParentId)?null:x.ParentId.Trim();

        // The customer catalog supports exactly two levels: a top-level Group and its content subcategories.
        // Reject malformed API requests here so they cannot create published categories that the customer UI cannot display.
        if(x.ContentKind!="Group")
        {
            if(string.Equals(id,parentId,StringComparison.Ordinal))
                throw new InvalidOperationException("لا يمكن أن يكون القسم الفرعي تابعاً لنفسه.");

            var parentIsTopLevelGroup=false;
            using(var parent=c.CreateCommand())
            {
                parent.Transaction=transaction;
                parent.CommandText="SELECT ParentId,ContentKind FROM CatalogCategories WHERE CategoryId=$parent";
                parent.Parameters.AddWithValue("$parent",parentId!);
                using var reader=parent.ExecuteReader();
                if(!reader.Read())
                    throw new InvalidOperationException("القسم الرئيسي المحدد غير موجود.");
                parentIsTopLevelGroup=reader.IsDBNull(0)&&string.Equals(reader.GetString(1),"Group",StringComparison.Ordinal);
            }
            if(!parentIsTopLevelGroup)
                throw new InvalidOperationException("يجب ربط القسم الفرعي بقسم رئيسي من المستوى الأول.");

            if(!string.IsNullOrWhiteSpace(x.CategoryId))
            {
                using var children=c.CreateCommand();
                children.Transaction=transaction;
                children.CommandText="SELECT COUNT(*) FROM CatalogCategories WHERE ParentId=$id";
                children.Parameters.AddWithValue("$id",id);
                if(Convert.ToInt32(children.ExecuteScalar())>0)
                    throw new InvalidOperationException("لا يمكن تحويل قسم رئيسي يحتوي أقساماً فرعية إلى قسم فرعي.");
            }
        }

        if(parentId is null && x.ContentKind=="Group" && !x.IsPublished && !string.IsNullOrWhiteSpace(x.CategoryId))
        {
            using var publishedChildren=c.CreateCommand();
            publishedChildren.Transaction=transaction;
            publishedChildren.CommandText="SELECT COUNT(*) FROM CatalogCategories WHERE ParentId=$id AND IsPublished=1";
            publishedChildren.Parameters.AddWithValue("$id",id);
            if(Convert.ToInt32(publishedChildren.ExecuteScalar())>0)
                throw new InvalidOperationException("لا يمكن إلغاء نشر القسم الرئيسي قبل إلغاء نشر أقسامه الفرعية.");
        }
        if(parentId is null && x.ContentKind=="Group" && !x.IsPublished && !string.IsNullOrWhiteSpace(x.CategoryId))
        {
            using var publishedChildren=c.CreateCommand();
            publishedChildren.Transaction=transaction;
            publishedChildren.CommandText="SELECT COUNT(*) FROM CatalogCategories WHERE ParentId=$id AND IsPublished=1";
            publishedChildren.Parameters.AddWithValue("$id",id);
            if(Convert.ToInt32(publishedChildren.ExecuteScalar())>0)
                throw new InvalidOperationException("لا يمكن إلغاء نشر القسم الرئيسي قبل إلغاء نشر أقسامه الفرعية.");
        }
        using var cmd=c.CreateCommand();
        cmd.Transaction=transaction;
        cmd.CommandText="""
        INSERT INTO CatalogCategories(CategoryId,ParentId,Title,ContentKind,PosterPath,IsPublished,CreatedUtc)
        VALUES($id,$parent,$title,$kind,$poster,$published,$created)
        ON CONFLICT(CategoryId) DO UPDATE SET ParentId=excluded.ParentId,Title=excluded.Title,ContentKind=excluded.ContentKind,
        PosterPath=COALESCE(excluded.PosterPath,CatalogCategories.PosterPath),IsPublished=excluded.IsPublished
        """;
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$parent",(object?)parentId??DBNull.Value);
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
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("معرّف القسم غير صالح", nameof(id));

        using var c = Open();
        using var transaction = c.BeginTransaction();
        var updatedUtc = DateTime.UtcNow.ToString("O");

        // Detach and unpublish content rather than deleting its metadata or playback links.
        // The original media files and series/episode records remain available for reassignment.
        if (TableExists(c, transaction, "VideoMetadata"))
        {
            using var videos = c.CreateCommand();
            videos.Transaction = transaction;
            videos.CommandText = """
                UPDATE VideoMetadata
                SET CategoryId = NULL, IsPublished = 0, UpdatedUtc = $updated
                WHERE CategoryId IN (
                    SELECT CategoryId FROM CatalogCategories
                    WHERE CategoryId = $id OR ParentId = $id
                )
                """;
            videos.Parameters.AddWithValue("$updated", updatedUtc);
            videos.Parameters.AddWithValue("$id", id);
            videos.ExecuteNonQuery();
        }

        if (TableExists(c, transaction, "SeriesCatalog"))
        {
            using var series = c.CreateCommand();
            series.Transaction = transaction;
            series.CommandText = """
                UPDATE SeriesCatalog
                SET CategoryId = NULL, IsPublished = 0, UpdatedUtc = $updated
                WHERE CategoryId IN (
                    SELECT CategoryId FROM CatalogCategories
                    WHERE CategoryId = $id OR ParentId = $id
                )
                """;
            series.Parameters.AddWithValue("$updated", updatedUtc);
            series.Parameters.AddWithValue("$id", id);
            series.ExecuteNonQuery();
        }

        using var cmd = c.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM CatalogCategories WHERE CategoryId = $id";
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException("القسم غير موجود");

        // ParentId has ON DELETE CASCADE; the category tree is removed in this transaction.
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
