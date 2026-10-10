using Madyar.PeerChat;
using EgorVpn.Presentation;
using Microsoft.Data.Sqlite;
using System.Text;
using System.IO.Compression;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
namespace PeerMessagingChecks;
internal static class Program {
    static readonly List<(string Name, Func<Task> Run)> Tests = [];
    static void Check(bool condition, string why) { if (!condition) throw new Exception(why); }
    static async Task Reject(Func<Task> action) {
        try { await action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return; }
        throw new Exception("Operation unexpectedly accepted");
    }
    static string Temp() => Path.Combine("/private/tmp/madyar-messaging-checks", Guid.NewGuid().ToString("N"), "profile");
    static async Task<VerifiedContactInvite> Contact() {
        await using var other = await PeerWorkspace.OpenAsync(Temp(), new Protector());
        await other.SetNicknameAsync("Друг");
        return PeerInviteCodec.Parse((await other.CreateInviteAsync()).Uri);
    }
    static async Task<(PeerWorkspace W, string Path, string Peer, long Generation)> Fixture() {
        var path = Temp(); var w = await PeerWorkspace.OpenAsync(path, new Protector());
        var c = await Contact(); await w.AddContactAsync(c);
        return (w, path, c.PeerId, await w.SetLinkEnabledAsync(true));
    }
    static string Id() => Guid.NewGuid().ToString("D");
    static ContactInviteDisplay SignedAt(PeerProfile profile,byte[] seed,DateTimeOffset date) {
        static string Encode(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
        var payload=JsonSerializer.SerializeToUtf8Bytes(new{peerId=profile.PeerId,publicKey=Encode(profile.PublicKey),nickname=profile.Nickname,inviteId=Id(),createdUtc=date.ToUniversalTime().ToString("O")});
        var signer=new Ed25519Signer();signer.Init(true,new Ed25519PrivateKeyParameters(seed,0));var data=Encoding.UTF8.GetBytes("madyar-contact-v1\n").Concat(payload).ToArray();signer.BlockUpdate(data,0,data.Length);
        var encoded=Encode(JsonSerializer.SerializeToUtf8Bytes(new{version=1,payload=Encode(payload),signature=Encode(signer.GenerateSignature())}));
        return new("madyar://contact/"+encoded,"MDC1-"+encoded);
    }
    static void Add(string name, Func<Task> run) => Tests.Add((name,run));
    static async Task Main() {
        Add("SignedInvitationRegistersDurableCapability", async () => {
            var path=Temp();await using var w=await PeerWorkspace.OpenAsync(path,new Protector());await w.SetNicknameAsync("Issuer");
            var invite=PeerInviteCodec.Parse((await w.CreateInviteAsync()).Uri);
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(path,"messages.db"),Pooling=false}.ToString());db.Open();
            using var schema=db.CreateCommand();schema.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='IssuedInvites'";
            Check((long)schema.ExecuteScalar()! ==1,"Creating signed invite did not register a durable invitation capability");
            using var rows=db.CreateCommand();rows.CommandText="SELECT COUNT(*) FROM IssuedInvites WHERE Id=$id";rows.Parameters.AddWithValue("$id",invite.InviteId.ToString("D"));
            Check((long)rows.ExecuteScalar()! ==1,"Signed invite ID was not persisted");
        });
        Add("PublicClientPackageContractExists", () => {
            Check(typeof(PeerWorkspace).Assembly.GetType("Madyar.PeerChat.PeerClientPackage") is not null,"Public-only installer/contact package API is absent");return Task.CompletedTask;
        });
        Add("PublicPackageStrictSchemaExpiryAndIndependentIdentity", async () => {
            var path=Temp();await using var w=await PeerWorkspace.OpenAsync(path,new Protector());await w.SetNicknameAsync("Public owner");
            var display=await w.CreateInviteAsync();var own=await w.ReadProfileAsync();var parent=Path.GetDirectoryName(path)!;var contact=Path.Combine(parent,PeerClientPackage.PublicContactFileName);
            Check(await PeerClientPackage.ReadPublicContactAsync(contact)==null,"Absent sidecar was not optional");await PeerClientPackage.WritePublicContactAsync(contact,display);
            var read=await PeerClientPackage.ReadPublicContactAsync(contact);Check(read?.Invite.PeerId==own.PeerId&&read.InviteUri==display.Uri&&!read.IsExpired,"Public sidecar did not revalidate signed owner");
            using(var d=JsonDocument.Parse(await File.ReadAllBytesAsync(contact)))Check(d.RootElement.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"inviteUri","schemaVersion"}),"Public sidecar includes private or extra fields");
            bool noOverwrite=false;try{await PeerClientPackage.WritePublicContactAsync(contact,display);}catch(IOException){noOverwrite=true;}Check(noOverwrite&&(await PeerClientPackage.ReadPublicContactAsync(contact))?.InviteUri==display.Uri,"Existing public contact overwritten");
            foreach(var payload in new[]{"invalid json","{\"schemaVersion\":[],\"inviteUri\":\""+display.Uri+"\"}","{\"schemaVersion\":1,\"inviteUri\":\""+display.Uri+"\",\"seed\":\"private\"}","{\"schemaVersion\":1,\"schemaVersion\":1,\"inviteUri\":\""+display.Uri+"\"}","{\"schemaVersion\":2,\"inviteUri\":\""+display.Uri+"\"}"}) {
                bool failed=false;try{PeerClientPackage.ParsePublicContact(Encoding.UTF8.GetBytes(payload));}catch(InvalidDataException){failed=true;}Check(failed,"Untrusted extra/duplicate/schema fields accepted");
            }
            bool oversize=false;try{PeerClientPackage.ParsePublicContact(new byte[8193]);}catch(InvalidDataException){oversize=true;}Check(oversize,"Oversized public DTO accepted");
            using var creds=await w.CreateLinkCredentialsAsync();var expired=SignedAt(own,creds.Seed,DateTimeOffset.UtcNow.AddDays(-91));
            var legacyBytes=JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,inviteUri=expired.Uri});Check(PeerClientPackage.ParsePublicContact(legacyBytes).IsExpired,"Signed expired sidecar cannot show fresh-invite route");
            await Reject(()=>PeerClientPackage.WritePublicContactAsync(Path.Combine(parent,"expired.json"),expired));
            var future=SignedAt(own,creds.Seed,DateTimeOffset.UtcNow.AddHours(1));bool futureRejected=false;try{PeerClientPackage.ParsePublicContact(JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,inviteUri=future.Uri}));}catch(InvalidDataException){futureRejected=true;}Check(futureRejected,"Future dated signed public contact accepted");
            await using var recipient=await PeerWorkspace.OpenAsync(Temp(),new Protector());Check((await recipient.ReadProfileAsync()).PeerId!=own.PeerId&&!await recipient.ReadLinkEnabledAsync()&&(await recipient.ListContactsAsync()).Count==0,"Reading public package cloned identity/contacts or opted in");
        });
        Add("ShareZipPreservesInstallerAndContainsOnlyPublicContact", async () => {
            var path=Temp();await using var w=await PeerWorkspace.OpenAsync(path,new Protector());await w.SetNicknameAsync("Share owner");var display=await w.CreateInviteAsync();var parent=Path.GetDirectoryName(path)!;
            var exe=Path.Combine(parent,"original.exe");var bytes=Encoding.UTF8.GetBytes("MZ fixture unchanged installer bytes");await File.WriteAllBytesAsync(exe,bytes);var zip=Path.Combine(parent,"share.zip");
            await PeerClientPackage.CreateShareZipAsync(exe,zip,display);
            using(var archive=ZipFile.OpenRead(zip)) {
                Check(archive.Entries.Select(e=>e.FullName).Order().SequenceEqual(new[]{PeerClientPackage.InstallerFileName,PeerClientPackage.PublicContactFileName}.Order()),"Share ZIP contains unexpected entries");
                using var installer=archive.GetEntry(PeerClientPackage.InstallerFileName)!.Open();using var copy=new MemoryStream();await installer.CopyToAsync(copy);Check(copy.ToArray().SequenceEqual(bytes),"Packaged EXE bytes changed");
                using var contact=archive.GetEntry(PeerClientPackage.PublicContactFileName)!.Open();using var raw=new MemoryStream();await contact.CopyToAsync(raw);Check(PeerClientPackage.ParsePublicContact(raw.ToArray()).InviteUri==display.Uri,"Share ZIP lost sender signed public invite");
            }
            Check((await File.ReadAllBytesAsync(exe)).SequenceEqual(bytes),"Original installer changed");
            var hash=System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(zip));
            bool existing=false;try{await PeerClientPackage.CreateShareZipAsync(exe,zip,display);}catch(IOException){existing=true;}Check(existing&&System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(zip)).SequenceEqual(hash),"Existing destination overwritten");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();var cancelledZip=Path.Combine(parent,"cancelled.zip");try{await PeerClientPackage.CreateShareZipAsync(exe,cancelledZip,display,cancelled.Token);}catch(OperationCanceledException){}
            Check(!File.Exists(cancelledZip)&&Directory.GetFiles(parent,"*.tmp-*").Length==0,"Cancelled package left partial output");
            var link=Path.Combine(parent,"linked.exe");string? junction=null;
            if(OperatingSystem.IsWindows()) {
                junction=Path.Combine(parent,"linked-directory");using var mklink=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe","/c mklink /J \""+junction+"\" \""+parent+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;await mklink.WaitForExitAsync();
                Check(mklink.ExitCode==0,"Native junction fixture could not be created");link=Path.Combine(junction,"original.exe");
            }else File.CreateSymbolicLink(link,exe);
            bool rejected=false;try{await PeerClientPackage.CreateShareZipAsync(link,Path.Combine(parent,"linked.zip"),display);}catch(IOException){rejected=true;}Check(rejected&&!File.Exists(Path.Combine(parent,"linked.zip")),"Linked EXE packaged");if(junction!=null)Directory.Delete(junction);else File.Delete(link);
            var large=Path.Combine(parent,"large.exe");using(var f=new FileStream(large,FileMode.CreateNew,FileAccess.Write)){f.Write(bytes);f.SetLength(512L*1024*1024+1);}bool tooLarge=false;try{await PeerClientPackage.CreateShareZipAsync(large,Path.Combine(parent,"large.zip"),display);}catch(InvalidDataException){tooLarge=true;}Check(tooLarge,"Oversized EXE packaged");File.Delete(large);
        });
        Add("InstalledInviteDismissalIsBoundedPersistentAndOffline", async () => {
            var path=Temp();await using var w=await PeerWorkspace.OpenAsync(path,new Protector());var one=Id();var two=Id();
            Check(!await w.IsInstalledInviteDismissedAsync(one),"Fresh installed invitation already dismissed");await w.DismissInstalledInviteAsync(one);
            Check(await w.IsInstalledInviteDismissedAsync(one)&&!await w.ReadLinkEnabledAsync(),"Dismissal opts into network or is not scoped");await w.DismissInstalledInviteAsync(two);
            Check(!await w.IsInstalledInviteDismissedAsync(one)&&await w.IsInstalledInviteDismissedAsync(two),"Dismissal preference accumulated stale invitation IDs");
            await w.DisposeAsync();await using var reopened=await PeerWorkspace.OpenAsync(path,new Protector());Check(await reopened.IsInstalledInviteDismissedAsync(two),"Dismissal did not persist across restart");
        });
        Add("IntroductionRequiresCapabilityAndAuthenticatedReturnIdentity", async () => {
            var path=Temp();await using var owner=await PeerWorkspace.OpenAsync(path,new Protector());await owner.SetNicknameAsync("Owner");
            var cap=PeerInviteCodec.Parse((await owner.CreateInviteAsync()).Uri);var strangerPath=Temp();await using var stranger=await PeerWorkspace.OpenAsync(strangerPath,new Protector());await stranger.SetNicknameAsync("Stranger");
            var reply=(await stranger.CreateInviteAsync()).Uri;var peer=(await stranger.ReadProfileAsync()).PeerId;var generation=await owner.ReadLinkGenerationAsync();
            Check(!await owner.AcceptIntroductionAsync(generation,peer,Id(),cap.InviteId.ToString("D"),reply),"Disabled request accepted");
            generation=await owner.SetLinkEnabledAsync(true);
            Check(!await owner.AcceptIntroductionAsync(generation,"wrong-peer",Id(),cap.InviteId.ToString("D"),reply),"Return identity mismatch accepted");
            Check(!await owner.AcceptIntroductionAsync(generation,peer,Id(),Id(),reply),"Unregistered capability accepted");
            Check(!await owner.AcceptIntroductionAsync(generation,peer,Id(),cap.InviteId.ToString("D"),reply+"tampered"),"Tampered signed return accepted");
            Check(await owner.AcceptIntroductionAsync(generation,peer,Id(),cap.InviteId.ToString("D"),reply),"Valid opt-in request rejected");
            Check((await owner.ListContactsAsync()).Count==0&&(await owner.ListMessagesAsync(peer)).Count==0,"Request auto-added contact or message");
            Check(!await owner.AcceptIncomingAsync(generation,peer,Id(),"text","unknown"),"Pending request opened ordinary messaging");
            var initial=(await owner.ListContactRequestsAsync()).Single();
            Check(await owner.AcceptIntroductionAsync(generation,peer,Id(),cap.InviteId.ToString("D"),reply)&&(await owner.ListContactRequestsAsync()).Single()==initial,"Same-peer retry duplicated or mutated request");
            await owner.RevokeInviteAsync(cap.InviteId.ToString("D"));
            Check(!await owner.AcceptIntroductionAsync(generation,peer,Id(),cap.InviteId.ToString("D"),reply),"Revoked capability accepted");
            Check(await owner.AcceptContactRequestAsync(peer)&&(await owner.ListContactsAsync()).Single().PeerId==peer&&(await owner.ListContactRequestsAsync()).Count==0,"Explicit acceptance did not grant contact");
            Check(await owner.AcceptIncomingAsync(generation,peer,Id(),"text","accepted"),"Accepted contact messaging failed");
        });
        Add("InvitationExpiryPruningAndRequestLimitsAreDurable", async () => {
            var path=Temp();await using var owner=await PeerWorkspace.OpenAsync(path,new Protector());await owner.SetNicknameAsync("Owner");
            var first=PeerInviteCodec.Parse((await owner.CreateInviteAsync()).Uri);
            for(int i=0;i<16;i++)await owner.CreateInviteAsync();var active=await owner.ListIssuedInvitesAsync();
            Check(active.Count==16&&active.All(i=>i.Id!=first.InviteId.ToString("D"))&&active.All(i=>i.ExpiresUtc>DateTimeOffset.UtcNow.AddDays(89)),"Invite lifetime or oldest pruning failed");
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(path,"messages.db"),Pooling=false}.ToString())) {
                db.Open();using var q=db.CreateCommand();q.CommandText="UPDATE IssuedInvites SET ExpiresUtc='2000-01-01T00:00:00.0000000+00:00' WHERE Id=$id";q.Parameters.AddWithValue("$id",active[0].Id);q.ExecuteNonQuery();
            }
            Check((await owner.ListIssuedInvitesAsync()).Count==15,"Expired local invite stayed active");var cap=(await owner.ListIssuedInvitesAsync()).Last();var generation=await owner.SetLinkEnabledAsync(true);
            string? lastPeer=null;
            for(int i=0;i<17;i++) {
                await using var sender=await PeerWorkspace.OpenAsync(Temp(),new Protector());await sender.SetNicknameAsync("Sender "+i);
                var reply=(await sender.CreateInviteAsync()).Uri;var peer=(await sender.ReadProfileAsync()).PeerId;
                Check(await owner.AcceptIntroductionAsync(generation,peer,Id(),cap.Id,reply)==(i<16),"Pending request limit not enforced");if(i==0)lastPeer=peer;
            }
            Check((await owner.ListContactRequestsAsync()).Count==16,"Wrong request retention");
            Check(await owner.RejectContactRequestAsync(lastPeer!)&&(await owner.ListContactRequestsAsync()).Count==15&&(await owner.ListContactsAsync()).Count==0,"Reject granted access or lost unrelated requests");
            await owner.SetLinkEnabledAsync(false);var fresh=await owner.SetLinkEnabledAsync(true);
            var c=await Contact();
            await using var sender2=await PeerWorkspace.OpenAsync(Temp(),new Protector());await sender2.SetNicknameAsync("Late");var returnUri=(await sender2.CreateInviteAsync()).Uri;var remote=(await sender2.ReadProfileAsync()).PeerId;
            Check(!await owner.AcceptIntroductionAsync(generation,remote,Id(),cap.Id,returnUri)&&fresh>generation,"Stale generation accepted introduction");
            await owner.DisposeAsync();await using var reopened=await PeerWorkspace.OpenAsync(path,new Protector());
            Check((await reopened.ListContactRequestsAsync()).Count==15&&(await reopened.ListIssuedInvitesAsync()).Count==15,"Restart lost durable capabilities/requests");
            Check(!await reopened.AcceptIntroductionAsync(await reopened.ReadLinkGenerationAsync(),remote,Id(),cap.Id,returnUri),"Restart activated receiving from persisted preference");
        });
        Add("IncomingOwnerKeepsDistinctContactLabel", () => {
            var incoming=new PeerMessage(Id(),"peer",false,"text","Привет",DateTimeOffset.UtcNow,"Received");
            var first=new PeerMessageItem(incoming,"Наташа · 12345678");
            var sameName=new PeerMessageItem(incoming,"Наташа · 87654321");
            Check(first.Title=="Наташа · 12345678"&&sameName.Title=="Наташа · 87654321","Incoming author must use actual distinct contact label instead of generic message title");
            return Task.CompletedTask;
        });
        Add("ServerCardKeepsOwnerSeparateFromSafeServerPreview", () => {
            const string uri="vless://11111111-1111-4111-8111-111111111111@example.invalid:443#SharedServer";
            var message=new PeerMessage(Id(),"peer",false,"server",uri,DateTimeOffset.UtcNow,"Received");
            var incoming=new PeerMessageItem(message,"Наташа · 12345678");
            var outgoing=new PeerMessageItem(message with {Outgoing=true,Status="Failed"},"Наташа · 12345678");
            Check(incoming.Title=="Наташа · 12345678"&&!incoming.IsOutgoing&&incoming.ServerTitle=="SharedServer"&&incoming.CanAddServer,"Received server loses owner or explicit add action");
            Check(incoming.Body=="example.invalid:443"&&!incoming.Body.Contains("11111111"),"Preview exposes server credentials");
            Check(outgoing.Title=="Вы"&&outgoing.IsOutgoing&&outgoing.ServerTitle=="SharedServer"&&!outgoing.CanAddServer&&outgoing.CanRetry,"Sent server loses self owner or retry action");
            var malformed=new PeerMessageItem(message with {Text="invalid configuration"},"Наташа · 12345678");
            Check(malformed.Title==incoming.Title&&malformed.ServerTitle=="Сервер"&&!malformed.CanAddServer,"Malformed server loses contact owner or can be imported");
            return Task.CompletedTask;
        });
        Add("DefaultDisabledRejectsTraffic", async () => {
            await using var w = await PeerWorkspace.OpenAsync(Temp(), new Protector()); var c = await Contact(); await w.AddContactAsync(c);
            Check(!await w.ReadLinkEnabledAsync(), "Fresh profile opted in");
            await Reject(() => w.AddOutgoingAsync(c.PeerId,"text","blocked"));
            Check(!await w.AcceptIncomingAsync(await w.ReadLinkGenerationAsync(),c.PeerId,Id(),"text","blocked"),"Disabled incoming accepted");
            Check((await w.ListMessagesAsync(c.PeerId)).Count==0,"Disabled traffic persisted");
        });
        Add("OutgoingPersistsPendingAndAcknowledgementIsMonotonic", async () => {
            var f=await Fixture(); await using var w=f.W;
            var m=await w.AddOutgoingAsync(f.Peer,"text","hello");
            Check(Guid.TryParseExact(m.Id,"D",out var id)&&id!=Guid.Empty&&m.Outgoing&&m.Status=="Pending"&&m.Text=="hello","Wrong pending model");
            await w.MarkOutgoingAsync(f.Peer,m.Id,false); Check((await w.ListMessagesAsync(f.Peer)).Single().Status=="Failed","Failure lost");
            await w.MarkOutgoingAsync(f.Peer,m.Id,true); await w.MarkOutgoingAsync(f.Peer,m.Id,false);
            Check((await w.ListMessagesAsync(f.Peer)).Single().Status=="Delivered","Late failure downgraded delivery");
        });
        Add("DuplicateAcknowledgesExistingAndRejectsConflictingPayload", async () => {
            var f=await Fixture();await using var w=f.W; var id=Id();
            Check(await w.AcceptIncomingAsync(f.Generation,f.Peer,id,"text","original"),"First receive failed");
            Check(await w.AcceptIncomingAsync(f.Generation,f.Peer,id.ToUpperInvariant(),"text","original"),"UUID representation bypassed durable dedupe");
            Check(!await w.AcceptIncomingAsync(f.Generation,f.Peer,id,"text","tampered"),"Conflicting content accepted");
            Check(!await w.AcceptIncomingAsync(f.Generation,f.Peer,id,"server","original"),"Conflicting kind accepted");
            var m=(await w.ListMessagesAsync(f.Peer)).Single(); Check(!m.Outgoing&&m.Status=="Received"&&m.Text=="original","Duplicate mutated existing receive");
        });
        Add("MessageIdIsScopedToAuthenticatedPeer", async () => {
            var f=await Fixture();await using var w=f.W;var c=await Contact(); await w.AddContactAsync(c);var id=Id();
            Check(await w.AcceptIncomingAsync(f.Generation,f.Peer,id,"text","first"),"First failed");
            Check(await w.AcceptIncomingAsync(f.Generation,c.PeerId,id,"text","second"),"Different peer deduped");
            Check((await w.ListMessagesAsync(f.Peer)).Single().Text=="first"&&(await w.ListMessagesAsync(c.PeerId)).Single().Text=="second","Conversation mixed");
        });
        Add("UnknownSelfMalformedIdAndKindsRejectWithoutWrites", async () => {
            var f=await Fixture();await using var w=f.W;var stranger=await Contact();var own=(await w.ReadProfileAsync()).PeerId;
            foreach(var peer in new[]{stranger.PeerId,own,"invalid"}) {
                Check(!await w.AcceptIncomingAsync(f.Generation,peer,Id(),"text","bad"),"Wrong peer received");
                await Reject(()=>w.AddOutgoingAsync(peer,"text","bad"));
            }
            foreach(var id in new[]{"", "bad", Guid.Empty.ToString("D"), Guid.NewGuid().ToString("N")}) Check(!await w.AcceptIncomingAsync(f.Generation,f.Peer,id,"text","bad"),"Invalid ID accepted");
            foreach(var kind in new[]{"", "Text", "attachment"}) { Check(!await w.AcceptIncomingAsync(f.Generation,f.Peer,Id(),kind,"bad"),"Unknown kind received"); await Reject(()=>w.AddOutgoingAsync(f.Peer,kind,"bad")); }
            Check((await w.ListMessagesAsync(f.Peer)).Count==0,"Invalid receive wrote history");
        });
        Add("TextServerAndUtf8LimitsRejectBeforePersist", async () => {
            var f=await Fixture();await using var w=f.W;
            await w.AddOutgoingAsync(f.Peer,"text",new string('x',2000)); await w.AddOutgoingAsync(f.Peer,"server",new string('x',32768));
            foreach(var item in new[]{("text",new string('x',2001)),("server",new string('x',32769)),("server",new string('界',22000)),("text","invalid\uD800")}) {
                await Reject(()=>w.AddOutgoingAsync(f.Peer,item.Item1,item.Item2));
                Check(!await w.AcceptIncomingAsync(f.Generation,f.Peer,Id(),item.Item1,item.Item2),"Malformed/oversize received");
            }
            Check((await w.ListMessagesAsync(f.Peer)).Count==2,"Oversize persisted");
        });
        Add("DisableAndReenableRejectAllPriorGenerationCallbacks", async () => {
            var f=await Fixture();await using var w=f.W;var pending=await w.AddOutgoingAsync(f.Peer,"text","queued");
            var disabled=await w.SetLinkEnabledAsync(false); Check(disabled>f.Generation&&!await w.ReadLinkEnabledAsync(),"Disable did not advance state");
            Check(!await w.AcceptIncomingAsync(f.Generation,f.Peer,Id(),"text","late"),"Disable callback wrote"); await Reject(()=>w.AddOutgoingAsync(f.Peer,"text","late"));
            await w.MarkOutgoingAsync(f.Peer,pending.Id,false); Check((await w.ListMessagesAsync(f.Peer)).Single().Status=="Failed","Shutdown did not mark pending failure");
            var fresh=await w.SetLinkEnabledAsync(true);Check(fresh>disabled,"Reenable reused generation");
            Check(!await w.AcceptIncomingAsync(f.Generation,f.Peer,Id(),"text","old session"),"Old generation callback accepted");
            Check(await w.AcceptIncomingAsync(fresh,f.Peer,Id(),"text","new session"),"Fresh receive rejected");
            Check((await w.ListMessagesAsync(f.Peer)).Count==2,"Late message persisted");
        });
        Add("RestartRetainsHistoryButRequiresExplicitRuntimeEnable", async () => {
            var f=await Fixture();var outgoing=await f.W.AddOutgoingAsync(f.Peer,"text","restart");await f.W.DisposeAsync();
            await using var w=await PeerWorkspace.OpenAsync(f.Path,new Protector());
            Check(await w.ReadLinkEnabledAsync(),"Persistent preference lost"); var generation=await w.ReadLinkGenerationAsync();Check(generation>f.Generation,"Restart reused generation");
            Check((await w.ListMessagesAsync(f.Peer)).Single().Id==outgoing.Id,"Restart lost message");
            Check(!await w.AcceptIncomingAsync(generation,f.Peer,Id(),"text","unsolicited"),"Persistent preference activated inbound session");
            await Reject(()=>w.AddOutgoingAsync(f.Peer,"text","before runtime enable"));
            var fresh=await w.SetLinkEnabledAsync(true);Check(await w.AcceptIncomingAsync(fresh,f.Peer,Id(),"text","connected"),"Explicit start failed");
        });
        Add("UnknownOutgoingAcknowledgementCannotInsertOrMutateInbound", async () => {
            var f=await Fixture();await using var w=f.W;var outgoing=await w.AddOutgoingAsync(f.Peer,"text","out");var id=Id();
            Check(await w.AcceptIncomingAsync(f.Generation,f.Peer,id,"text","in"),"Inbound failed");
            var other=await Contact();await w.AddContactAsync(other);await w.MarkOutgoingAsync(other.PeerId,outgoing.Id,true);
            await w.MarkOutgoingAsync(f.Peer,id,true);await w.MarkOutgoingAsync(f.Peer,Id(),true);
            var messages=await w.ListMessagesAsync(f.Peer);Check(messages.Count==2&&messages[0].Status=="Pending"&&messages[1].Status=="Received","Wrong-peer/fabricated ACK changed history");
        });
        Add("RetentionUsesNewestInsertionRowsAndIndependentConversationLimits", async () => {
            var f=await Fixture();await using var w=f.W;var other=await Contact();await w.AddContactAsync(other);await w.AddOutgoingAsync(other.PeerId,"text","keep other");
            for(int i=0;i<500;i++) await w.AddOutgoingAsync(f.Peer,"text","row "+i);
            // A clock correction must not make new inserts prune themselves or reorder the conversation.
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(f.Path,"messages.db"),Pooling=false}.ToString())) {
                db.Open();using var query=db.CreateCommand();query.CommandText="UPDATE Messages SET CreatedUtc='2099-01-01T00:00:00.0000000+00:00' WHERE PeerId=$peer";query.Parameters.AddWithValue("$peer",f.Peer);query.ExecuteNonQuery();
            }
            for(int i=500;i<505;i++) await w.AddOutgoingAsync(f.Peer,"text","row "+i);
            var messages=await w.ListMessagesAsync(f.Peer);Check(messages.Count==500&&messages[0].Text=="row 5"&&messages[^1].Text=="row 504","Retention did not keep newest 500 rows");
            Check((await w.ListMessagesAsync(other.PeerId)).Single().Text=="keep other","Retention deleted another conversation");
            await w.ClearConversationAsync(f.Peer);Check((await w.ListMessagesAsync(f.Peer)).Count==0&&(await w.ListMessagesAsync(other.PeerId)).Count==1,"Clear crossed conversation boundary");
            await w.SetLinkEnabledAsync(false);await w.ClearConversationAsync(other.PeerId);Check((await w.ListMessagesAsync(other.PeerId)).Count==0,"Disabled user clear failed");
        });
        Add("ReceiveInsertionAndRetentionRollBackAsOneTransaction", async () => {
            var f=await Fixture();await using var w=f.W;
            for(int i=0;i<500;i++) await w.AddOutgoingAsync(f.Peer,"text","row "+i);
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(f.Path,"messages.db"),Pooling=false}.ToString());db.Open();
            using var query=db.CreateCommand();query.CommandText="CREATE TRIGGER FailHistoryPrune BEFORE DELETE ON Messages BEGIN SELECT RAISE(ABORT, 'fixture blocked prune'); END;";query.ExecuteNonQuery();
            bool rejected=false;try{await w.AcceptIncomingAsync(f.Generation,f.Peer,Id(),"text","must rollback");}catch(SqliteException){rejected=true;}
            Check(rejected,"Storage failure incorrectly acknowledged");var messages=await w.ListMessagesAsync(f.Peer);Check(messages.Count==500&&messages[0].Text=="row 0"&&messages[^1].Text=="row 499","Insert survived failed retention transaction");
        });
        Add("CredentialsUseExistingIdentityAndActivelyClearSeed", async () => {
            var path=Temp();await using var w=await PeerWorkspace.OpenAsync(path,new Protector());var profile=await w.ReadProfileAsync();
            var credentials=await w.CreateLinkCredentialsAsync();var seed=credentials.Seed;Check(credentials.PeerId==profile.PeerId&&seed.Length==32&&seed.Any(b=>b!=0),"Different transport identity");
            var again=await w.CreateLinkCredentialsAsync();Check(again.Seed.SequenceEqual(seed)&&!ReferenceEquals(again.Seed,seed),"Credential identity changed/aliased");
            credentials.Dispose();Check(seed.All(b=>b==0),"Disposed credential seed retained");Check(again.Seed.Any(b=>b!=0),"Disposal zeroed another lease");again.Dispose();
        });
        Add("BackupRestoresIdentityWithoutHistoryAndInvalidatesOldSession", async () => {
            var source=await Fixture();await using var s=source.W;await s.SetNicknameAsync("Source");await s.AddOutgoingAsync(source.Peer,"server","vless://source-private");
            var sourceInvite=PeerInviteCodec.Parse((await s.CreateInviteAsync()).Uri);
            var backup=Path.Combine(Path.GetDirectoryName(source.Path)!,"identity.madyarbackup");await s.ExportBackupAsync(backup,"test password long");
            var target=await Fixture();await using var w=target.W;await w.SetNicknameAsync("Target");var targetInvite=PeerInviteCodec.Parse((await w.CreateInviteAsync()).Uri);
            await w.DismissInstalledInviteAsync(targetInvite.InviteId.ToString("D"));
            await using var applicant=await PeerWorkspace.OpenAsync(Temp(),new Protector());await applicant.SetNicknameAsync("Applicant");var reply=(await applicant.CreateInviteAsync()).Uri;var applicantPeer=(await applicant.ReadProfileAsync()).PeerId;
            Check(await w.AcceptIntroductionAsync(target.Generation,applicantPeer,Id(),targetInvite.InviteId.ToString("D"),reply),"Pre-restore request fixture rejected");
            var old=await w.AddOutgoingAsync(target.Peer,"text","old identity history");using var preview=await w.PreviewBackupAsync(backup,"test password long");
            await w.RestoreBackupAsync(preview,true);Check((await w.ReadProfileAsync()).PeerId==(await s.ReadProfileAsync()).PeerId,"Backup identity changed");
            Check(!await w.ReadLinkEnabledAsync()&&(await w.ListMessagesAsync(source.Peer)).Count==0,"Backup leaked source history or enabled state");
            Check((await w.ListMessagesAsync(target.Peer)).Count==0,"Prior identity history mixed with restored identity");
            Check(await w.ReadLinkGenerationAsync()>target.Generation&&!await w.AcceptIncomingAsync(target.Generation,source.Peer,Id(),"text","old callback"),"Restore retained live session");
            Check((await w.ListIssuedInvitesAsync()).Count==0&&(await w.ListContactRequestsAsync()).Count==0,"Restore imported stale capabilities or requests");
            Check(!await w.IsInstalledInviteDismissedAsync(targetInvite.InviteId.ToString("D")),"Restore retained prior identity's installed invitation dismissal");
            var freshGeneration=await w.SetLinkEnabledAsync(true);
            Check(!await w.AcceptIntroductionAsync(freshGeneration,applicantPeer,Id(),targetInvite.InviteId.ToString("D"),reply)&&!await w.AcceptIntroductionAsync(freshGeneration,applicantPeer,Id(),sourceInvite.InviteId.ToString("D"),reply),"Restore accepted old or backup-source capability");
            var preserved=Directory.GetDirectories(Path.GetDirectoryName(target.Path)!,".profile.backup-*").Single();
            Check(File.Exists(Path.Combine(preserved,"messages.db")),"Restore discarded prior history checkpoint");
        });
        Add("IdentityBindingRejectsHistoryCopiedFromAnotherIdentity", async () => {
            var a=await Fixture();await a.W.AddOutgoingAsync(a.Peer,"text","private A");await a.W.DisposeAsync();
            var b=await Fixture();await b.W.DisposeAsync();File.Copy(Path.Combine(a.Path,"messages.db"),Path.Combine(b.Path,"messages.db"),true);
            bool rejected=false;try{await using var w=await PeerWorkspace.OpenAsync(b.Path,new Protector());}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Another identity's history opened");
        });
        Add("MissingIdentityBindingNeverAdoptsExistingHistory", async () => {
            var f=await Fixture();await f.W.AddOutgoingAsync(f.Peer,"text","private history");await f.W.DisposeAsync();
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(f.Path,"messages.db"),Pooling=false}.ToString())) {
                db.Open();using var query=db.CreateCommand();query.CommandText="DROP TABLE LinkState";query.ExecuteNonQuery();
            }
            bool rejected=false;try{await using var w=await PeerWorkspace.OpenAsync(f.Path,new Protector());}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Unbound prior history adopted a possibly different identity");
        });
        Add("CancellationDoesNotPersistReceiveOrOptIn", async () => {
            var f=await Fixture();await using var w=f.W;using var ct=new CancellationTokenSource();ct.Cancel();
            try{await w.SetLinkEnabledAsync(false,ct.Token);}catch(OperationCanceledException){}
            Check(await w.ReadLinkEnabledAsync()&&await w.ReadLinkGenerationAsync()==f.Generation,"Cancelled toggle wrote state");
            try{await w.AcceptIncomingAsync(f.Generation,f.Peer,Id(),"text","cancelled",ct.Token);}catch(OperationCanceledException){}
            Check((await w.ListMessagesAsync(f.Peer)).Count==0,"Cancelled receive persisted");
        });
        int passed=0,failed=0;foreach(var test in Tests)try{await test.Run();Console.WriteLine("PASS "+test.Name);passed++;}catch(Exception e){Console.WriteLine("FAIL "+test.Name+": "+e);failed++;}
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");Environment.ExitCode=failed==0?0:1;
    }
    sealed class Protector:IIdentityProtector {public byte[] Protect(byte[] value)=>value.Select(b=>(byte)(b^0xA5)).ToArray();public byte[] Unprotect(byte[] value)=>Protect(value);}
}
