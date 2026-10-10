using Madyar.PeerChat;
using EgorVpn.Models;
using EgorVpn.Presentation;
using EgorVpn.Services;
using System.Text.Json;
if(args.SequenceEqual(new[]{"--reply-name"})){await ReplyNameChecks.Run();return 0;}
var root=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"madyar-link-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
int passed=0;
void Check(bool yes,string why){if(!yes)throw new Exception(why);passed++;Console.WriteLine("PASS "+why);}
try {
 await ReplyNameChecks.Run();passed+=4;
 await using var a=await PeerWorkspace.OpenAsync(Path.Combine(root,"a"),new Plain());
 await using var b=await PeerWorkspace.OpenAsync(Path.Combine(root,"b"),new Plain());
 await a.SetNicknameAsync("A");await b.SetNicknameAsync("B");
 var pa=await a.ReadProfileAsync();var pb=await b.ReadProfileAsync();
 await a.AddContactAsync(PeerInviteCodec.Parse((await b.CreateInviteAsync()).Uri));
 await b.AddContactAsync(PeerInviteCodec.Parse((await a.CreateInviteAsync()).Uri));
 var ta=new Fake();var tb=new Fake();ta.Other=tb;tb.Other=ta;
 await using(var ca=new PeerLinkController(a,()=>ta)) {
 await using(var cb=new PeerLinkController(b,()=>tb)) {
 Check(!ca.Enabled&&!await a.ReadLinkEnabledAsync(),"Fresh controller has no transport traffic");
 await ca.EnableAsync();await cb.EnableAsync();
 var sent=await ca.SendAsync(pb.PeerId,"text","Привет");
 Check(sent.Status=="Delivered"&&(await b.ListMessagesAsync(pa.PeerId)).Single().Text=="Привет","Two controllers ACK only persisted recipient message");
 var oldCallback=tb.HandleEvent!;var oldGeneration=await b.ReadLinkGenerationAsync();
 var pending=await b.AddOutgoingAsync(pa.PeerId,"text","pending");
 await cb.DisableAsync();
 Check(!cb.Enabled&&tb.Stopped&&!await b.ReadLinkEnabledAsync(),"Disable stops transport and persists opt-out");
 await oldCallback(new PeerLinkEvent("received",pa.PeerId,Guid.NewGuid().ToString("D"),"text","late","late-token"));
 Check((await b.ListMessagesAsync(pa.PeerId)).Count==2,"Late callback after disable cannot append local history");
 await cb.EnableAsync();await oldCallback(new("peer",pa.PeerId,Online:true));
 Check(!cb.IsOnline(pa.PeerId),"Old connection cannot overwrite re-enabled online state");
 Check(!await b.MarkOutgoingAsync(oldGeneration,pa.PeerId,pending.Id,true)&&(await b.ListMessagesAsync(pa.PeerId)).Single(m=>m.Id==pending.Id).Status=="Pending","Stale positive ACK cannot mark an old message delivered");
 tb.DropAck=true;var before=(await b.ListMessagesAsync(pa.PeerId)).Count;
 var lost=await ca.SendAsync(pb.PeerId,"text","ACK lost");tb.DropAck=false;
 var retry=await ca.RetryAsync(pb.PeerId,lost.Id);
 Check(lost.Status=="Failed"&&retry.Status=="Delivered"&&retry.Id==lost.Id&&(await b.ListMessagesAsync(pa.PeerId)).Count==before+1,"Lost ACK retry preserves UUID and deduplicates already persisted recipient message");
 ta.SuppressDelivery=true;var attempt=ca.SendAsync(pb.PeerId,"text","inflight");
 await ta.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));await ca.DisableAsync();
 Check((await attempt.WaitAsync(TimeSpan.FromSeconds(3))).Status=="Failed","Disable cancels pending send without false delivery");
 await cb.DisposeAsync();
 Check(!cb.Enabled&&tb.Stopped&&await b.ReadLinkEnabledAsync(),"Application shutdown stops traffic while preserving opted-in preference");
 }}
 var gate=new Blocking();await using(var blocked=new PeerLinkController(a,()=>gate)) {
 var enabling=blocked.EnableAsync();await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));await blocked.DisableAsync().WaitAsync(TimeSpan.FromSeconds(3));
 try{await enabling;throw new Exception("enable was not cancelled");}catch(OperationCanceledException){}
 Check(!blocked.Enabled&&gate.Stopped&&!await a.ReadLinkEnabledAsync(),"Disable interrupts pending helper startup");
 }
 var quitGate=new Blocking();var quitting=new PeerLinkController(a,()=>quitGate);
 var quitStart=quitting.EnableAsync();await quitGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));await quitting.DisposeAsync();
 try{await quitStart;throw new Exception("startup not cancelled");}catch(OperationCanceledException){}
 Check(quitGate.Stopped&&await a.ReadLinkEnabledAsync(),"Application shutdown during pending start preserves opted-in intent while stopping transport");
 await a.SetLinkEnabledAsync(false);
 var transports=new List<Fake>();
 await using(var overlapping=new PeerLinkController(a,()=>{var f=new Fake();transports.Add(f);return f;})) {
 await overlapping.EnableAsync();Task? newer=null;bool trigger=true;
 overlapping.Updated+=()=>{if(overlapping.State=="Disabled"&&trigger){trigger=false;newer=overlapping.EnableAsync();}};
 await overlapping.DisableAsync();await newer!;
 Check(transports.Count==2&&transports[0].Stopped&&!transports[1].Stopped&&overlapping.Enabled&&await a.ReadLinkEnabledAsync(),"Overlapping re-enable disposes the replaced transport and preserves latest preference");
 await overlapping.DisableAsync();Check(transports.All(t=>t.Stopped),"Final disable leaves no replaced transport alive");
 }
 var held=new Fake{HoldAccept=true};
 await using(var presence=new PeerLinkController(a,()=>held)) {
 await presence.EnableAsync();var receiving=held.HandleEvent!(new("received",pb.PeerId,Guid.NewGuid().ToString("D"),"text","presence","receipt"));
 await held.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));await presence.DisableAsync();await presence.EnableAsync();
 held.ReleaseAccept.TrySetResult();await receiving;
 Check(!presence.IsOnline(pb.PeerId),"Receipt continuation crossing disable and enable cannot seed fresh online state");
 await presence.DisableAsync();
 }
 // Controlled ACK-loss and stale callbacks use real workspaces/controller with a transport fixture.
 await using(var issuer=await PeerWorkspace.OpenAsync(Path.Combine(root,"ack-issuer"),new Plain())) {
 await using(var requester=await PeerWorkspace.OpenAsync(Path.Combine(root,"ack-requester"),new Plain())) {
  await issuer.SetNicknameAsync("Ack issuer");await requester.SetNicknameAsync("Ack requester");
  var target=PeerInviteCodec.Parse((await issuer.CreateInviteAsync()).Uri);var remote=(await requester.ReadProfileAsync()).PeerId;
  var ti=new IntroductionFixture();var tr=new IntroductionFixture();ti.Other=tr;tr.Other=ti;
  await using(var ci=new PeerLinkController(issuer,()=>ti)) {
  await using(var cr=new PeerLinkController(requester,()=>tr)) {
   await ci.EnableAsync();await cr.EnableAsync();Check(ti.Invites.Any(i=>i.Id==target.InviteId.ToString("D")),"Enable publishes only registered invitation capabilities");
   ti.DropAck=true;Check(!await cr.SendIntroductionAsync(target),"Missing positive introduction ACK never reports delivered");
   var saved=(await issuer.ListContactRequestsAsync()).Single();ti.DropAck=false;
   Check(await cr.SendIntroductionAsync(target)&&(await issuer.ListContactRequestsAsync()).Single()==saved,"Lost introduction ACK retry keeps one original persisted peer request");
   var old=ti.HandleEvent!;await ci.DisableAsync();await issuer.RejectContactRequestAsync(remote);await ci.EnableAsync();
   var reply=(await requester.CreateInviteAsync()).Uri;
   await old(new("intro_received",remote,Guid.NewGuid().ToString("D"),Text:reply,Receipt:"late",Invite:target.InviteId.ToString("D")));
   Check((await issuer.ListContactRequestsAsync()).Count==0&&!ci.IsOnline(remote),"Late introduction callback after disable/re-enable cannot create fresh request or online state");
   tr.Suppress=true;var pending=cr.SendIntroductionAsync(target);await tr.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));await cr.DisableAsync();
   Check(!await pending.WaitAsync(TimeSpan.FromSeconds(3)),"Disabling pending introduction cancels its separate delivery wait without success");await ci.DisableAsync();
  }}
 }}
 // A persisted message may be paused at ACK while the user blocks its sender.
 await using(var left=await PeerWorkspace.OpenAsync(Path.Combine(root,"block-left"),new Plain())) {
 await using(var right=await PeerWorkspace.OpenAsync(Path.Combine(root,"block-right"),new Plain())) {
  await left.SetNicknameAsync("Left");await right.SetNicknameAsync("Right");var li=PeerInviteCodec.Parse((await left.CreateInviteAsync()).Uri);var ri=PeerInviteCodec.Parse((await right.CreateInviteAsync()).Uri);
  await left.AddContactAsync(ri);await right.AddContactAsync(li);var lt=new Fake();var rt=new Fake {HoldAccept=true};lt.Other=rt;rt.Other=lt;
  await using var lc=new PeerLinkController(left,()=>lt);await using var rc=new PeerLinkController(right,()=>rt);await lc.EnableAsync();await rc.EnableAsync();int notifications=0;rc.IncomingPersisted+=_=>notifications++;
  var send=lc.SendAsync(ri.PeerId,"text","persisted before block");await rt.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));await right.BlockContactAsync(li.PeerId);await rc.UpdateContactsAsync();rt.ReleaseAccept.TrySetResult();await send.WaitAsync(TimeSpan.FromSeconds(3));
  Check(notifications==0&&!rc.IsOnline(li.PeerId),"Block during pending ACK suppresses notification and online callback");
  await rt.HandleEvent!(new("peer",li.PeerId,Online:true));Check(!rc.IsOnline(li.PeerId),"Late peer callback cannot resurrect blocked contact");
  Check(!await right.AcceptIncomingAsync(await right.ReadLinkGenerationAsync(),li.PeerId,Guid.NewGuid().ToString("D"),"text","blocked"),"Blocked sender cannot append another local message");
  await right.UnblockContactAsync(li.PeerId);Check((await right.ListContactsAsync()).Count==0,"Unblock never silently adds contact back");
  lt.SuppressDelivery=true;var pending=lc.SendAsync(ri.PeerId,"text","cancel on block");await lt.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));await left.BlockContactAsync(ri.PeerId);await lc.UpdateContactsAsync();var failed=await pending.WaitAsync(TimeSpan.FromSeconds(3));
  Check(failed.Status=="Failed"&&(await left.ListMessagesAsync(ri.PeerId)).Single(m=>m.Id==failed.Id).Status=="Failed","Blocking pending send persists Failed before its wait completes");
 }}
 foreach(bool block in new[]{false,true}) {
 await using var sender=await PeerWorkspace.OpenAsync(Path.Combine(root,"intro-race-"+block),new Plain());await using var receiver=await PeerWorkspace.OpenAsync(Path.Combine(root,"intro-target-"+block),new Plain());await sender.SetNicknameAsync("Sender");await receiver.SetNicknameAsync("Target");
 var target=PeerInviteCodec.Parse((await receiver.CreateInviteAsync()).Uri);await sender.AddContactAsync(target);var transport=new IntroductionFixture();await using var controller=new PeerLinkController(sender,()=>transport);await controller.EnableAsync();transport.HoldInvites=true;
 var pending=controller.SendIntroductionAsync(target);await transport.InvitesStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
 if(block)await sender.BlockContactAsync(target.PeerId);else await sender.RemoveContactAsync(target.PeerId);await controller.UpdateContactsAsync();transport.ReleaseInvites.TrySetResult();
 Check(!await pending.WaitAsync(TimeSpan.FromSeconds(3))&&transport.IntroductionsSent==0,"Contact "+(block?"block":"delete")+" during invite refresh prevents queued introduction send");
 }
 // Real helper process fixture: actual standard libp2p encryption/TCP and persisted ACK, no public network.
 string helper=args.Length==2&&args[0]=="--helper"?args[1]:Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/Madyar.Link/runtime",OperatingSystem.IsWindows()?"windows-amd64/MadyarLink.exe":"macos-arm64/MadyarLink"));
 if(!File.Exists(helper))throw new FileNotFoundException("Bundled helper required for process integration checks",helper);
 // Introduction is the only allowed channel before the issuer explicitly accepts this new peer.
 await using(var issuer=await PeerWorkspace.OpenAsync(Path.Combine(root,"issuer"),new Plain())) {
 await using(var requester=await PeerWorkspace.OpenAsync(Path.Combine(root,"requester"),new Plain())) {
  await issuer.SetNicknameAsync("Issuer");await requester.SetNicknameAsync("Requester");
  var target=PeerInviteCodec.Parse((await issuer.CreateInviteAsync()).Uri);var requesterPeer=(await requester.ReadProfileAsync()).PeerId;
  await requester.AddContactAsync(target);
  var issuerProcess=new PeerLinkProcess(helper,false);var requesterProcess=new PeerLinkProcess(helper,false);
  await using(var issuerLink=new PeerLinkController(issuer,()=>issuerProcess)) {
  await using(var requesterLink=new PeerLinkController(requester,()=>requesterProcess)) {
   Check((await issuer.ListContactsAsync()).Count==0&&!await issuer.ReadLinkEnabledAsync(),"Introduction issuer starts with zero contacts and opt-in false");
   await issuerLink.EnableAsync();await requesterLink.EnableAsync();
   await issuerProcess.FixturePeerAsync(requesterPeer,requesterProcess.Addresses,CancellationToken.None);
   await requesterProcess.FixturePeerAsync(target.PeerId,issuerProcess.Addresses,CancellationToken.None);
   Check(await requesterLink.SendIntroductionAsync(target).WaitAsync(TimeSpan.FromSeconds(20)),"Two real helper processes deliver authenticated signed introduction with durable SQLite ACK");
   var request=(await issuer.ListContactRequestsAsync()).Single();
   Check(request.PeerId==requesterPeer&&PeerInviteCodec.Parse(request.InviteUri).PeerId==requesterPeer&&(await issuer.ListContactsAsync()).Count==0,
    "Real introduction pins requester identity and awaits explicit acceptance without auto-add");
   var rejected=await requesterLink.SendAsync(target.PeerId,"text","not accepted").WaitAsync(TimeSpan.FromSeconds(20));
   Check(rejected.Status=="Failed"&&(await issuer.ListMessagesAsync(requesterPeer)).Count==0,"Unknown-peer text remains rejected after introduction ACK");
   Check(await requesterLink.SendIntroductionAsync(target).WaitAsync(TimeSpan.FromSeconds(20))&&(await issuer.ListContactRequestsAsync()).Single()==request,
    "Real repeated introduction ACKs one persisted peer request without duplicate prompts");
   Check(await issuer.AcceptContactRequestAsync(requesterPeer),"Issuer explicitly accepts actual introduction");await issuerLink.UpdateContactsAsync();
   var welcome=await issuerLink.SendAsync(requesterPeer,"text","Welcome").WaitAsync(TimeSpan.FromSeconds(20));
   var response=await requesterLink.SendAsync(target.PeerId,"text","Thanks").WaitAsync(TimeSpan.FromSeconds(20));
   Check(welcome.Status=="Delivered"&&response.Status=="Delivered"&&(await requester.ListMessagesAsync(target.PeerId)).Any(m=>m.Id==welcome.Id)&&(await issuer.ListMessagesAsync(requesterPeer)).Any(m=>m.Id==response.Id),
    "Explicitly accepted introduction grants actual bidirectional authenticated text delivery");
   var configuration=SingleServerShare.Export(SingleServerShare.Import("vless://10000000-0000-4000-8000-000000000007@selected.invalid:443?security=reality&type=tcp&pbk=fixture&sid=ab&sni=cdn.invalid#Selected").Nodes.Single());
   var serverMessage=await issuerLink.SendAsync(requesterPeer,"server",configuration).WaitAsync(TimeSpan.FromSeconds(20));
   var received=(await requester.ListMessagesAsync(target.PeerId)).Single(m=>m.Id==serverMessage.Id);var group=SingleServerShare.Import(received.Text);
   Check(serverMessage.Status=="Delivered"&&group.Nodes.Count==1&&group.Url=="","Accepted real introduction supports selected-server delivery and local one-node import");
   var oldGeneration=await issuer.ReadLinkGenerationAsync();await issuerLink.DisableAsync();
   Check(!await issuer.AcceptIntroductionAsync(oldGeneration,requesterPeer,Guid.NewGuid().ToString("D"),target.InviteId.ToString("D"),request.InviteUri),
    "Disabled real issuer rejects stale introduction generation without reviving transport");await requesterLink.DisableAsync();
  }}
 }}
 // A subscription owner deliberately shares one selected node. Recipient has no subscription/cache.
 var ownerGroup=new SubscriptionGroup { Name="Private owner subscription",Url="https://provider.invalid/subscription-secret" };
 for(int i=1;i<=10;i++) {
  var uri=$"vless://10000000-0000-4000-8000-{i:D12}@server-{i}.invalid:443?type=tcp&security=reality&encryption=none&flow=xtls-rprx-vision&sni=cdn-{i}.invalid&fp=chrome&pbk=fixture-public-{i}&sid=ab{i:D2}&spx=%2Fprobe%2F{i}&pqv=fixture-verify-{i}#Server%20{i}";
  if(!VlessUriParser.TryParse(uri,out var node,out var error)||node is null)throw new Exception("Owner fixture cannot parse: "+error);
  ownerGroup.Nodes.Add(node);
 }
 var selected=ownerGroup.Nodes[6];var exported=SingleServerShare.Export(selected);
 Check(ownerGroup.Nodes.Count==10&&exported==selected.RawUri&&!exported.Contains(ownerGroup.Url)&&
  ownerGroup.Nodes.Where(n=>n!=selected).All(n=>!exported.Contains(n.UserId)),"One selected of ten owner nodes exports only its VLESS credentials, without subscription URL or other UUIDs");
 var statePath=Path.Combine(root,"recipient-vpn","state.json");
 var stateStore=new AtomicJsonStore<AppState>(statePath);var recipientState=await stateStore.LoadAsync();
 Check(recipientState.Subscriptions.Count==0&&!File.Exists(statePath),"Recipient starts with zero subscriptions and no VPN state cache");
 string? sharedMessageId=null;
 var processA=new PeerLinkProcess(helper,false);var processB=new PeerLinkProcess(helper,false);
 await using(var ca=new PeerLinkController(a,()=>processA)) {
 await using(var cb=new PeerLinkController(b,()=>processB)) {
 await ca.EnableAsync();await cb.EnableAsync();
 await processA.FixturePeerAsync(pb.PeerId,processB.Addresses,CancellationToken.None);
 await processB.FixturePeerAsync(pa.PeerId,processA.Addresses,CancellationToken.None);
 var sent=await ca.SendAsync(pb.PeerId,"text","real-process").WaitAsync(TimeSpan.FromSeconds(20));
 Check(sent.Status=="Delivered"&&(await b.ListMessagesAsync(pa.PeerId)).Any(m=>m.Id==sent.Id&&m.Text=="real-process"),"Two real helper processes authenticate and ACK SQLite-persisted text");
 var server=await ca.SendAsync(pb.PeerId,"server",exported).WaitAsync(TimeSpan.FromSeconds(20));sharedMessageId=server.Id;
 var incoming=(await b.ListMessagesAsync(pa.PeerId)).Single(m=>m.Id==server.Id);
 Check(server.Status=="Delivered"&&incoming.Kind=="server"&&!incoming.Outgoing&&incoming.Status=="Received"&&incoming.Text==exported,
  "Selected-server envelope crosses two real encrypted helper processes and ACKs the SQLite-persisted recipient configuration");
 Check(recipientState.Subscriptions.Count==0&&!File.Exists(statePath),"Receiving a server message alone does not create a subscription or modify VPN cache");
 // User-confirmed add uses the same importer and presentation-to-cache mapping as the application.
 var imported=SingleServerShare.Import(incoming.Text);var item=new SubscriptionItem(imported);
 Check(imported.Nodes.Count==1&&item.Nodes.Count==1&&imported.Url==""&&SingleServerShare.IsLocal(imported),
  "Recipient with no subscriptions imports exactly one local group/node with no provider URL");
 recipientState.Subscriptions.Add(item.ToSaved());recipientState.SelectedGroupId=item.Id;
 await stateStore.SaveAsync(recipientState);
 var restoredState=await new AtomicJsonStore<AppState>(statePath).LoadAsync();
 var restored=SubscriptionItem.FromSaved(restoredState.Subscriptions.Single());var restoredNode=restored.Nodes.Single().Source;
 Check(File.Exists(statePath)&&restoredState.Subscriptions.Count==1&&restored.Nodes.Count==1&&restored.Url==""&&
  SingleServerShare.IsLocal(restored.Source)&&restoredState.SelectedGroupId==restored.Id&&restoredNode.RawUri==selected.RawUri,
  "Actual atomic disk cache restores the only imported standalone server and selected group without a subscription");
 Check(SingleServerShare.ConnectionKey(restoredNode)==SingleServerShare.ConnectionKey(selected),
  "Persisted recipient connection identity equals the selected owner connection identity");
 using(var config=JsonDocument.Parse(XrayConfigBuilder.Build(restoredNode))) {
  var proxy=config.RootElement.GetProperty("outbounds")[0];var settings=proxy.GetProperty("settings");var stream=proxy.GetProperty("streamSettings");
  Check(proxy.GetProperty("protocol").GetString()=="vless"&&settings.GetProperty("id").GetString()==selected.UserId&&
   settings.GetProperty("address").GetString()==selected.Host&&settings.GetProperty("port").GetInt32()==selected.Port&&
   settings.GetProperty("encryption").GetString()==selected.Encryption&&settings.GetProperty("flow").GetString()==selected.Flow&&
   stream.GetProperty("method").GetString()==selected.Transport&&stream.GetProperty("security").GetString()==selected.Security,
   "Actual Xray configuration retains selected provider UUID, endpoint, encryption, flow and transport");
  var reality=stream.GetProperty("realitySettings");
  Check(reality.GetProperty("serverName").GetString()==selected.Sni&&reality.GetProperty("fingerprint").GetString()==selected.Fingerprint&&
   reality.GetProperty("password").GetString()==selected.PublicKey&&reality.GetProperty("shortId").GetString()==selected.ShortId&&
   reality.GetProperty("spiderX").GetString()==selected.SpiderX&&reality.GetProperty("mldsa65Verify").GetString()==selected.Mldsa65Verify,
   "Actual Xray REALITY configuration retains SNI, fingerprint, public key, short ID, spiderX and post-quantum verifier");
  Check(XrayConfigBuilder.Build(restoredNode)==XrayConfigBuilder.Build(selected),
   "Owner selected node and restored recipient node generate identical complete Xray configuration");
 }
 await ca.DisableAsync();await cb.DisableAsync();
 Check(!ca.Enabled&&!cb.Enabled&&!await a.ReadLinkEnabledAsync()&&!await b.ReadLinkEnabledAsync(),"Real process shutdown leaves both chat profiles disabled");
 }}
 await b.DisposeAsync();
 await using(var reopened=await PeerWorkspace.OpenAsync(Path.Combine(root,"b"),new Plain())) {
  var persistent=(await reopened.ListMessagesAsync(pa.PeerId)).Single(m=>m.Id==sharedMessageId);
  Check(persistent.Kind=="server"&&persistent.Status=="Received"&&persistent.Text==exported,
   "Recipient workspace reopen preserves the ACKed selected-server message in actual SQLite history");
 }
 Check(ownerGroup.Nodes.Count==10&&ownerGroup.Url=="https://provider.invalid/subscription-secret",
  "Single-server delivery/import leaves the owner's original ten-node subscription intact");
 Console.WriteLine($"PeerLink checks: {passed} passed");return 0;
}finally{Directory.Delete(root,true);}
sealed class Plain:IIdentityProtector{public byte[]Protect(byte[]b)=>b.ToArray();public byte[]Unprotect(byte[]b)=>b.ToArray();}
sealed class Fake:IPeerLinkTransport{
 public Func<PeerLinkEvent,Task>? HandleEvent{get;set;}public Fake? Other;public string Peer="";public bool Stopped,SuppressDelivery,HoldAccept,DropAck;public Dictionary<string,PeerMessage> Pending=[];
 public TaskCompletionSource AcceptStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseAccept=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public TaskCompletionSource SendStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public Task StartAsync(PeerLinkCredentials id,IReadOnlyList<PeerContact>c,CancellationToken ct){Peer=id.PeerId;Stopped=false;return Task.CompletedTask;}
 public Task UpdateContactsAsync(IReadOnlyList<PeerContact>c,CancellationToken ct)=>Task.CompletedTask;
 public async Task SendAsync(PeerMessage m,CancellationToken ct){Pending[m.Id]=m;if(SuppressDelivery){SendStarted.TrySetResult();return;}await Other!.HandleEvent!(new("received",Peer,m.Id,m.Kind,m.Text,m.Id));}
 public async Task AcceptAsync(string receipt,bool accepted,CancellationToken ct){if(HoldAccept){AcceptStarted.TrySetResult();await ReleaseAccept.Task;}if(Other is {} other){var m=other.Pending[receipt];await other.HandleEvent!(new("sent",Peer,m.Id,Delivered:accepted&&!DropAck));}}
 public Task StopAsync(){Stopped=true;return Task.CompletedTask;}public ValueTask DisposeAsync(){Stopped=true;return ValueTask.CompletedTask;}
}
sealed class Blocking:IPeerLinkTransport {
 public Func<PeerLinkEvent,Task>?HandleEvent{get;set;}public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Stopped;
 public async Task StartAsync(PeerLinkCredentials id,IReadOnlyList<PeerContact> c,CancellationToken ct){Started.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}
 public Task UpdateContactsAsync(IReadOnlyList<PeerContact> c,CancellationToken ct)=>Task.CompletedTask;
 public Task SendAsync(PeerMessage m,CancellationToken ct)=>Task.CompletedTask;public Task AcceptAsync(string receipt,bool accepted,CancellationToken ct)=>Task.CompletedTask;
 public Task StopAsync(){Stopped=true;return Task.CompletedTask;}public ValueTask DisposeAsync(){Stopped=true;return ValueTask.CompletedTask;}
}
sealed class IntroductionFixture:IPeerLinkTransport,IPeerIntroductionTransport {
 public Func<PeerLinkEvent,Task>?HandleEvent{get;set;}public IntroductionFixture?Other;public string Peer="";public bool DropAck,Suppress,HoldInvites;public int IntroductionsSent;public TaskCompletionSource InvitesStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseInvites=new(TaskCreationOptions.RunContinuationsAsynchronously);public IReadOnlyList<PeerIssuedInvite>Invites=[];
 public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public Task StartAsync(PeerLinkCredentials id,IReadOnlyList<PeerContact> contacts,CancellationToken ct){Peer=id.PeerId;return Task.CompletedTask;}
 public Task UpdateContactsAsync(IReadOnlyList<PeerContact> contacts,CancellationToken ct)=>Task.CompletedTask;
 public async Task UpdateInvitesAsync(IReadOnlyList<PeerIssuedInvite> invites,CancellationToken ct){Invites=invites.ToArray();if(HoldInvites){InvitesStarted.TrySetResult();await ReleaseInvites.Task.WaitAsync(ct);}}
 public async Task SendIntroductionAsync(string id,string recipient,string targetInviteId,string signedReturnUri,CancellationToken ct){IntroductionsSent++;if(Suppress){Started.TrySetResult();return;}await Other!.HandleEvent!(new("intro_received",Peer,id,Text:signedReturnUri,Receipt:id,Invite:targetInviteId));}
 public Task SendAsync(PeerMessage message,CancellationToken ct)=>Task.CompletedTask;
 public Task AcceptAsync(string receipt,bool accepted,CancellationToken ct)=>Other!.HandleEvent!(new("intro_sent",Peer,receipt,Delivered:accepted&&!DropAck));
 public Task StopAsync()=>Task.CompletedTask;public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
