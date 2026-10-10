using Madyar.PeerChat;
namespace PeerChatChecks;
internal static partial class Program {
 static void IdentityTests() {
  Tests.Add(("identity","Ed25519_Rfc8032_Vector1",()=>{
   var seed=Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
   Check(Convert.ToHexString(PeerSigning.PublicKey(seed)).ToLowerInvariant()=="d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
   Check(Convert.ToHexString(PeerSigning.Sign(seed,[])).ToLowerInvariant()=="e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");return Task.CompletedTask;
  }));
  Tests.Add(("identity","PeerId_Libp2p_Vector",()=>{
   var pub=Convert.FromHexString("1ed1e8fae2c4a144b8be8fd4b47bf3d3b34b871c3cacf6010f0e42d474fce27e");
   Check(PeerIdCodec.FromPublicKey(pub)=="12D3KooWBtg3aaRMjxwedh83aGiUkwSxDwUZkzuJcfaqUmo7R3pq");return Task.CompletedTask;
  }));
  Tests.Add(("identity","RestartAndNicknameKeepSameKey",async()=>{var p=Temp();PeerProfile a;await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector())){a=await w.ReadProfileAsync();await w.SetNicknameAsync("  Егор  ");Check((await w.ReadProfileAsync()).Nickname=="Егор");await w.SetNicknameAsync("admin");}await using var w2=await PeerWorkspace.OpenAsync(p,new TestProtector());var b=await w2.ReadProfileAsync();Check(a.PeerId==b.PeerId&&a.PublicKey.SequenceEqual(b.PublicKey)&&b.Nickname=="admin");}));
  Tests.Add(("identity","CorruptKeyDoesNotRegenerate",async()=>{var p=Temp();await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector())){}File.WriteAllText(Path.Combine(p,"identity.json"),"broken");await Throws(async()=>{await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector());});Check(File.ReadAllText(Path.Combine(p,"identity.json"))=="broken");}));
  Tests.Add(("identity","ConcurrentOpenCreatesOneIdentity",async()=>{var p=Temp();await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector());var key=(await w.ReadProfileAsync()).PeerId;await Throws(async()=>{await using var other=await PeerWorkspace.OpenAsync(p,new TestProtector());});Check((await w.ReadProfileAsync()).PeerId==key);}));
  Tests.Add(("identity","CancellationBeforeCommitLeavesNoPartialProfile",async()=>{var p=Temp();using var c=new CancellationTokenSource();c.Cancel();try { await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector(),c.Token);throw new TestFailure("Cancellation ignored"); } catch(OperationCanceledException) {}Check(!Directory.Exists(p));}));
 }
}
