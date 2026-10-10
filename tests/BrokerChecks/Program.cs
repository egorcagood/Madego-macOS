using Madego.Broker;
using Madyar.PeerChat;
using System.Security.Cryptography;
if(args.Length==2&&args[0]=="--review"){if(args[1]=="http")await DownloadChecks.RunAsync();else await ReviewChecks.RunAsync(args[1]);return;}
PrivateFiles.SetProcessMask();
int checks=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
void Reject(Action action,string name){try{action();}catch(CryptographicException){checks++;return;}throw new Exception(name);}
var keys=new TestKeys();var protector=new WrappingProtector(keys);var seed=RandomNumberGenerator.GetBytes(32);
var a=protector.Protect(seed);var b=protector.Protect(seed);Check(!a.SequenceEqual(b),"fresh nonce");Check(protector.Unprotect(a).SequenceEqual(seed),"envelope roundtrip");
a[^1]^=1;Reject(()=>protector.Unprotect(a),"tamper rejected");
Reject(()=>new WrappingProtector(keys,"subscriptions").Unprotect(b),"purpose bound AAD");
keys.Missing=true;Reject(()=>protector.Unprotect(b),"missing key fails closed");Check(!keys.LastMayCreate,"unprotect must never create");keys.Missing=false;
var root=Path.Combine(Path.GetFullPath("work"),"broker-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var peerDir=Path.Combine(root,"peer");string peer;
await using(var first=await PeerWorkspace.OpenAsync(peerDir,protector)){peer=(await first.ReadProfileAsync()).PeerId;Check(!await first.ReadLinkEnabledAsync(),"optin defaults off");}
await using(var second=await PeerWorkspace.OpenAsync(peerDir,protector)){Check((await second.ReadProfileAsync()).PeerId==peer,"identity restored with native SQLite");await second.SetNicknameAsync("Fixture");var invite=await second.CreateInviteAsync();Check(PeerInviteCodec.Parse(invite.Uri).PeerId==peer,"Windows invite codec unchanged");}
Check(File.Exists(Path.Combine(peerDir,"contacts.db")),"native SQLite used");
Console.WriteLine($"BROKER_PLATFORM_CHECKS_PASS={checks}");
await IpcChecks.RunAsync();
await AppChecks.RunAsync();
await ReviewChecks.RunAsync();
await DownloadChecks.RunAsync();
await RefreshChecks.RunAsync();
if(args.Length==2&&args[0]=="--native")NativeChecks.Run(args[1]);
sealed class TestKeys : IWrappingKeyStore {
 byte[] key=RandomNumberGenerator.GetBytes(32);public bool Missing;public bool LastMayCreate;
 public byte[] Read(bool mayCreate){LastMayCreate=mayCreate;if(Missing)throw new CryptographicException("missing");return key.ToArray();}
}
