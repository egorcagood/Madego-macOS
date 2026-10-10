using System.Collections.Concurrent;
namespace Madyar.PeerChat;
public sealed class PeerLinkController : IAsyncDisposable
{
    public event Action<PeerIncomingActivity>? IncomingPersisted;
    readonly PeerWorkspace _workspace;
    readonly Func<IPeerLinkTransport> _factory;
    readonly SemaphoreSlim _operations=new(1,1);
    readonly ConcurrentDictionary<string,TaskCompletionSource<bool>> _pending=new();
    readonly ConcurrentDictionary<string,TaskCompletionSource<bool>> _introductions=new();
    readonly ConcurrentDictionary<string,bool> _contactIntroductions=new();
    readonly ConcurrentDictionary<string,(long Epoch,bool Online)> _online=new();
    IPeerLinkTransport? _transport;
    CancellationTokenSource? _starting;
    long _epoch;
    volatile bool _enabled;
    bool _disposed;
    public bool Enabled=>_enabled;
    public string State {get;private set;}="Disabled";
    public event Action? Updated;
    public bool IsOnline(string peer)=>Enabled&&_online.TryGetValue(peer,out var seen)&&seen.Epoch==Volatile.Read(ref _epoch)&&seen.Online;
    public PeerLinkController(PeerWorkspace workspace,Func<IPeerLinkTransport> transport){_workspace=workspace;_factory=transport;}
    public bool IsCurrentActivity(PeerIncomingActivity activity)=>Enabled&&activity.Epoch==Volatile.Read(ref _epoch);
    void PublishActivity(PeerIncomingActivity activity){if(IsCurrentActivity(activity)&&IncomingPersisted is {} observers)foreach(Action<PeerIncomingActivity> observer in observers.GetInvocationList())try{observer(activity);}catch(Exception){}}
    void Notify(){if(Updated is {} observers)foreach(Action observer in observers.GetInvocationList())try{observer();}catch(Exception){/* UI observers cannot interrupt transport teardown. */}}
    void SetOnline(long epoch,string peer,bool online)=>_online.AddOrUpdate(peer,(epoch,online),(_,current)=>current.Epoch>epoch?current:(epoch,online));
    void Change(string state){State=state;Notify();}
    async Task CloseTransportAsync()
    {
        var old=_transport;_transport=null;
        if(old is not null){old.HandleEvent=null;await old.DisposeAsync();}
    }
    public async Task EnableAsync(CancellationToken ct=default)
    {
        if(_disposed)throw new ObjectDisposedException(nameof(PeerLinkController));
        if(_enabled&&State=="Ready")return;
        var epoch=Interlocked.Increment(ref _epoch);
        _starting?.Cancel();
        var starting=CancellationTokenSource.CreateLinkedTokenSource(ct);_starting=starting;
        await _operations.WaitAsync(starting.Token);
        IPeerLinkTransport? transport=null;
        try {
            starting.Token.ThrowIfCancellationRequested();
            if(epoch!=Volatile.Read(ref _epoch))throw new OperationCanceledException();
            // Replacing an enabled/failed instance always closes its transport before publishing another.
            await CloseTransportAsync();_online.Clear();
            var generation=await _workspace.SetLinkEnabledAsync(true,starting.Token);
            using var identity=await _workspace.CreateLinkCredentialsAsync(starting.Token);
            transport=_factory();_transport=transport;_enabled=true;Change("Connecting");
            transport.HandleEvent=e=>ReceiveAsync(transport,epoch,generation,e);
            await transport.StartAsync(identity,await _workspace.ListContactsAsync(starting.Token),starting.Token);
            starting.Token.ThrowIfCancellationRequested();
            if(epoch!=Volatile.Read(ref _epoch))throw new OperationCanceledException();
            if(transport is IPeerIntroductionTransport introductions)await introductions.UpdateInvitesAsync(await _workspace.ListIssuedInvitesAsync(starting.Token),starting.Token);
            starting.Token.ThrowIfCancellationRequested();
            if(epoch!=Volatile.Read(ref _epoch))throw new OperationCanceledException();
            Change("Ready");
        } catch {
            try {
                if(epoch==Volatile.Read(ref _epoch)) {
                    _enabled=false;
                    try{await _workspace.SetLinkEnabledAsync(false);}finally{Change("Unavailable");}
                }
            } finally {
                if(ReferenceEquals(_transport,transport))_transport=null;
                if(transport is not null){transport.HandleEvent=null;await transport.DisposeAsync();}
            }
            throw;
        } finally {_operations.Release();}
    }
    public Task DisableAsync()=>StopAsync(true);
    async Task StopAsync(bool optOut)
    {
        var epoch=Interlocked.Increment(ref _epoch);_starting?.Cancel();_enabled=false;_online.Clear();
        foreach(var pair in _pending)if(_pending.TryRemove(pair.Key,out var wait))wait.TrySetCanceled();
        foreach(var pair in _introductions)if(_introductions.TryRemove(pair.Key,out var wait))wait.TrySetCanceled();
        Change("Disabled");
        await _operations.WaitAsync();
        try {
            // A later Enable owns both the newer persisted intent and its replacement transport.
            if(epoch!=Volatile.Read(ref _epoch))return;
            try{if(optOut)await _workspace.SetLinkEnabledAsync(false);else await _workspace.SuspendLinkAsync();}
            finally{await CloseTransportAsync();}
        }finally{_operations.Release();}
    }
    async Task ReceiveAsync(IPeerLinkTransport transport,long epoch,long generation,PeerLinkEvent e)
    {
        if(!_enabled||epoch!=Volatile.Read(ref _epoch))return;
        if(e.Op=="intro_received") {
            var result=await _workspace.AcceptIntroductionDetailedAsync(generation,e.Peer,e.Id,e.Invite,e.Text);bool accepted=result.Accepted;
            if(!_enabled||epoch!=Volatile.Read(ref _epoch))return;
            try {await transport.AcceptAsync(e.Receipt,accepted,CancellationToken.None);}
            finally {
            // Persistence, not an ACK write, owns the new-message notification.
            if(accepted&&_enabled&&epoch==Volatile.Read(ref _epoch)) {
                if(result.Created) {
                    var request=(await _workspace.ListContactRequestsAsync()).FirstOrDefault(r=>r.PeerId==e.Peer);
                    if(request is not null&&!await _workspace.IsBlockedAsync(e.Peer))PublishActivity(new(e.Peer,e.Id,"request",request.Nickname,epoch));
                }
                Notify();
            }
            }
        } else if(e.Op=="intro_sent") {
            if(_introductions.TryRemove(e.Peer+":"+e.Id,out var wait)) {
                bool current=await _workspace.ReadLinkGenerationAsync()==generation&&!await _workspace.IsBlockedAsync(e.Peer);
                wait.TrySetResult(current&&_enabled&&epoch==Volatile.Read(ref _epoch)&&e.Delivered);Notify();
            }
        } else if(e.Op=="received") {
            var result=await _workspace.AcceptIncomingDetailedAsync(generation,e.Peer,e.Id,e.Kind,e.Text);bool accepted=result.Accepted;
            if(!_enabled||epoch!=Volatile.Read(ref _epoch))return;
            try {await transport.AcceptAsync(e.Receipt,accepted,CancellationToken.None);}
            finally {if(accepted&&_enabled&&epoch==Volatile.Read(ref _epoch)&&await _workspace.IsKnownContactAsync(e.Peer)){SetOnline(epoch,e.Peer,true);if(result.Created)PublishActivity(new(e.Peer,e.Id,e.Kind,"",epoch));Notify();}}
        } else if(e.Op=="sent") {
            if(_pending.TryRemove(e.Peer+":"+e.Id,out var wait)) {
                try {bool current=await _workspace.MarkOutgoingAsync(generation,e.Peer,e.Id,e.Delivered);wait.TrySetResult(current&&e.Delivered);}
                catch(Exception ex){wait.TrySetException(ex);}Notify();
            }
        } else if(e.Op=="peer") {if(await _workspace.IsKnownContactAsync(e.Peer)&&_enabled&&epoch==Volatile.Read(ref _epoch)){SetOnline(epoch,e.Peer,e.Online);Notify();}}
        else if(e.Op=="state"&&e.Detail=="process-exited") {
            // Disposal belongs to a separate task, not the child's reader callback.
            _=StopFailedAsync(epoch);
        }
    }
    async Task StopFailedAsync(long epoch){if(epoch!=Volatile.Read(ref _epoch))return;try{await DisableAsync();}catch(Exception){}if(epoch+1==Volatile.Read(ref _epoch)&&!Enabled)Change("Unavailable");}
    public async Task UpdateContactsAsync(CancellationToken ct=default)
    {
        await _operations.WaitAsync(ct);
        try {
        var contacts=await _workspace.ListContactsAsync(ct);var allowed=contacts.Select(c=>c.PeerId).ToHashSet(StringComparer.Ordinal);
        foreach(var peer in _online.Keys)if(!allowed.Contains(peer))_online.TryRemove(peer,out _);
        foreach(var item in _pending) {
            var split=item.Key.LastIndexOf(':');var peer=item.Key[..split];
            if(!allowed.Contains(peer)&&_pending.TryRemove(item.Key,out var wait)) {
                try {await _workspace.MarkOutgoingAsync(peer,item.Key[(split+1)..],false,CancellationToken.None);}
                finally {wait.TrySetResult(false);}
            }
        }
        var blocked=(await _workspace.ListBlockedContactsAsync(ct)).Select(c=>c.PeerId).ToHashSet(StringComparer.Ordinal);
        foreach(var item in _introductions) {
            var peer=item.Key[..item.Key.LastIndexOf(':')];
            if((blocked.Contains(peer)||(_contactIntroductions.ContainsKey(item.Key)&&!allowed.Contains(peer)))&&_introductions.TryRemove(item.Key,out var wait))wait.TrySetResult(false);
        }
        if(Enabled&&_transport is { } transport)await transport.UpdateContactsAsync(contacts,ct);
        Notify();
        }finally{_operations.Release();}
    }
    public async Task RefreshInvitesAsync(CancellationToken ct=default) {
        var epoch=Volatile.Read(ref _epoch);var transport=_transport;
        if(!Enabled||transport is not IPeerIntroductionTransport introductions)return;
        var invites=await _workspace.ListIssuedInvitesAsync(ct);
        if(Enabled&&epoch==Volatile.Read(ref _epoch)&&ReferenceEquals(transport,_transport))await introductions.UpdateInvitesAsync(invites,ct);
    }
    public async Task<bool> SendIntroductionAsync(VerifiedContactInvite target,string nickname,CancellationToken ct=default) {
        ct.ThrowIfCancellationRequested();PeerInvitationPolicy.Validate(target);
        if(await _workspace.IsBlockedAsync(target.PeerId,ct))throw new InvalidOperationException("Контакт заблокирован.");
        var epoch=Volatile.Read(ref _epoch);
        if(!Enabled||State!="Ready")throw new InvalidOperationException("Подключите чат перед отправкой своего контакта.");
        var name=PeerSetupProfile.NormalizeName(nickname);
        if(target.PeerId==(await _workspace.ReadProfileAsync(ct)).PeerId)throw new InvalidDataException("Это ваш собственный контакт.");
        await _workspace.SetNicknameAsync(name,ct);
        if(!Enabled||epoch!=Volatile.Read(ref _epoch))return false;
        return await SendIntroductionAsync(target,ct);
    }
    public async Task<bool> SendIntroductionAsync(VerifiedContactInvite target,CancellationToken ct=default) {
        PeerInvitationPolicy.Validate(target);
        if(await _workspace.IsBlockedAsync(target.PeerId,ct))throw new InvalidOperationException("Контакт заблокирован.");
        var epoch=Volatile.Read(ref _epoch);var transport=_transport;
        var wasContact=await _workspace.IsKnownContactAsync(target.PeerId,ct);
        if(!Enabled||State!="Ready"||transport is null)throw new InvalidOperationException("Подключите чат перед отправкой своего контакта.");
        if(transport is not IPeerIntroductionTransport introductions)throw new NotSupportedException("Сетевой модуль не поддерживает запрос знакомства. Обновите клиент.");
        if(target.PeerId==(await _workspace.ReadProfileAsync(ct)).PeerId)throw new InvalidDataException("Это ваш собственный контакт.");
        var own=await _workspace.CreateInviteAsync(ct);await RefreshInvitesAsync(ct);
        if(!Enabled||epoch!=Volatile.Read(ref _epoch)||!ReferenceEquals(transport,_transport)||await _workspace.IsBlockedAsync(target.PeerId,ct)||(wasContact&&!await _workspace.IsKnownContactAsync(target.PeerId,ct)))return false;
        var id=Guid.NewGuid().ToString("D");var key=target.PeerId+":"+id;
        var wait=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!_introductions.TryAdd(key,wait))throw new InvalidOperationException("Запрос уже отправляется.");
        if(wasContact)_contactIntroductions[key]=true;
        try {
            if(await _workspace.IsBlockedAsync(target.PeerId,ct)||(wasContact&&!await _workspace.IsKnownContactAsync(target.PeerId,ct)))return false;
            await introductions.SendIntroductionAsync(id,target.PeerId,target.InviteId.ToString("D"),own.Uri,ct);
            var delivered=await wait.Task.WaitAsync(TimeSpan.FromSeconds(45),ct);
            return delivered&&!await _workspace.IsBlockedAsync(target.PeerId,ct)&&(!wasContact||await _workspace.IsKnownContactAsync(target.PeerId,ct));
        } catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return false;}
        finally{_introductions.TryRemove(key,out _);_contactIntroductions.TryRemove(key,out _);}
    }
    public async Task<PeerMessage> SendAsync(string peer,string kind,string text,CancellationToken ct=default)
    {
        if(!Enabled||State!="Ready"||_transport is not { } transport)throw new InvalidOperationException("Подключите чат перед отправкой.");
        var message=await _workspace.AddOutgoingAsync(peer,kind,text,ct);
        return await TransmitAsync(transport,message,ct);
    }
    public async Task<PeerMessage> RetryAsync(string peer,string id,CancellationToken ct=default) {
        if(!Enabled||State!="Ready"||_transport is not {} transport)throw new InvalidOperationException("Подключите чат перед отправкой.");
        if(_pending.ContainsKey(peer+":"+id))throw new InvalidOperationException("Сообщение уже отправляется.");
        var message=await _workspace.PrepareOutgoingRetryAsync(peer,id,ct);
        if(message.Status=="Delivered")return message;
        return await TransmitAsync(transport,message,ct);
    }
    async Task<PeerMessage> TransmitAsync(IPeerLinkTransport transport,PeerMessage message,CancellationToken ct) {
        var peer=message.PeerId;
        var key=peer+":"+message.Id;var wait=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!_pending.TryAdd(key,wait))throw new InvalidOperationException("Сообщение уже отправляется.");Notify();
        try {
            await transport.SendAsync(message,ct);
            bool delivered=await wait.Task.WaitAsync(TimeSpan.FromSeconds(45),ct);
            return message with {Status=delivered?"Delivered":"Failed"};
        } catch {
            _pending.TryRemove(key,out _);await _workspace.MarkOutgoingAsync(peer,message.Id,false);Notify();
            if(ct.IsCancellationRequested)throw;
            return message with {Status="Failed"};
        }
    }
    public async ValueTask DisposeAsync(){if(_disposed)return;_disposed=true;await StopAsync(false);}
}
