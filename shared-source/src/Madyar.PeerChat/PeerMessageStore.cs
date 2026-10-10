using System.Globalization;
using Microsoft.Data.Sqlite;
namespace Madyar.PeerChat;
internal sealed class PeerMessageStore(string directory) {
    SqliteConnection Open() {
        var path=Path.Combine(directory,"messages.db"); PeerFiles.NoLinks(path);
        var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, Pooling=false }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    internal (bool Enabled,long Generation) Initialize(string owner,long floor) {
        using var db=Open();using var tx=db.BeginTransaction();using var schema=db.CreateCommand();schema.Transaction=tx;
        schema.CommandText="""
            CREATE TABLE IF NOT EXISTS LinkState(SingleId INTEGER PRIMARY KEY CHECK(SingleId=1),SchemaVersion INTEGER NOT NULL,OwnerPeerId TEXT NOT NULL,Enabled INTEGER NOT NULL CHECK(Enabled IN (0,1)),Generation INTEGER NOT NULL CHECK(Generation>=0));
            CREATE TABLE IF NOT EXISTS Messages(PeerId TEXT NOT NULL,Id TEXT NOT NULL,Outgoing INTEGER NOT NULL CHECK(Outgoing IN (0,1)),Kind TEXT NOT NULL CHECK(Kind IN ('text','server')),Text TEXT NOT NULL,CreatedUtc TEXT NOT NULL,Status TEXT NOT NULL CHECK(Status IN ('Pending','Delivered','Failed','Received')),PRIMARY KEY(PeerId,Id));
            CREATE TABLE IF NOT EXISTS IssuedInvites(Id TEXT PRIMARY KEY,ExpiresUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ContactRequests(PeerId TEXT PRIMARY KEY,Id TEXT NOT NULL,Nickname TEXT NOT NULL,InviteUri TEXT NOT NULL,InvitationId TEXT NOT NULL,ReceivedUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS NotificationPreference(SingleId INTEGER PRIMARY KEY CHECK(SingleId=1),Enabled INTEGER NOT NULL CHECK(Enabled IN (0,1)));
            CREATE TABLE IF NOT EXISTS InstalledInviteDismissal(SingleId INTEGER PRIMARY KEY CHECK(SingleId=1),InvitationId TEXT NOT NULL);
            """;
        schema.ExecuteNonQuery();
        using(var ownership=db.CreateCommand()) {
            ownership.Transaction=tx;ownership.CommandText="SELECT (SELECT COUNT(*) FROM LinkState WHERE SingleId=1),(SELECT COUNT(*) FROM Messages)+(SELECT COUNT(*) FROM IssuedInvites)+(SELECT COUNT(*) FROM ContactRequests)+(SELECT COUNT(*) FROM InstalledInviteDismissal)+(SELECT COUNT(*) FROM NotificationPreference)";using var r=ownership.ExecuteReader();r.Read();
            if(r.GetInt64(0)==0&&r.GetInt64(1)>0)throw new InvalidDataException("История сообщений не связана с профилем. Исходный файл сохранён.");
        }
        using var insert=db.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT OR IGNORE INTO LinkState VALUES(1,1,$owner,0,0)";insert.Parameters.AddWithValue("$owner",owner);insert.ExecuteNonQuery();
        bool enabled;long generation;
        using(var read=db.CreateCommand()) {
            read.Transaction=tx;read.CommandText="SELECT SchemaVersion,OwnerPeerId,Enabled,Generation FROM LinkState WHERE SingleId=1";using var r=read.ExecuteReader();
            if(!r.Read()||r.GetInt32(0)!=1||r.GetString(1)!=owner)throw new InvalidDataException("История сообщений принадлежит другому профилю или имеет неподдерживаемый формат.");
            enabled=r.GetInt32(2)==1;generation=checked(Math.Max(floor,r.GetInt64(3))+1);
        }
        using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE LinkState SET Generation=$generation WHERE SingleId=1";update.Parameters.AddWithValue("$generation",generation);update.ExecuteNonQuery();
        using var pending=db.CreateCommand();pending.Transaction=tx;pending.CommandText="UPDATE Messages SET Status='Failed' WHERE Outgoing=1 AND Status='Pending'";pending.ExecuteNonQuery();
        using var preference=db.CreateCommand();preference.Transaction=tx;preference.CommandText="INSERT OR IGNORE INTO NotificationPreference VALUES(1,1)";preference.ExecuteNonQuery();
        tx.Commit();return(enabled,generation);
    }
    internal void SetEnabled(bool enabled,long generation) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="UPDATE LinkState SET Enabled=$enabled,Generation=$generation WHERE SingleId=1";
        q.Parameters.AddWithValue("$enabled",enabled?1:0);q.Parameters.AddWithValue("$generation",generation);
        if(q.ExecuteNonQuery()!=1)throw new InvalidDataException("Повреждено состояние чата.");
    }
    internal bool Add(PeerMessage message,bool deduplicate)=>AddResult(message,deduplicate).Accepted;
    internal PeerAcceptance AddResult(PeerMessage message,bool deduplicate) {
        using var db=Open();using var tx=db.BeginTransaction();
        using(var existing=db.CreateCommand()) {
            existing.Transaction=tx;existing.CommandText="SELECT Outgoing,Kind,Text FROM Messages WHERE PeerId=$peer AND Id=$id";
            existing.Parameters.AddWithValue("$peer",message.PeerId);existing.Parameters.AddWithValue("$id",message.Id);using var r=existing.ExecuteReader();
            if(r.Read())return new(deduplicate&&!r.GetBoolean(0)&&r.GetString(1)==message.Kind&&r.GetString(2)==message.Text,false);
        }
        using(var insert=db.CreateCommand()) {
            insert.Transaction=tx;insert.CommandText="INSERT INTO Messages(PeerId,Id,Outgoing,Kind,Text,CreatedUtc,Status) VALUES($peer,$id,$outgoing,$kind,$text,$created,$status)";
            insert.Parameters.AddWithValue("$peer",message.PeerId);insert.Parameters.AddWithValue("$id",message.Id);insert.Parameters.AddWithValue("$outgoing",message.Outgoing?1:0);insert.Parameters.AddWithValue("$kind",message.Kind);insert.Parameters.AddWithValue("$text",message.Text);insert.Parameters.AddWithValue("$created",message.CreatedUtc.ToString("O",CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$status",message.Status);insert.ExecuteNonQuery();
        }
        using var prune=db.CreateCommand();prune.Transaction=tx;prune.CommandText="DELETE FROM Messages WHERE PeerId=$peer AND rowid NOT IN (SELECT rowid FROM Messages WHERE PeerId=$peer ORDER BY rowid DESC LIMIT 500)";prune.Parameters.AddWithValue("$peer",message.PeerId);prune.ExecuteNonQuery();tx.Commit();return new(true,true);
    }
    internal bool NotificationsEnabled() {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT Enabled FROM NotificationPreference WHERE SingleId=1";
        var value=q.ExecuteScalar();if(value is not long flag||flag is not (0 or 1))throw new InvalidDataException("Повреждены настройки уведомлений.");return flag==1;
    }
    internal void SetNotifications(bool enabled) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="UPDATE NotificationPreference SET Enabled=$enabled WHERE SingleId=1";q.Parameters.AddWithValue("$enabled",enabled?1:0);
        if(q.ExecuteNonQuery()!=1)throw new InvalidDataException("Повреждены настройки уведомлений.");
    }
    internal void MarkOutgoing(string peer,string id,bool delivered) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="UPDATE Messages SET Status=$status WHERE PeerId=$peer AND Id=$id AND Outgoing=1 AND (Status!='Delivered' OR $status='Delivered')";
        q.Parameters.AddWithValue("$status",delivered?"Delivered":"Failed");q.Parameters.AddWithValue("$peer",peer);q.Parameters.AddWithValue("$id",id);q.ExecuteNonQuery();
    }
    internal PeerMessage PrepareRetry(string peer,string id) {
        using var db=Open();using var tx=db.BeginTransaction();PeerMessage message;
        using(var q=db.CreateCommand()) {
            q.Transaction=tx;q.CommandText="SELECT Id,PeerId,Outgoing,Kind,Text,CreatedUtc,Status FROM Messages WHERE PeerId=$peer AND Id=$id";
            q.Parameters.AddWithValue("$peer",peer);q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();
            if(!r.Read()||!r.GetBoolean(2))throw new InvalidDataException("Исходящее сообщение не найдено.");
            message=new(r.GetString(0),r.GetString(1),true,r.GetString(3),r.GetString(4),DateTimeOffset.ParseExact(r.GetString(5),"O",CultureInfo.InvariantCulture),r.GetString(6));
        }
        if(message.Status=="Delivered")return message;
        using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE Messages SET Status='Pending' WHERE PeerId=$peer AND Id=$id";
        update.Parameters.AddWithValue("$peer",peer);update.Parameters.AddWithValue("$id",id);update.ExecuteNonQuery();tx.Commit();return message with {Status="Pending"};
    }
    internal IReadOnlyList<PeerMessage> List(string peer) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT Id,PeerId,Outgoing,Kind,Text,CreatedUtc,Status FROM Messages WHERE PeerId=$peer ORDER BY rowid";q.Parameters.AddWithValue("$peer",peer);
        using var r=q.ExecuteReader();var result=new List<PeerMessage>();while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetBoolean(2),r.GetString(3),r.GetString(4),DateTimeOffset.ParseExact(r.GetString(5),"O",CultureInfo.InvariantCulture),r.GetString(6)));return result;
    }
    internal void Clear(string peer) {using var db=Open();using var q=db.CreateCommand();q.CommandText="DELETE FROM Messages WHERE PeerId=$peer";q.Parameters.AddWithValue("$peer",peer);q.ExecuteNonQuery();}
}
