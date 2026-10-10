using EgorVpn.Services;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Text;
namespace PeerChatChecks;
internal static partial class Program {
 static async Task<string> ActivationInvite(){await using var w=await Madyar.PeerChat.PeerWorkspace.OpenAsync(Temp(),new TestProtector());await w.SetNicknameAsync("Егор");return (await w.CreateInviteAsync()).Uri;}
 static void ActivationTests() {
 Tests.Add(("activation","ExactArgumentsAndUnchangedUri",async()=>{var uri=await ActivationInvite();Check(PeerContactActivation.ParseArgs(["--import-contact",uri])==uri);Check(PeerContactActivation.ParseArgs([])==null);foreach(var a in new[]{new[]{"--import-contact"},new[]{"--import-contact",uri,"extra"},new[]{"other","--import-contact",uri},new[]{"--import-contact",uri+"\""},new[]{"--import-contact",uri+"\n"},new[]{"--import-contact","https://example.org"},new[]{"--import-contact",new string('x',2049)}})await Throws(()=>{PeerContactActivation.ParseArgs(a);return Task.CompletedTask;});}));
 Tests.Add(("activation","InvalidStartupDoesNotCreateDataOrMigrateLegacy",async()=>{
  var uri=await ActivationInvite();var p=Temp();var parent=Path.GetDirectoryName(p)!;var legacy=Path.Combine(parent,"legacy");Directory.CreateDirectory(legacy);var original="legacy VPN subscription fixture";File.WriteAllText(Path.Combine(legacy,"subscriptions.json"),original);
  void Prepare(){Directory.CreateDirectory(p);File.Copy(Path.Combine(legacy,"subscriptions.json"),Path.Combine(p,"subscriptions.json"));}
  foreach(var args in new[]{new[]{"--import-contact","madyar://contact/ew"},new[]{"--import-contact",uri,"extra"}}) {await Throws(()=>{PeerContactActivation.PrepareStartup(args,Prepare);return Task.CompletedTask;});Check(!Directory.Exists(p),"Rejected activation created/migrated data");Check(File.ReadAllText(Path.Combine(legacy,"subscriptions.json"))==original);}
  Check(PeerContactActivation.PrepareStartup(["--import-contact",uri],Prepare)==uri);Check(File.ReadAllText(Path.Combine(p,"subscriptions.json"))==original);
 }));
 Tests.Add(("activation","LocalPipeTransfersOnlyPreviewAndCancels",async()=>{var uri=await ActivationInvite();var a=new PeerContactActivation(Guid.NewGuid().ToString("N")[..20]);string? seen=null;using var c=new CancellationTokenSource();await using(var r=await a.StartReceiver(s=>{seen=s;return Task.CompletedTask;},c.Token)){Check(await a.TryForwardToRunningInstanceAsync(uri));for(int i=0;seen==null&&i<100;i++)await Task.Delay(10);Check(seen==uri);c.Cancel();}Check(!await a.TryForwardToRunningInstanceAsync(uri));}));
 Tests.Add(("activation","MalformedPipeFramesDoNotStopSubsequentValidPreview",async()=>{
  var uri=await ActivationInvite();var scope=Guid.NewGuid().ToString("N")[..20];var a=new PeerContactActivation(scope);var seen=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
  await using var receiver=await a.StartReceiver(s=>{seen.TrySetResult(s);return Task.CompletedTask;});
  foreach(var invalid in new[]{"madyar://contact/ew","madyar://contact/!!!","madyar://contact/"+Convert.ToBase64String("{\"version\":\"wrong\",\"payload\":\"x\",\"signature\":\"x\"}"u8.ToArray()).TrimEnd('=').Replace('+','-').Replace('/','_')}) {
   using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
   await using var pipe=new NamedPipeClientStream(".","MadyarVPN-peer-import-"+scope,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
   await pipe.ConnectAsync(deadline.Token);var bytes=Encoding.UTF8.GetBytes(invalid);var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
   // A rejected frame closes the pipe immediately; the sender may observe EOF,
   // BrokenPipe on flush, or a negative acknowledgement depending on scheduling.
   try {await pipe.WriteAsync(header,deadline.Token);await pipe.WriteAsync(bytes,deadline.Token);await pipe.FlushAsync(deadline.Token);
    var ack=new byte[1];var length=await pipe.ReadAsync(ack,deadline.Token);Check(length==0||ack[0]==0,"Malformed input accepted");
   }catch(IOException){}
  }
  Check(await a.TryForwardToRunningInstanceAsync(uri),"Listener stopped after malformed input");Check(await seen.Task.WaitAsync(TimeSpan.FromSeconds(3))==uri);
 }));
 Tests.Add(("activation","BoundedQueueRejectsInsteadOfDropping",async()=>{var uri=await ActivationInvite();var a=new PeerContactActivation(Guid.NewGuid().ToString("N")[..20]);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await using var r=await a.StartReceiver(async _=>{entered.TrySetResult();await release.Task;});try {Check(await a.TryForwardToRunningInstanceAsync(uri));await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));for(int i=0;i<3;i++)Check(await a.TryForwardToRunningInstanceAsync(uri));Check(!await a.TryForwardToRunningInstanceAsync(uri));}finally{release.SetResult();}}));
 }
}
