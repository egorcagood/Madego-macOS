using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Madyar.PeerChat;
public sealed record PeerIssuedInvite(string Id,DateTimeOffset ExpiresUtc);
public sealed record PeerContactRequest(string Id,string PeerId,string Nickname,string InviteUri,string InvitationId,DateTimeOffset ReceivedUtc);
internal static class PeerInvitationPolicy {
    internal static DateTimeOffset Expiry(VerifiedContactInvite invite)=>invite.CreatedUtc.AddDays(90);
    internal static void Validate(VerifiedContactInvite invite,bool allowExpired=false) {
        var now=DateTimeOffset.UtcNow;
        if(invite.CreatedUtc>now.AddMinutes(5))throw new InvalidDataException("Дата приглашения находится в будущем.");
        if(!allowExpired&&Expiry(invite)<=now)throw new InvalidDataException("Приглашение истекло. Попросите новый контакт.");
    }
}
internal sealed class PeerIntroductionStore(string directory) {
    static string Time(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    static DateTimeOffset ReadTime(string value)=>DateTimeOffset.ParseExact(value,"O",CultureInfo.InvariantCulture);
    SqliteConnection Open() {
        var path=Path.Combine(directory,"messages.db");PeerFiles.NoLinks(path);
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
        try{db.Open();return db;}catch{db.Dispose();throw;}
    }
    static void Prune(SqliteConnection db,SqliteTransaction? tx=null) {
        using var q=db.CreateCommand();q.Transaction=tx;q.CommandText="DELETE FROM IssuedInvites WHERE ExpiresUtc<=$now";
        q.Parameters.AddWithValue("$now",Time(DateTimeOffset.UtcNow));q.ExecuteNonQuery();
    }
    internal void Register(PeerIssuedInvite invite) {
        using var db=Open();using var tx=db.BeginTransaction();Prune(db,tx);
        using(var q=db.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT INTO IssuedInvites VALUES($id,$expires)";
            q.Parameters.AddWithValue("$id",invite.Id);q.Parameters.AddWithValue("$expires",Time(invite.ExpiresUtc));q.ExecuteNonQuery();}
        using var trim=db.CreateCommand();trim.Transaction=tx;trim.CommandText="DELETE FROM IssuedInvites WHERE rowid NOT IN (SELECT rowid FROM IssuedInvites ORDER BY ExpiresUtc DESC,rowid DESC LIMIT 16)";
        trim.ExecuteNonQuery();tx.Commit();
    }
    internal IReadOnlyList<PeerIssuedInvite> Issued() {
        using var db=Open();Prune(db);using var q=db.CreateCommand();q.CommandText="SELECT Id,ExpiresUtc FROM IssuedInvites ORDER BY ExpiresUtc,rowid LIMIT 17";
        using var r=q.ExecuteReader();var result=new List<PeerIssuedInvite>();while(r.Read()) {
            if(!Guid.TryParseExact(r.GetString(0),"D",out var id)||id==Guid.Empty)throw new InvalidDataException("Повреждено приглашение.");
            result.Add(new(id.ToString("D"),ReadTime(r.GetString(1))));
        }
        if(result.Count>16)throw new InvalidDataException("Превышен лимит приглашений.");return result;
    }
    internal void Revoke(string id) {using var db=Open();using var q=db.CreateCommand();q.CommandText="DELETE FROM IssuedInvites WHERE Id=$id";q.Parameters.AddWithValue("$id",id);q.ExecuteNonQuery();}
    internal PeerAcceptance Add(PeerContactRequest request) {
        using var db=Open();using var tx=db.BeginTransaction();
        using(var q=db.CreateCommand()){q.Transaction=tx;q.CommandText="SELECT COUNT(*) FROM ContactRequests WHERE PeerId=$peer";q.Parameters.AddWithValue("$peer",request.PeerId);
            if((long)q.ExecuteScalar()!>0)return new(true,false);}
        using(var q=db.CreateCommand()){q.Transaction=tx;q.CommandText="SELECT COUNT(*) FROM ContactRequests";if((long)q.ExecuteScalar()!>=16)return new(false,false);}
        using(var q=db.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT INTO ContactRequests VALUES($peer,$id,$nickname,$uri,$invitation,$received)";
            q.Parameters.AddWithValue("$peer",request.PeerId);q.Parameters.AddWithValue("$id",request.Id);q.Parameters.AddWithValue("$nickname",request.Nickname);
            q.Parameters.AddWithValue("$uri",request.InviteUri);q.Parameters.AddWithValue("$invitation",request.InvitationId);q.Parameters.AddWithValue("$received",Time(request.ReceivedUtc));q.ExecuteNonQuery();}
        tx.Commit();return new(true,true);
    }
    internal IReadOnlyList<PeerContactRequest> Requests() {
        using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT Id,PeerId,Nickname,InviteUri,InvitationId,ReceivedUtc FROM ContactRequests ORDER BY rowid LIMIT 17";
        using var r=q.ExecuteReader();var result=new List<PeerContactRequest>();while(r.Read()) {
            var request=new PeerContactRequest(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),ReadTime(r.GetString(5)));
            var invite=PeerInviteCodec.Parse(request.InviteUri);
            if(invite.PeerId!=request.PeerId||invite.Nickname!=request.Nickname||!Guid.TryParseExact(request.Id,"D",out var id)||id==Guid.Empty||!Guid.TryParseExact(request.InvitationId,"D",out var target)||target==Guid.Empty)
                throw new InvalidDataException("Повреждён запрос знакомства.");result.Add(request);
        }
        if(result.Count>16)throw new InvalidDataException("Превышен лимит запросов.");return result;
    }
    internal bool Remove(string peer) {using var db=Open();using var q=db.CreateCommand();q.CommandText="DELETE FROM ContactRequests WHERE PeerId=$peer";q.Parameters.AddWithValue("$peer",peer);return q.ExecuteNonQuery()==1;}
    internal bool IsDismissed(string id) {using var db=Open();using var q=db.CreateCommand();q.CommandText="SELECT InvitationId FROM InstalledInviteDismissal WHERE SingleId=1";return q.ExecuteScalar() as string==id;}
    internal void Dismiss(string id) {using var db=Open();using var tx=db.BeginTransaction();using var q=db.CreateCommand();q.Transaction=tx;
        q.CommandText="INSERT INTO InstalledInviteDismissal VALUES(1,$id) ON CONFLICT(SingleId) DO UPDATE SET InvitationId=excluded.InvitationId";q.Parameters.AddWithValue("$id",id);q.ExecuteNonQuery();tx.Commit();}
}
public sealed partial class PeerWorkspace {
    PeerIntroductionStore Introductions=>new(_directory);
    public Task<IReadOnlyList<PeerIssuedInvite>> ListIssuedInvitesAsync(CancellationToken ct=default)=>Locked(()=>Introductions.Issued(),ct);
    public Task<IReadOnlyList<PeerContactRequest>> ListContactRequestsAsync(CancellationToken ct=default)=>Locked(()=> (IReadOnlyList<PeerContactRequest>)Introductions.Requests().Where(r=>!Contacts.IsBlocked(r.PeerId)).ToArray(),ct);
    public Task RevokeInviteAsync(string id,CancellationToken ct=default)=>Locked(()=>{if(MessageId(id,out var value))Introductions.Revoke(value);return true;},ct);
    public Task<bool> IsInstalledInviteDismissedAsync(string inviteId,CancellationToken ct=default)=>Locked(()=>MessageId(inviteId,out var id)&&Introductions.IsDismissed(id),ct);
    public Task DismissInstalledInviteAsync(string inviteId,CancellationToken ct=default)=>Locked(()=>{if(!MessageId(inviteId,out var id))throw new InvalidDataException("Неверный ID приглашения.");Introductions.Dismiss(id);return true;},ct);
    public async Task<bool> AcceptIntroductionAsync(long generation,string authenticatedPeer,string requestId,string targetInviteId,string signedReturnUri,CancellationToken ct=default)=>(await AcceptIntroductionDetailedAsync(generation,authenticatedPeer,requestId,targetInviteId,signedReturnUri,ct).ConfigureAwait(false)).Accepted;
    public Task<PeerAcceptance> AcceptIntroductionDetailedAsync(long generation,string authenticatedPeer,string requestId,string targetInviteId,string signedReturnUri,CancellationToken ct=default)=>Locked(()=> {
        if(!_linkEnabled||!_linkActive||generation!=_linkGeneration||authenticatedPeer==_identity.PeerId||Contacts.IsBlocked(authenticatedPeer)||!MessageId(requestId,out var id)||!MessageId(targetInviteId,out var target))return new PeerAcceptance(false,false);
        VerifiedContactInvite invite;
        try{invite=PeerInviteCodec.Parse(signedReturnUri);PeerInvitationPolicy.Validate(invite);}catch(Exception e)when(e is InvalidDataException or FormatException or JsonException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException){return new PeerAcceptance(false,false);}
        if(invite.PeerId!=authenticatedPeer||!Introductions.Issued().Any(i=>i.Id==target))return new PeerAcceptance(false,false);
        if(KnownMessagingPeer(authenticatedPeer))return new PeerAcceptance(true,false);
        return Introductions.Add(new(id,authenticatedPeer,invite.Nickname,signedReturnUri,target,DateTimeOffset.UtcNow));
    },ct);
    public Task<bool> AcceptContactRequestAsync(string peer,CancellationToken ct=default)=>Locked(()=> {
        if(Contacts.IsBlocked(peer))return false;var request=Introductions.Requests().SingleOrDefault(r=>r.PeerId==peer);if(request is null)return false;
        var invite=PeerInviteCodec.Parse(request.InviteUri);
        if(invite.PeerId==_identity.PeerId||invite.PeerId!=peer)throw new InvalidDataException("Запрос принадлежит другому контакту.");
        Contacts.Add(new(invite.PeerId,invite.PublicKey,invite.Nickname,null,DateTimeOffset.UtcNow,null));Contacts.RememberInvite(invite);
        return Introductions.Remove(peer);
    },ct);
    public Task<bool> RejectContactRequestAsync(string peer,CancellationToken ct=default)=>Locked(()=>Introductions.Remove(peer),ct);
}
