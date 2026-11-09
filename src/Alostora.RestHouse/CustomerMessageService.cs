using Microsoft.Data.Sqlite;
using System.IO;

namespace Alostora.RestHouse;

public sealed record CreateCustomerMessageRequest(string Category,string Title,string Details,string? Contact,string? ViewerId);
public sealed record CustomerMessage(
    long Id,string Category,string Title,string Details,string? Contact,string? ViewerId,
    string Status,string CreatedUtc,string UpdatedUtc,string? AdminReply=null,string? AdminReplyUtc=null);

public sealed class CustomerMessageService
{
    private readonly string _connectionString;

    public CustomerMessageService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString=new SqliteConnectionStringBuilder
        {
            DataSource=databasePath,ForeignKeys=true,DefaultTimeout=10,Pooling=true
        }.ToString();
        using var c=Open();
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
        CREATE TABLE IF NOT EXISTS CustomerMessages(
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          Category TEXT NOT NULL,
          Title TEXT NOT NULL,
          Details TEXT NOT NULL,
          Contact TEXT NULL,
          ViewerId TEXT NULL,
          Status TEXT NOT NULL DEFAULT 'جديد',
          CreatedUtc TEXT NOT NULL,
          UpdatedUtc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_CustomerMessages_CreatedUtc ON CustomerMessages(CreatedUtc DESC);
        CREATE INDEX IF NOT EXISTS IX_CustomerMessages_Status ON CustomerMessages(Status);
        """;
        cmd.ExecuteNonQuery();
        EnsureColumn(c,"AdminReply","TEXT NULL");
        EnsureColumn(c,"AdminReplyUtc","TEXT NULL");
    }

    private static void EnsureColumn(SqliteConnection connection,string name,string definition)
    {
        var exists=false;
        using(var check=connection.CreateCommand())
        {
            check.CommandText="PRAGMA table_info(CustomerMessages)";
            using var reader=check.ExecuteReader();
            while(reader.Read())
                if(string.Equals(reader.GetString(1),name,StringComparison.OrdinalIgnoreCase)){exists=true;break;}
        }
        if(exists)return;
        using var alter=connection.CreateCommand();
        alter.CommandText=$"ALTER TABLE CustomerMessages ADD COLUMN {name} {definition}";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open(){var c=new SqliteConnection(_connectionString);c.Open();return c;}

    public long Create(CreateCustomerMessageRequest request)
    {
        var now=DateTime.UtcNow.ToString("O");
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="""
        INSERT INTO CustomerMessages(Category,Title,Details,Contact,ViewerId,Status,CreatedUtc,UpdatedUtc)
        VALUES($category,$title,$details,$contact,$viewer,'جديد',$created,$updated);
        SELECT last_insert_rowid();
        """;
        cmd.Parameters.AddWithValue("$category",request.Category.Trim());
        cmd.Parameters.AddWithValue("$title",request.Title.Trim());
        cmd.Parameters.AddWithValue("$details",request.Details.Trim());
        cmd.Parameters.AddWithValue("$contact",(object?)request.Contact?.Trim()??DBNull.Value);
        cmd.Parameters.AddWithValue("$viewer",(object?)request.ViewerId?.Trim()??DBNull.Value);
        cmd.Parameters.AddWithValue("$created",now);cmd.Parameters.AddWithValue("$updated",now);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<CustomerMessage> List(int limit=300)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="""
        SELECT Id,Category,Title,Details,Contact,ViewerId,Status,CreatedUtc,UpdatedUtc,AdminReply,AdminReplyUtc
        FROM CustomerMessages ORDER BY Id DESC LIMIT $limit
        """;
        cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,1000));
        using var r=cmd.ExecuteReader();var rows=new List<CustomerMessage>();
        while(r.Read())rows.Add(new CustomerMessage(
            r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),
            r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),
            r.GetString(6),r.GetString(7),r.GetString(8),
            r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:r.GetString(10)));
        return rows;
    }

    public bool SetStatus(long id,string status)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE CustomerMessages SET Status=$status,UpdatedUtc=$updated WHERE Id=$id";
        cmd.Parameters.AddWithValue("$status",status);cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id",id);return cmd.ExecuteNonQuery()>0;
    }

    public bool SetAdminReply(long id,string reply)
    {
        var text=reply.Trim();
        if(text.Length<1||text.Length>2500) throw new ArgumentException("يجب أن يكون الرد بين حرف واحد و2500 حرف.");
        using var c=Open();using var cmd=c.CreateCommand();
        var now=DateTime.UtcNow.ToString("O");
        cmd.CommandText="UPDATE CustomerMessages SET AdminReply=$reply,AdminReplyUtc=$updated,UpdatedUtc=$updated WHERE Id=$id";
        cmd.Parameters.AddWithValue("$reply",text);
        cmd.Parameters.AddWithValue("$updated",now);
        cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteNonQuery()>0;
    }

    public CustomerMessage? GetForViewer(long id,string viewerId)
    {
        if(string.IsNullOrWhiteSpace(viewerId)) return null;
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="""
        SELECT Id,Category,Title,Details,Contact,ViewerId,Status,CreatedUtc,UpdatedUtc,AdminReply,AdminReplyUtc
        FROM CustomerMessages WHERE Id=$id AND ViewerId=$viewer
        """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$viewer",viewerId.Trim());
        using var r=cmd.ExecuteReader();
        if(!r.Read()) return null;
        return new CustomerMessage(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),
            r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),
            r.GetString(6),r.GetString(7),r.GetString(8),
            r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:r.GetString(10));
    }

    public bool Delete(long id)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="DELETE FROM CustomerMessages WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id",id);return cmd.ExecuteNonQuery()>0;
    }
}
