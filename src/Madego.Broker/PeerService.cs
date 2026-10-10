using Madyar.PeerChat;
namespace Madego.Broker;

public sealed class PeerService(MacPaths paths,IWrappingKeyStore keys,string helper,IpcServer ipc,CancellationToken life):IAsyncDisposable
{
    readonly SemaphoreSlim _initialization=new(1,1);
    readonly CancellationTokenSource _restore=new();
    readonly SemaphoreSlim _backupGate=new(1,1);
    PeerWorkspace? _workspace;PeerLinkController? _controller;
    PeerBackupPreview? _preview;string? _previewId;
    public async Task RestoreIntentAsync(CancellationToken ct)
    {
        if(!Directory.Exists(paths.Peer))return;
        using var restore=CancellationTokenSource.CreateLinkedTokenSource(ct,_restore.Token);
        var workspace=await OpenAsync(restore.Token);if(await workspace.ReadLinkEnabledAsync(restore.Token))await _controller!.EnableAsync(restore.Token);
    }
    async Task<PeerWorkspace> OpenAsync(CancellationToken ct)
    {
        await _initialization.WaitAsync(ct);
        try {
            if(_workspace is not null)return _workspace;
            PrivateFiles.NoLinks(paths.Peer);
            _workspace=await PeerWorkspace.OpenAsync(paths.Peer,new WrappingProtector(keys),ct);
            _controller=new PeerLinkController(_workspace,()=>new PeerLinkProcess(helper));
            _controller.Updated+=Changed;_controller.IncomingPersisted+=Incoming;return _workspace;
        }finally{_initialization.Release();}
    }
    void Changed(){_=NotifyAsync("changed",new{});}
    void Incoming(PeerIncomingActivity activity){if(_controller?.IsCurrentActivity(activity)==true)_=NotifyAsync("incoming",new{peer=activity.PeerId,kind=activity.Kind});}
    async Task NotifyAsync(string name,object data){try{await ipc.EventAsync(name,data,life);}catch(OperationCanceledException){}catch(IOException){}}
    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        var workspace=_workspace;
        if(workspace is null)return new{enabled=false,state="Disabled",nickname="",peerId="",contacts=Array.Empty<object>(),requests=Array.Empty<object>(),blocked=Array.Empty<object>(),invites=Array.Empty<object>()};
        var profile=await workspace.ReadProfileAsync(ct);
        return new {enabled=_controller!.Enabled,state=_controller.State,nickname=profile.Nickname,peerId=profile.PeerId,
            contacts=(await workspace.ListContactsAsync(ct)).Select(c=>new{peerId=c.PeerId,name=c.Alias??c.Nickname,online=_controller.IsOnline(c.PeerId)}).ToArray(),
            requests=(await workspace.ListContactRequestsAsync(ct)).Select(c=>new{peerId=c.PeerId,name=c.Nickname}).ToArray(),
            blocked=(await workspace.ListBlockedContactsAsync(ct)).Select(c=>new{peerId=c.PeerId,name=c.Nickname}).ToArray(),
            invites=(await workspace.ListIssuedInvitesAsync(ct)).Select(i=>new{id=i.Id,expires=i.ExpiresUtc}).ToArray()};
    }
    public async Task<object> DispatchAsync(IpcRequest request,CancellationToken ct)
    {
        if(request.Method is "backup.preview" or "backup.restore" or "backup.cancel")return await BackupAsync(request,ct);
        var p=request.Params;string Get(string name)=>p.GetProperty(name).GetString()??throw new ArgumentException();
        if(request.Method=="chat.disable") {_restore.Cancel();if(_workspace is null&&!Directory.Exists(paths.Peer))return await SnapshotAsync(ct);}
        var workspace=await OpenAsync(ct);var link=_controller!;
        switch(request.Method) {
            case "chat.enable":await link.EnableAsync(ct);break;
            case "chat.disable":await link.DisableAsync();break;
            case "profile.get":break;
            case "profile.nickname":await workspace.SetNicknameAsync(Get("name"),ct);break;
            case "profile.invite":var own=await workspace.CreateInviteAsync(ct);await link.RefreshInvitesAsync(ct);return new{uri=own.Uri,code=own.Code};
            case "profile.revoke":await workspace.RevokeInviteAsync(Get("id"),ct);await link.RefreshInvitesAsync(ct);break;
            case "contact.add":
                if((await workspace.ListContactsAsync(ct)).Count>=64)throw new BrokerFault("contact_limit");
                if(string.IsNullOrWhiteSpace((await workspace.ReadProfileAsync(ct)).Nickname))throw new BrokerFault("profile_name_required");
                var invite=PeerInviteCodec.Parse(Get("invite"));await workspace.AddContactAsync(invite,ct);await link.UpdateContactsAsync(ct);
                var delivered=link.Enabled&&await link.SendIntroductionAsync(invite,ct);return new{introductionDelivered=delivered};
            case "contact.accept":if((await workspace.ListContactsAsync(ct)).Count>=64)throw new BrokerFault("contact_limit");await workspace.AcceptContactRequestAsync(Get("peer"),ct);await link.UpdateContactsAsync(ct);break;
            case "contact.reject":await workspace.RejectContactRequestAsync(Get("peer"),ct);break;
            case "contact.remove":await workspace.RemoveContactAsync(Get("peer"),ct);await link.UpdateContactsAsync(ct);break;
            case "contact.block":await workspace.BlockContactAsync(Get("peer"),ct);await link.UpdateContactsAsync(ct);break;
            case "contact.unblock":await workspace.UnblockContactAsync(Get("peer"),ct);await link.UpdateContactsAsync(ct);break;
            case "contact.alias":await workspace.SetAliasAsync(Get("peer"),Get("name"),ct);break;
            case "messages.list":return new{messages=await workspace.ListMessagesAsync(Get("peer"),ct),draft=await workspace.GetDraftAsync(Get("peer"),ct)};
            case "messages.clear":await workspace.ClearConversationAsync(Get("peer"),ct);break;
            case "messages.send":return await link.SendAsync(Get("peer"),"text",Get("text"),ct);
            case "messages.server":return await link.SendAsync(Get("peer"),"server",Get("uri"),ct);
            case "messages.retry":return await link.RetryAsync(Get("peer"),Get("id"),ct);
            case "messages.draft":await workspace.SaveDraftAsync(Get("peer"),Get("text"),ct);break;
            case "backup.export":await workspace.ExportBackupAsync(Get("path"),Get("password"),ct);return new{saved=true};
            default:throw new BrokerFault("unknown_method");
        }
        Changed();return await SnapshotAsync(ct);
    }
    async Task<object> BackupAsync(IpcRequest request,CancellationToken ct)
    {
        string Get(string name)=>request.Params.GetProperty(name).GetString()??throw new ArgumentException();
        await _backupGate.WaitAsync(ct);
        try {
            if(request.Method=="backup.cancel") {_preview?.Dispose();_preview=null;_previewId=null;return new{cancelled=true};}
            if(request.Method=="backup.preview") {
                _preview?.Dispose();_preview=null;_previewId=null;
                var preview=await PeerWorkspace.PreviewBackupFileAsync(Get("path"),Get("password"),ct);
                if(preview.ContactCount>64){preview.Dispose();throw new BrokerFault("contact_limit");}
                _preview=preview;_previewId=Guid.NewGuid().ToString("N");
                return new{id=_previewId,peerId=preview.Profile.PeerId,name=preview.Profile.Nickname,contacts=preview.ContactCount};
            }
            if(_preview is null||_previewId!=Get("id")||!request.Params.GetProperty("confirmed").GetBoolean())throw new BrokerFault("restore_confirmation_required");
            // Move ownership into this operation. Cancellation cannot dispose a preview in use.
            using var owned=_preview;_preview=null;_previewId=null;
            await _initialization.WaitAsync(ct);
            try {
                _restore.Cancel();
                var oldLink=_controller;_controller=null;
                if(oldLink is not null){oldLink.Updated-=Changed;oldLink.IncomingPersisted-=Incoming;await oldLink.DisableAsync();await oldLink.DisposeAsync();}
                var oldWorkspace=_workspace;_workspace=null;if(oldWorkspace is not null)await oldWorkspace.DisposeAsync();
                // A new wrapping key is permitted only after verified password backup + explicit replacement confirmation.
                var recoveryKeys=keys.ConfirmedRecovery();PrivateFiles.Directory(paths.Peer);
                try {_workspace=await PeerWorkspace.RestoreOfflineAsync(paths.Peer,new WrappingProtector(recoveryKeys),owned,true,ct);}
                catch { // Restore code retains its checkpoint/journal. Try to reopen only the actual saved identity.
                    try {_workspace=await PeerWorkspace.OpenAsync(paths.Peer,new WrappingProtector(keys),CancellationToken.None);}catch(Exception){}
                    throw;
                }
                finally {
                    if(_workspace is not null){_controller=new PeerLinkController(_workspace,()=>new PeerLinkProcess(helper));_controller.Updated+=Changed;_controller.IncomingPersisted+=Incoming;}
                }
            }finally{_initialization.Release();}
            Changed();return await SnapshotAsync(ct);
        }finally{_backupGate.Release();}
    }

    public async ValueTask DisposeAsync()
    {
        _preview?.Dispose();
        if(_controller is not null){_controller.Updated-=Changed;_controller.IncomingPersisted-=Incoming;await _controller.DisposeAsync();}
        if(_workspace is not null)await _workspace.DisposeAsync();
    }
}
