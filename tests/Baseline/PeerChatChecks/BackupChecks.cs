using Madyar.PeerChat;
using System.Buffers.Binary;
namespace PeerChatChecks;
internal static partial class Program {
 const string Password="test password with spaces";
 static string BackupPath(string p)=>Path.Combine(Path.GetDirectoryName(p)!,Guid.NewGuid().ToString("N")+".madyarbackup");
 static void BackupTests() {
 Tests.Add(("backup","BackupRestoresIdentityAndContactsAcrossProtectors",async()=>{var p=Temp();var b=BackupPath(p);PeerProfile original;var c=Contact();await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector(11))){await w.SetNicknameAsync("🕊 Егор");await w.AddContactAsync(c);await w.SetAliasAsync(c.PeerId,"Друг");await w.SaveDraftAsync(c.PeerId,"not in backup");original=await w.ReadProfileAsync();await w.ExportBackupAsync(b,Password);}var q=Temp();await using(var w=await PeerWorkspace.OpenAsync(q,new TestProtector(77))){using var preview=await w.PreviewBackupAsync(b,Password);Check(preview.Profile.PeerId==original.PeerId&&preview.ContactCount==1);var result=await w.RestoreBackupAsync(preview,true);Check(result.PeerId==original.PeerId);Check((await w.ListContactsAsync()).Single().Alias=="Друг");Check(await w.GetDraftAsync(c.PeerId)=="");}await using var w2=await PeerWorkspace.OpenAsync(q,new TestProtector(77));Check((await w2.ReadProfileAsync()).PeerId==original.PeerId);}));
 Tests.Add(("backup","WrongPasswordTamperTruncateNoMutation",async()=>{var p=Temp();var b=BackupPath(p);await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector());var id=(await w.ReadProfileAsync()).PeerId;await w.ExportBackupAsync(b,Password);await Throws(()=>w.PreviewBackupAsync(b,"wrong password"));var bytes=File.ReadAllBytes(b);var t=b+".tamper";bytes[^1]^=1;File.WriteAllBytes(t,bytes);await Throws(()=>w.PreviewBackupAsync(t,Password));File.WriteAllBytes(t,bytes[..^1]);await Throws(()=>w.PreviewBackupAsync(t,Password));Check((await w.ReadProfileAsync()).PeerId==id);}));
 Tests.Add(("backup","HeaderBombRejectedBeforeKdf",async()=>{var p=Temp();await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector());var b=BackupPath(p);await w.ExportBackupAsync(b,Password);var bytes=File.ReadAllBytes(b);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12,4),uint.MaxValue);File.WriteAllBytes(b+".bad",bytes);await Throws(()=>w.PreviewBackupAsync(b+".bad",Password));bytes=File.ReadAllBytes(b);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16,4),uint.MaxValue);File.WriteAllBytes(b+".bad",bytes);await Throws(()=>w.PreviewBackupAsync(b+".bad",Password));}));
 Tests.Add(("backup","ExistingFileNotOverwritten",async()=>{var p=Temp();var b=BackupPath(p);await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector());File.WriteAllText(b,"keep");await Throws(()=>w.ExportBackupAsync(b,Password));Check(File.ReadAllText(b)=="keep");}));
 Tests.Add(("backup","ReplaceNeedsConfirmation",async()=>{var p=Temp();await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector());var id=(await w.ReadProfileAsync()).PeerId;var b=BackupPath(p);await w.ExportBackupAsync(b,Password);using var preview=await w.PreviewBackupAsync(b,Password);await Throws(()=>w.RestoreBackupAsync(preview,false));Check((await w.ReadProfileAsync()).PeerId==id);}));
 Tests.Add(("backup","RestoreWriteFailureRollsBack",RestoreWriteFailureRollsBack));
 Tests.Add(("backup","CrashJournalRecoversEverySwapBoundary",CrashJournalRecoversEverySwapBoundary));
 Tests.Add(("backup","RollbackCanRestartAfterEveryRecoveryMutation",async()=>{
  for(int boundary=1;boundary<=4;boundary++) {
   var p=Temp();string old;await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector())){old=(await w.ReadProfileAsync()).PeerId;}
   var parent=Path.GetDirectoryName(p)!;var backupName=".p2p-chat.backup-"+Guid.NewGuid().ToString("N");var stageName=".p2p-chat.restore-"+Guid.NewGuid().ToString("N");
   Directory.Move(p,Path.Combine(parent,backupName));await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector())){}
   PeerFiles.Save(PeerProfileRestore.JournalPath(p),new RestoreJournal(1,stageName,backupName,"NewMoved"));
   await Throws(()=>{PeerProfileRestore.Recover(p,new AfterFaultFiles(boundary));return Task.CompletedTask;});
   await using var recovered=await PeerWorkspace.OpenAsync(p,new TestProtector());Check((await recovered.ReadProfileAsync()).PeerId==old,$"Lost original after rollback mutation {boundary}");
  }
 }));
 Tests.Add(("backup","InvalidExportPasswordDoesNotDecryptPrivateIdentity",async()=>{var p=Temp();var protector=new CountingProtector();await using var w=await PeerWorkspace.OpenAsync(p,protector);protector.Reads=0;foreach(var password in new[]{"short",new string('x',257),"abcdefghij\uD800"})await Throws(()=>w.ExportBackupAsync(BackupPath(p),password));Check(protector.Reads==0,"Invalid password caused unnecessary private-key decryption");}));
 Tests.Add(("backup","PreviewExpiryActivelyClearsPrivateSeed",()=>{var seed=Enumerable.Repeat((byte)91,32).ToArray();var key=PeerSigning.PublicKey(seed);var snapshot=new BackupSnapshot(1,seed,new(PeerIdCodec.FromPublicKey(key),key,"Time"),[]);var clock=new ManualClock();using var preview=new PeerBackupPreview(snapshot,clock);clock.Fire();Check(seed.All(b=>b==0));return Throws(()=>{preview.Use(_=>true);return Task.CompletedTask;});}));
 Tests.Add(("backup","CorruptProfileCanRestoreConfirmedBackupWithoutRegenerating",async()=>{var p=Temp();var b=BackupPath(p);string id;await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector())){await w.SetNicknameAsync("Recovery");id=(await w.ReadProfileAsync()).PeerId;await w.ExportBackupAsync(b,Password);}File.WriteAllText(Path.Combine(p,"identity.json"),"corrupt preserved");using var preview=new PeerBackupPreview(PeerBackupCodec.Decrypt(File.ReadAllBytes(b),Password,default));await Throws(()=>PeerWorkspace.RestoreOfflineAsync(p,new TestProtector(),preview,false));Check(File.ReadAllText(Path.Combine(p,"identity.json"))=="corrupt preserved");await using var restored=await PeerWorkspace.RestoreOfflineAsync(p,new TestProtector(),preview,true);Check((await restored.ReadProfileAsync()).PeerId==id);Check(Directory.GetDirectories(Path.GetDirectoryName(p)!,".p2p-chat.backup-*").Any(d=>File.ReadAllText(Path.Combine(d,"identity.json"))=="corrupt preserved"));}));
 Tests.Add(("backup","CommittedRestoreSurvivesJournalCleanupFailure",async()=>{var source=Temp();var b=BackupPath(source);string sourceId;await using(var s=await PeerWorkspace.OpenAsync(source,new TestProtector())){await s.SetNicknameAsync("Source");sourceId=(await s.ReadProfileAsync()).PeerId;await s.ExportBackupAsync(b,Password);}var p=Temp();await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector(),new FaultFiles(7,true))){using var preview=await w.PreviewBackupAsync(b,Password);await Throws(()=>w.RestoreBackupAsync(preview,true));}await using var recovered=await PeerWorkspace.OpenAsync(p,new TestProtector());Check((await recovered.ReadProfileAsync()).PeerId==sourceId);}));
 Tests.Add(("backup","ReadAndDraftCannotRaceRestore",async()=>{var p=Temp();using var files=new BlockingFiles();await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector(),files);var c=Contact();await w.AddContactAsync(c);var b=BackupPath(p);await w.ExportBackupAsync(b,Password);using var preview=await w.PreviewBackupAsync(b,Password);var restore=w.RestoreBackupAsync(preview,true);await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var read=w.ReadProfileAsync();var save=w.SaveDraftAsync(c.PeerId,"after");try {Check(!read.IsCompleted&&!save.IsCompleted,"Operations bypassed restore gate");}finally {files.Release.Set();}await Task.WhenAll(restore,read,save);Check(await w.GetDraftAsync(c.PeerId)=="after");}));
 }
 static async Task RestoreWriteFailureRollsBack(){
  var source=Temp();var b=BackupPath(source);await using(var s=await PeerWorkspace.OpenAsync(source,new TestProtector())){await s.SetNicknameAsync("Source");await s.ExportBackupAsync(b,Password);}
  var p=Temp();await using var w=await PeerWorkspace.OpenAsync(p,new TestProtector(),new FaultFiles(4,false));var old=(await w.ReadProfileAsync()).PeerId;using var preview=await w.PreviewBackupAsync(b,Password);await Throws(()=>w.RestoreBackupAsync(preview,true));Check((await w.ReadProfileAsync()).PeerId==old);
 }
 static async Task CrashJournalRecoversEverySwapBoundary(){
  var source=Temp();var b=BackupPath(source);await using(var s=await PeerWorkspace.OpenAsync(source,new TestProtector())){await s.SetNicknameAsync("Source");await s.ExportBackupAsync(b,Password);}
  for(int boundary=1;boundary<=6;boundary++){
   var p=Temp();string old;await using(var w=await PeerWorkspace.OpenAsync(p,new TestProtector(),new FaultFiles(boundary,true))){old=(await w.ReadProfileAsync()).PeerId;using var preview=await w.PreviewBackupAsync(b,Password);await Throws(()=>w.RestoreBackupAsync(preview,true));}
   await using var recovered=await PeerWorkspace.OpenAsync(p,new TestProtector());Check((await recovered.ReadProfileAsync()).PeerId==old,$"Lost original at boundary {boundary}");
  }
 }
 sealed class CountingProtector:IIdentityProtector {
  readonly TestProtector _real=new();public int Reads;
  public byte[] Protect(byte[] b)=>_real.Protect(b);public byte[] Unprotect(byte[] b){Reads++;return _real.Unprotect(b);}
 }
 sealed class AfterFaultFiles(int boundary):IRestoreFileSystem {
  readonly RestoreFileSystem _real=new();int _calls;
  void After(){if(++_calls==boundary)throw new IOException("Interruption after durable mutation");}
  public void Write(string p,byte[] b){_real.Write(p,b);After();}public void Move(string a,string b){_real.Move(a,b);After();}public void Delete(string p){_real.Delete(p);After();}
 }
 sealed class ManualClock:TimeProvider {
  TimerCallback? _callback;object? _state;
  public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period){_callback=callback;_state=state;return new ManualTimer();}
  public void Fire(){if(_callback==null)throw new Exception("Preview created no expiry timer");_callback(_state);}
  sealed class ManualTimer:ITimer {public bool Change(TimeSpan d,TimeSpan p)=>true;public void Dispose(){}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
 }
 sealed class BlockingFiles:IRestoreFileSystem,IDisposable {
  readonly RestoreFileSystem _real=new();public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);public readonly ManualResetEventSlim Release=new(false);bool _first=true;
  public void Write(string p,byte[] b){if(_first){_first=false;Entered.SetResult();if(!Release.Wait(TimeSpan.FromSeconds(15)))throw new IOException("Barrier timed out");}_real.Write(p,b);}
  public void Move(string a,string b)=>_real.Move(a,b);public void Delete(string p)=>_real.Delete(p);public void Dispose()=>Release.Dispose();
 }
 sealed class FaultFiles(int boundary,bool persistent):IRestoreFileSystem {
  readonly RestoreFileSystem _real=new();int _calls;
  void Before(){_calls++;if(_calls==boundary||persistent&&_calls>boundary)throw new IOException("Injected file-system failure");}
  public void Write(string p,byte[] b){Before();_real.Write(p,b);}
  public void Move(string a,string b){Before();_real.Move(a,b);}
  public void Delete(string p){Before();_real.Delete(p);}
 }
}
