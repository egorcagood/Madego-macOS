using Microsoft.Data.Sqlite;
using System.Globalization;
namespace Madyar.PeerChat;
internal sealed class PeerContactStore(string directory) {
    SqliteConnection Open() {
        var path = Path.Combine(directory, "contacts.db"); PeerFiles.NoLinks(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, Pooling=false, ForeignKeys=true }.ToString());
        db.Open(); return db;
    }
    internal void Initialize() {
        using var db = Open(); using var q = db.CreateCommand();
        q.CommandText = "CREATE TABLE IF NOT EXISTS Contacts(PeerId TEXT PRIMARY KEY,PublicKey BLOB NOT NULL,Nickname TEXT NOT NULL,Alias TEXT NULL,AddedUtc TEXT NOT NULL,LastSeenUtc TEXT NULL); CREATE TABLE IF NOT EXISTS Drafts(PeerId TEXT PRIMARY KEY REFERENCES Contacts(PeerId) ON DELETE CASCADE,Text TEXT NOT NULL); CREATE TABLE IF NOT EXISTS BlockedContacts(PeerId TEXT PRIMARY KEY,PublicKey BLOB NOT NULL,Nickname TEXT NOT NULL,BlockedUtc TEXT NOT NULL); CREATE TABLE IF NOT EXISTS ContactInvites(PeerId TEXT PRIMARY KEY REFERENCES Contacts(PeerId) ON DELETE CASCADE,InviteUri TEXT NOT NULL);";
        q.ExecuteNonQuery();
    }
    internal IReadOnlyList<PeerContact> List() {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT PeerId,PublicKey,Nickname,Alias,AddedUtc,LastSeenUtc FROM Contacts WHERE PeerId NOT IN (SELECT PeerId FROM BlockedContacts) ORDER BY AddedUtc,PeerId LIMIT 10001";
        using var r=q.ExecuteReader();var result=new List<PeerContact>();
        while(r.Read()) {
            var c=new PeerContact(r.GetString(0),(byte[])r[1],r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),DateTimeOffset.ParseExact(r.GetString(4),"O",CultureInfo.InvariantCulture),r.IsDBNull(5)?null:DateTimeOffset.ParseExact(r.GetString(5),"O",CultureInfo.InvariantCulture));
            Validate(c);result.Add(c);
        }
        if(result.Count>10000)throw new InvalidDataException("Слишком много контактов.");return result;
    }
    internal static void Validate(PeerContact c) {
        if(!PeerIdCodec.Matches(c.PeerId,c.PublicKey)||PeerFiles.Name(c.Nickname)!=c.Nickname||(c.Alias!=null&&PeerFiles.Name(c.Alias)!=c.Alias)||c.LastSeenUtc!=null)throw new InvalidDataException("Повреждённый контакт.");
    }
    internal bool Add(PeerContact contact) {
        Validate(contact);if(IsBlocked(contact.PeerId))throw new InvalidOperationException("Контакт заблокирован. Сначала снимите блокировку в настройках чата.");using var db=Open();using var tx=db.BeginTransaction();
        using var exists=db.CreateCommand();exists.Transaction=tx;exists.CommandText="SELECT COUNT(*) FROM Contacts WHERE PeerId=$id";exists.Parameters.AddWithValue("$id",contact.PeerId);
        if((long)exists.ExecuteScalar()!>0)return false;
        using var count=db.CreateCommand();count.Transaction=tx;count.CommandText="SELECT COUNT(*) FROM Contacts";if((long)count.ExecuteScalar()!>=10000)throw new InvalidDataException("Достигнут лимит локальных контактов (10000).");
        using var q=db.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO Contacts VALUES($id,$key,$name,$alias,$added,NULL)";
        q.Parameters.AddWithValue("$id",contact.PeerId);q.Parameters.AddWithValue("$key",contact.PublicKey);q.Parameters.AddWithValue("$name",contact.Nickname);q.Parameters.AddWithValue("$alias",(object?)contact.Alias??DBNull.Value);q.Parameters.AddWithValue("$added",contact.AddedUtc.ToString("O",CultureInfo.InvariantCulture));q.ExecuteNonQuery();tx.Commit();return true;
    }
    internal void RememberInvite(VerifiedContactInvite invite) {
        if(invite.SignedUri is not {} uri)return;
        var verified=PeerInviteCodec.Parse(uri);if(verified.PeerId!=invite.PeerId)throw new InvalidDataException("Приглашение другого контакта.");
        using var db=Open();using var q=db.CreateCommand();q.CommandText="INSERT INTO ContactInvites VALUES($id,$uri) ON CONFLICT(PeerId) DO UPDATE SET InviteUri=excluded.InviteUri";q.Parameters.AddWithValue("$id",invite.PeerId);q.Parameters.AddWithValue("$uri",uri);q.ExecuteNonQuery();
    }
    internal VerifiedContactInvite? ReadInvite(string peer) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT InviteUri FROM ContactInvites WHERE PeerId=$id";q.Parameters.AddWithValue("$id",peer);
        if(q.ExecuteScalar() is not string uri)return null;var invite=PeerInviteCodec.Parse(uri);if(invite.PeerId!=peer)throw new InvalidDataException("Приглашение другого контакта.");return invite;
    }
    internal bool IsBlocked(string peer) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT COUNT(*) FROM BlockedContacts WHERE PeerId=$id";q.Parameters.AddWithValue("$id",peer);return (long)q.ExecuteScalar()!>0;
    }
    internal static void ValidateBlock(PeerBlockedContact c) {
        if(!PeerIdCodec.Matches(c.PeerId,c.PublicKey)||PeerFiles.Name(c.Nickname)!=c.Nickname||c.BlockedUtc.Offset!=TimeSpan.Zero)throw new InvalidDataException("Повреждена блокировка контакта.");
    }
    internal IReadOnlyList<PeerBlockedContact> Blocked() {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT PeerId,PublicKey,Nickname,BlockedUtc FROM BlockedContacts ORDER BY BlockedUtc,PeerId LIMIT 10001";
        using var r=q.ExecuteReader();var list=new List<PeerBlockedContact>();
        while(r.Read()){var c=new PeerBlockedContact(r.GetString(0),(byte[])r[1],r.GetString(2),DateTimeOffset.ParseExact(r.GetString(3),"O",CultureInfo.InvariantCulture));ValidateBlock(c);list.Add(c);}
        if(list.Count>10000)throw new InvalidDataException("Превышен лимит блокировок.");return list;
    }
    internal bool Remove(string peer) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="DELETE FROM Contacts WHERE PeerId=$id";q.Parameters.AddWithValue("$id",peer);return q.ExecuteNonQuery()==1;
    }
    internal bool Unblock(string peer) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="DELETE FROM BlockedContacts WHERE PeerId=$id";q.Parameters.AddWithValue("$id",peer);return q.ExecuteNonQuery()==1;
    }
    internal bool Block(PeerBlockedContact c) {
        ValidateBlock(c);using var db=Open();using var tx=db.BeginTransaction();
        using(var exists=db.CreateCommand()){exists.Transaction=tx;exists.CommandText="SELECT COUNT(*) FROM BlockedContacts WHERE PeerId=$id";exists.Parameters.AddWithValue("$id",c.PeerId);if((long)exists.ExecuteScalar()!>0)return false;}
        using(var count=db.CreateCommand()){count.Transaction=tx;count.CommandText="SELECT COUNT(*) FROM BlockedContacts";if((long)count.ExecuteScalar()!>=10000)throw new InvalidDataException("Достигнут лимит блокировок.");}
        using(var q=db.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT INTO BlockedContacts VALUES($id,$key,$name,$utc); DELETE FROM Contacts WHERE PeerId=$id";q.Parameters.AddWithValue("$id",c.PeerId);q.Parameters.AddWithValue("$key",c.PublicKey);q.Parameters.AddWithValue("$name",c.Nickname);q.Parameters.AddWithValue("$utc",c.BlockedUtc.ToString("O",CultureInfo.InvariantCulture));q.ExecuteNonQuery();}
        tx.Commit();return true;
    }
    internal void Alias(string id,string? alias) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="UPDATE Contacts SET Alias=$alias WHERE PeerId=$id";q.Parameters.AddWithValue("$alias",(object?)alias??DBNull.Value);q.Parameters.AddWithValue("$id",id);
        if(q.ExecuteNonQuery()!=1)throw new InvalidDataException("Контакт не найден.");
    }
    internal string Draft(string id) {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT Text FROM Drafts WHERE PeerId=$id";q.Parameters.AddWithValue("$id",id);return q.ExecuteScalar() as string??"";
    }
    internal void SaveDraft(string id,string text) {
        if(text.Length>2000)throw new InvalidDataException("Черновик не может быть длиннее 2000 символов.");
        _=new System.Text.UTF8Encoding(false,true).GetBytes(text);
        using var db=Open();using var q=db.CreateCommand();q.CommandText="INSERT INTO Drafts VALUES($id,$text) ON CONFLICT(PeerId) DO UPDATE SET Text=excluded.Text";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$text",text);q.ExecuteNonQuery();
    }
}
public sealed partial class PeerWorkspace {
    PeerContactStore Contacts => new(_directory);
    public Task<IReadOnlyList<PeerContact>> ListContactsAsync(CancellationToken ct=default)=>Locked(()=>Contacts.List(),ct);
    public Task<bool> AddContactAsync(VerifiedContactInvite invite,CancellationToken ct=default)=>Locked(()=> {
        if(invite.PeerId==_identity.PeerId)throw new InvalidDataException("Это ваш собственный контакт.");
        var added=Contacts.Add(new(invite.PeerId,invite.PublicKey,invite.Nickname,null,DateTimeOffset.UtcNow,null));Contacts.RememberInvite(invite);return added;
    },ct);
    public Task<VerifiedContactInvite?> GetContactInviteAsync(string peer,CancellationToken ct=default)=>Locked(()=>KnownMessagingPeer(peer)?Contacts.ReadInvite(peer):null,ct);
    public Task<bool> IsBlockedAsync(string peer,CancellationToken ct=default)=>Locked(()=>Contacts.IsBlocked(peer),ct);
    public Task<IReadOnlyList<PeerBlockedContact>> ListBlockedContactsAsync(CancellationToken ct=default)=>Locked(()=>Contacts.Blocked(),ct);
    public Task<bool> RemoveContactAsync(string peer,CancellationToken ct=default)=>Locked(()=>{var removed=Contacts.Remove(peer);Introductions.Remove(peer);return removed;},ct);
    public Task<bool> BlockContactAsync(string peer,CancellationToken ct=default)=>Locked(()=>{
        if(peer==_identity.PeerId)throw new InvalidDataException("Это ваш собственный контакт.");
        if(Contacts.IsBlocked(peer))return false;
        var c=Contacts.List().SingleOrDefault(c=>c.PeerId==peer);
        PeerBlockedContact blocked;
        if(c is not null)blocked=new(peer,c.PublicKey,c.Alias??c.Nickname,DateTimeOffset.UtcNow);
        else {var request=Introductions.Requests().SingleOrDefault(r=>r.PeerId==peer)??throw new InvalidDataException("Контакт не найден.");var invite=PeerInviteCodec.Parse(request.InviteUri);blocked=new(peer,invite.PublicKey,invite.Nickname,DateTimeOffset.UtcNow);}
        var changed=Contacts.Block(blocked);Introductions.Remove(peer);return changed;
    },ct);
    public Task<bool> UnblockContactAsync(string peer,CancellationToken ct=default)=>Locked(()=>Contacts.Unblock(peer),ct);
    public Task<bool> IsKnownContactAsync(string peer,CancellationToken ct=default)=>Locked(()=>KnownMessagingPeer(peer),ct);
    public Task SetAliasAsync(string peerId,string? alias,CancellationToken ct=default)=>Locked(()=>{Contacts.Alias(peerId,string.IsNullOrWhiteSpace(alias)?null:PeerFiles.Name(alias));return true;},ct);
    public Task<string> GetDraftAsync(string peerId,CancellationToken ct=default)=>Locked(()=>Contacts.Draft(peerId),ct);
    public Task SaveDraftAsync(string peerId,string text,CancellationToken ct=default)=>Locked(()=>{Contacts.SaveDraft(peerId,text);return true;},ct);
}
