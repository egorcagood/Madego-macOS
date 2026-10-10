using Madyar.PeerChat;
namespace PeerChatChecks;
internal static partial class Program {
 static void ContactManagementTests() {
 Tests.Add(("contacts","DeletePreservesHistoryButRejectsLateMessages",async()=>{
  await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());var c=Contact();await w.AddContactAsync(c);var g=await w.SetLinkEnabledAsync(true);
  Check(await w.AcceptIncomingAsync(g,c.PeerId,Guid.NewGuid().ToString("D"),"text","Сохранить"));
  Check(await w.RemoveContactAsync(c.PeerId));Check((await w.ListContactsAsync()).Count==0);
  Check(!await w.AcceptIncomingAsync(g,c.PeerId,Guid.NewGuid().ToString("D"),"text","Поздно"));
  Check((await w.ListMessagesAsync(c.PeerId)).Count==1);Check(await w.AddContactAsync(c));
  Check((await w.ListMessagesAsync(c.PeerId)).Single().Text=="Сохранить");
 }));
 Tests.Add(("contacts","BlockPersistsAndRejectsReintroductionUntilManualUnblock",async()=>{
  var path=Temp();var c=Contact();
  await using(var w=await PeerWorkspace.OpenAsync(path,new TestProtector())) {
   await w.AddContactAsync(c);await w.SetNicknameAsync("Owner");var own=PeerInviteCodec.Parse((await w.CreateInviteAsync()).Uri);var g=await w.SetLinkEnabledAsync(true);
   Check(await w.BlockContactAsync(c.PeerId));Check(await w.IsBlockedAsync(c.PeerId));
   Check(!await w.AcceptIncomingAsync(g,c.PeerId,Guid.NewGuid().ToString("D"),"text","no"));
   await Throws(()=>w.AddContactAsync(c));
  }
  await using var again=await PeerWorkspace.OpenAsync(path,new TestProtector());
  Check((await again.ListBlockedContactsAsync()).Single().PeerId==c.PeerId);Check((await again.ListContactsAsync()).Count==0);
  Check(await again.UnblockContactAsync(c.PeerId));Check((await again.ListContactsAsync()).Count==0);Check(await again.AddContactAsync(c));
 }));
 Tests.Add(("contacts","BlockedRequestNeverPersistsAndDeleteAllowsFreshRequest",async()=>{
  await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());await using var other=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());
  await w.SetNicknameAsync("Owner");await other.SetNicknameAsync("Sender");var target=PeerInviteCodec.Parse((await w.CreateInviteAsync()).Uri);var sender=await other.CreateInviteAsync();var c=PeerInviteCodec.Parse(sender.Uri);await w.AddContactAsync(c);var g=await w.SetLinkEnabledAsync(true);
  await w.RemoveContactAsync(c.PeerId);Check(await w.AcceptIntroductionAsync(g,c.PeerId,Guid.NewGuid().ToString("D"),target.InviteId.ToString("D"),sender.Uri));
  Check((await w.ListContactRequestsAsync()).Count==1);Check(await w.BlockContactAsync(c.PeerId));Check((await w.ListContactRequestsAsync()).Count==0);
  Check(!await w.AcceptIntroductionAsync(g,c.PeerId,Guid.NewGuid().ToString("D"),target.InviteId.ToString("D"),sender.Uri));Check(!await w.AcceptContactRequestAsync(c.PeerId));
  await w.UnblockContactAsync(c.PeerId);Check((await w.ListContactsAsync()).Count==0);
  Check(await w.AcceptIntroductionAsync(g,c.PeerId,Guid.NewGuid().ToString("D"),target.InviteId.ToString("D"),sender.Uri));Check(await w.AcceptContactRequestAsync(c.PeerId));
  Check((await w.GetContactInviteAsync(c.PeerId))?.SignedUri==sender.Uri);
 }));
 Tests.Add(("contacts","BlockedLateAckPersistsFailedWithoutFalseDelivery",async()=>{
  await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());var c=Contact();await w.AddContactAsync(c);var g=await w.SetLinkEnabledAsync(true);var m=await w.AddOutgoingAsync(c.PeerId,"text","pending");await w.BlockContactAsync(c.PeerId);
  Check(!await w.MarkOutgoingAsync(g,c.PeerId,m.Id,true));Check((await w.ListMessagesAsync(c.PeerId)).Single().Status=="Failed");
 }));
 Tests.Add(("backup","EncryptedBackupRetainsBlocks",async()=>{
  var p=Temp();var b=BackupPath(p);var c=Contact();
  await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector())) {await w.AddContactAsync(c);await w.BlockContactAsync(c.PeerId);await w.ExportBackupAsync(b,Password);}
  await using var restored=await PeerWorkspace.OpenAsync(Temp(),new TestProtector(71));using var preview=await restored.PreviewBackupAsync(b,Password);
  await restored.RestoreBackupAsync(preview,true);Check(await restored.IsBlockedAsync(c.PeerId));Check((await restored.ListContactsAsync()).Count==0);
 }));
 Tests.Add(("backup","LegacyV1BackupStillImportsWithoutBlocks",async()=>{
  await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());using var credentials=await w.CreateLinkCredentialsAsync();
  using var snapshot=new BackupSnapshot(1,credentials.Seed.ToArray(),await w.ReadProfileAsync(),[]);
  var bytes=PeerBackupCodec.Encrypt(snapshot,Password,default);using var decoded=PeerBackupCodec.Decrypt(bytes,Password,default);
  Check(decoded.SchemaVersion==1);Check(decoded.BlockedContacts==null||decoded.BlockedContacts.Length==0);
 }));
 }
}
