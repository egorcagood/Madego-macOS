using Madyar.PeerChat;
using Microsoft.Data.Sqlite;
namespace PeerChatChecks;
internal static partial class Program {
 static VerifiedContactInvite Contact(string name="Контакт") { var k=PeerSigning.PublicKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));return new(PeerIdCodec.FromPublicKey(k),k,name,Guid.NewGuid(),DateTimeOffset.UtcNow); }
 static void ContactTests() {
 Tests.Add(("contacts","MaximumContactLabelsHaveBoundedAllocationAndDisambiguate",()=>{
  var rows=Enumerable.Range(0,10000).Select(i=>{var key=System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i));return new PeerContact(PeerIdCodec.FromPublicKey(key),key,"Егор",null,DateTimeOffset.UtcNow,null);}).ToArray();
  long before=GC.GetAllocatedBytesForCurrentThread();var labels=PeerContactLabels.Create(rows);long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
  Check(labels.Count==10000&&allocated<16*1024*1024,$"Label allocation is unbounded: {allocated} bytes");
  var collision=new[]{rows[0] with {PeerId="first1111abcdefgh",Alias="Друг"},rows[1] with {PeerId="second2222abcdefgh"},rows[2] with {PeerId="third2222abcdefgh"}};
  var names=PeerContactLabels.Create(collision);Check(names[0]=="Друг · 1111abcdefgh"&&names[1]=="Егор · second2222abcdefgh"&&names[2]=="Егор · third2222abcdefgh");return Task.CompletedTask;
 }));

 Tests.Add(("contacts","ContactsPersistActualSqlite",async()=>{var p=Temp();var c=Contact();await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector())){Check(await w.AddContactAsync(c));}using(var db=new SqliteConnection($"Data Source={Path.Combine(p,"contacts.db")};Pooling=False")){db.Open();using var q=db.CreateCommand();q.CommandText="SELECT COUNT(*) FROM Contacts";Check((long)q.ExecuteScalar()! ==1);}await using var w2=await PeerWorkspace.OpenAsync(p,new TestProtector());var items=await w2.ListContactsAsync();Check(items.Count==1&&items[0].PublicKey.SequenceEqual(c.PublicKey)&&items[0].LastSeenUtc==null);}));
 Tests.Add(("contacts","DuplicateInviteKeepsAlias",async()=>{await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());var c=Contact();Check(await w.AddContactAsync(c));await w.SetAliasAsync(c.PeerId,"Друг");Check(!await w.AddContactAsync(new(c.PeerId,c.PublicKey,"Changed",Guid.NewGuid(),DateTimeOffset.UtcNow)));var a=(await w.ListContactsAsync()).Single();Check(a.Alias=="Друг"&&a.Nickname=="Контакт");}));
 Tests.Add(("contacts","SelfContactRejected",async()=>{await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());var p=await w.ReadProfileAsync();await Throws(()=>w.AddContactAsync(new(p.PeerId,p.PublicKey,"Self",Guid.NewGuid(),DateTimeOffset.UtcNow)));Check((await w.ListContactsAsync()).Count==0);}));
 Tests.Add(("contacts","PeerIdKeyMismatchRejectedWithoutWrites",async()=>{await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());var c=Contact();await Throws(()=>w.AddContactAsync(new(c.PeerId,new byte[32],c.Nickname,Guid.NewGuid(),DateTimeOffset.UtcNow)));Check((await w.ListContactsAsync()).Count==0);}));
 Tests.Add(("contacts","DuplicateNamesRemainDistinct",async()=>{await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());var a=Contact("Егор");var b=Contact("Егор");await w.AddContactAsync(a);await w.AddContactAsync(b);Check((await w.ListContactsAsync()).Count==2&&a.PeerId!=b.PeerId);}));
 Tests.Add(("contacts","DraftsPersistPerPeer",async()=>{var p=Temp();var a=Contact();var b=Contact();await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector())){await w.AddContactAsync(a);await w.AddContactAsync(b);await w.SaveDraftAsync(a.PeerId,"Черновик");await w.SaveDraftAsync(b.PeerId,"Второй");await Throws(()=>w.SaveDraftAsync(a.PeerId,new string('x',2001)));}await using var w2=await PeerWorkspace.OpenAsync(p,new TestProtector());Check(await w2.GetDraftAsync(a.PeerId)=="Черновик"&&await w2.GetDraftAsync(b.PeerId)=="Второй");}));
 Tests.Add(("contacts","SimultaneousDraftAndProfileWriteNotLost",async()=>{await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());var c=Contact();await w.AddContactAsync(c);await Task.WhenAll(w.SetNicknameAsync("admin"),w.SaveDraftAsync(c.PeerId,"text"));Check((await w.ReadProfileAsync()).Nickname=="admin"&&await w.GetDraftAsync(c.PeerId)=="text");}));
 }
}
