using Madyar.PeerChat;
namespace PeerChatChecks;
internal static partial class Program {
 static readonly List<(string Group,string Name,Func<Task> Run)> Tests=[];
 static void Check(bool b,string why="Assertion failed") { if(!b) throw new Exception(why); }
 static async Task Throws(Func<Task> action) { try { await action(); } catch(Exception e) when(e is not TestFailure && e is not NotImplementedException) { return; } throw new TestFailure("Expected rejection"); }
 sealed class TestFailure(string message):Exception(message);
 static string Temp() { var p=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"madyar-peer-tests",Guid.NewGuid().ToString("N"),"p2p-chat"); Directory.CreateDirectory(Path.GetDirectoryName(p)!);return p; }
 static async Task Main(string[] args) {
  IdentityTests(); ContactTests(); InviteTests(); BackupTests(); ActivationTests(); SetupProfileTests(); ContactManagementTests();
  var group=args.Length==2&&args[0]=="--group"?args[1]:null;int failed=0,passed=0;
  foreach(var t in Tests.Where(t=>group==null||t.Group==group)) try { await t.Run(); Console.WriteLine("PASS "+t.Name);passed++; } catch(Exception e) { Console.WriteLine("FAIL "+t.Name+": "+e.Message);failed++; }
  Console.WriteLine($"RESULT {passed} passed, {failed} failed");Environment.ExitCode=failed==0&&passed>0?0:1;
 }
}
internal sealed class TestProtector(byte mask=0x55):IIdentityProtector {
 public byte[] Protect(byte[] bytes)=>bytes.Select(b=>(byte)(b^mask)).ToArray();
 public byte[] Unprotect(byte[] bytes)=>Protect(bytes);
}
