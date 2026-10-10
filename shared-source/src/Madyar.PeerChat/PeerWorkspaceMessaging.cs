using System.Text;
namespace Madyar.PeerChat;
public sealed partial class PeerWorkspace {
    bool _linkEnabled;
    bool _linkActive;
    long _linkGeneration;
    PeerMessageStore Messages => new(_directory);
    void InitializeMessaging() {
        (_linkEnabled,_linkGeneration)=Messages.Initialize(_identity.PeerId,_linkGeneration);
        _linkActive=false;
    }
    void InvalidateMessaging() {
        var generation=checked(_linkGeneration+1);
        // Block callbacks before swapping the identity's directory; history is preserved in the restore checkpoint.
        _linkActive=false;
        Messages.SetEnabled(false,generation);_linkEnabled=false;_linkGeneration=generation;
    }
    bool KnownMessagingPeer(string peer) => peer!=_identity.PeerId&&Contacts.List().Any(c=>c.PeerId==peer);
    static bool MessageId(string id,out string normalized) {
        normalized="";if(!Guid.TryParseExact(id,"D",out var value)||value==Guid.Empty)return false;
        normalized=value.ToString("D");return true;
    }
    static bool ValidMessagePayload(string kind,string text) {
        if(text==null||text.Length==0||kind is not ("text" or "server")||text.Length>(kind=="text"?2000:32768))return false;
        try{return new UTF8Encoding(false,true).GetByteCount(text)<=65536;}catch(EncoderFallbackException){return false;}
    }
    public Task<PeerLinkCredentials> CreateLinkCredentialsAsync(CancellationToken ct=default)=>Locked(()=>new PeerLinkCredentials(_identity.PeerId,ReadSeed()),ct);
    public Task<bool> ReadLinkEnabledAsync(CancellationToken ct=default)=>Locked(()=>_linkEnabled,ct);
    public Task<long> ReadLinkGenerationAsync(CancellationToken ct=default)=>Locked(()=>_linkGeneration,ct);
    public Task SuspendLinkAsync(CancellationToken ct=default)=>Locked(()=> {
        _linkActive=false;var generation=checked(_linkGeneration+1);
        Messages.SetEnabled(_linkEnabled,generation);_linkGeneration=generation;return true;
    },ct);
    public Task<long> SetLinkEnabledAsync(bool enabled,CancellationToken ct=default)=>Locked(()=> {
        var generation=checked(_linkGeneration+1);Messages.SetEnabled(enabled,generation);
        _linkEnabled=enabled;_linkActive=enabled;_linkGeneration=generation;return generation;
    },ct);
    public Task<PeerMessage> AddOutgoingAsync(string peer,string kind,string text,CancellationToken ct=default)=>Locked(()=> {
        if(!_linkEnabled||!_linkActive)throw new InvalidOperationException("Подключите чат перед отправкой.");
        if(!KnownMessagingPeer(peer))throw new InvalidDataException("Контакт не найден.");
        if(!ValidMessagePayload(kind,text))throw new InvalidDataException("Неверный тип или размер сообщения.");
        var message=new PeerMessage(Guid.NewGuid().ToString("D"),peer,true,kind,text,DateTimeOffset.UtcNow,"Pending");
        if(!Messages.Add(message,false))throw new InvalidDataException("Повторный ID сообщения.");return message;
    },ct);
    public Task<PeerMessage> PrepareOutgoingRetryAsync(string peer,string id,CancellationToken ct=default)=>Locked(()=> {
        if(!_linkEnabled||!_linkActive||!KnownMessagingPeer(peer)||!MessageId(id,out var normalized))throw new InvalidOperationException("Подключите чат и выберите исходящее сообщение.");
        return Messages.PrepareRetry(peer,normalized);
    },ct);
    public async Task<bool> AcceptIncomingAsync(long generation,string peer,string id,string kind,string text,CancellationToken ct=default)=>(await AcceptIncomingDetailedAsync(generation,peer,id,kind,text,ct).ConfigureAwait(false)).Accepted;
    public Task<PeerAcceptance> AcceptIncomingDetailedAsync(long generation,string peer,string id,string kind,string text,CancellationToken ct=default)=>Locked(()=> {
        if(!_linkEnabled||!_linkActive||generation!=_linkGeneration||!MessageId(id,out var normalized)||!ValidMessagePayload(kind,text)||!KnownMessagingPeer(peer))return new PeerAcceptance(false,false);
        return Messages.AddResult(new(normalized,peer,false,kind,text,DateTimeOffset.UtcNow,"Received"),true);
    },ct);
    public Task<bool> MarkOutgoingAsync(long generation,string peer,string id,bool delivered,CancellationToken ct=default)=>Locked(()=> {
        if(!_linkEnabled||!_linkActive||generation!=_linkGeneration||!MessageId(id,out var normalized))return false;
        if(!KnownMessagingPeer(peer)){Messages.MarkOutgoing(peer,normalized,false);return false;}
        Messages.MarkOutgoing(peer,normalized,delivered);return true;
    },ct);
    public Task MarkOutgoingAsync(string peer,string id,bool delivered,CancellationToken ct=default)=>Locked(()=> {
        if(MessageId(id,out var normalized))Messages.MarkOutgoing(peer,normalized,delivered);return true;
    },ct);
    public Task<IReadOnlyList<PeerMessage>> ListMessagesAsync(string peer,CancellationToken ct=default)=>Locked(()=>Messages.List(peer),ct);
    public Task ClearConversationAsync(string peer,CancellationToken ct=default)=>Locked(()=> {Messages.Clear(peer);return true;},ct);
}
