using EgorVpn.Models;
using EgorVpn.Services;
using EgorVpn.Routing;
using System.Text.Json;
void Check(bool yes,string name){if(!yes)throw new Exception(name);Console.WriteLine("PASS "+name);}
var old=JsonSerializer.Deserialize<AppSettings>("{\"Mtu\":1400,\"DnsProvider\":\"Quad9\",\"ChatMessageFontSize\":19,\"AutoConnectOnLaunch\":true}")!;
var mode=typeof(AppSettings).GetProperty("RoutingMode");
Check(mode is not null&&Equals(mode.GetValue(old),RoutingMode.FullVpn),"Legacy settings default to FullVpn without changing MTU/DNS/chat/autoconnect");
Check(old.Mtu==1400&&old.DnsProvider=="Quad9"&&old.ChatMessageFontSize==19&&old.AutoConnectOnLaunch,"Unrelated legacy values remain intact");
foreach(var invalid in new[]{"\"FutureMode\"","17","null","{\"mode\":\"RuMode\"}","[1]"}){
 var s=JsonSerializer.Deserialize<AppSettings>("{\"Mtu\":1380,\"RoutingMode\":"+invalid+"}")!;
 Check(s.RoutingMode==RoutingMode.FullVpn&&s.Mtu==1380,"Invalid routing value fails closed without resetting unrelated settings: "+invalid);
}
var prefs=new AppSettings{RoutingMode=RoutingMode.RuMode,RuMode=new(){DisabledServiceIds=["gosuslugi","future-service"],CustomRules=[new(DomainMatch.Exact,"example.com",DomainRoute.Proxy)]}};
var cloned=prefs.Copy();cloned.RuMode.DisabledServiceIds.Clear();cloned.RuMode.CustomRules.Clear();
Check(prefs.RuMode.DisabledServiceIds.Count==2&&prefs.RuMode.CustomRules.Count==1,"AppSettings copy isolates policy collections for drafts and attempts");
var restored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(prefs))!;
Check(restored.RoutingMode==RoutingMode.RuMode&&restored.RuMode.Validate()==null&&restored.RuMode.DisabledServiceIds.SequenceEqual(prefs.RuMode.DisabledServiceIds)&&restored.RuMode.CustomRules.SequenceEqual(prefs.RuMode.CustomRules),"Routing mode and own preferences survive JSON roundtrip");
var corrupted=new AppSettings{Mtu=1400,RoutingMode=RoutingMode.RuMode,RuMode=new(){DisabledServiceIds=null!}};
Check(corrupted.RepairRoutingPreferences()&&corrupted.RoutingMode==RoutingMode.FullVpn&&corrupted.Mtu==1400,"Damaged routing preferences repair only routing and fail closed");
var directory=Path.Combine(Path.GetTempPath(),"madego-routing-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
try{
 var store=new AtomicJsonStore<AppSettings>(Path.Combine(directory,"settings.json"));await store.SaveAsync(prefs);var loaded=await store.LoadAsync();
 Check(loaded.RoutingMode==RoutingMode.RuMode&&loaded.RuMode.CustomRules.SequenceEqual(prefs.RuMode.CustomRules),"Existing atomic store persists policy without extra state store");
}finally{Directory.Delete(directory,true);}
await CheckTransactions();
async Task CheckTransactions(){
 var calls=new List<string>();var oldSettings=new AppSettings();var candidate=new AppSettings{RoutingMode=RoutingMode.RuMode};
 Task Record(string label){calls.Add(label);return Task.CompletedTask;}
 Task Validate(AppSettings s,CancellationToken ct)=>Record("validate");Task Activate(AppSettings s,CancellationToken ct)=>Record("activate-health");Task Save(AppSettings s,CancellationToken ct)=>Record("save");Task Restore(CancellationToken ct)=>Record("restore-health");
 await RoutingPolicyTransaction.ApplyAsync(oldSettings,candidate,Validate,Activate,Save,Restore);
 Check(calls.SequenceEqual(new[]{"validate","activate-health","save"}),"Candidate validates before mutation, health before persistence");
 calls.Clear();try{await RoutingPolicyTransaction.ApplyAsync(oldSettings,candidate,(s,ct)=>Task.FromException(new InvalidDataException("invalid")),Activate,Save,Restore);throw new Exception("Expected failure");}catch(InvalidDataException){}
 Check(calls.Count==0,"Invalid candidate cannot stop live connection or write settings");
 calls.Clear();try{await RoutingPolicyTransaction.ApplyAsync(oldSettings,candidate,Validate,(s,ct)=>{calls.Add("activate-health");return Task.FromException(new IOException("health failed"));},Save,Restore);throw new Exception("Expected failure");}catch(IOException){}
 Check(calls.SequenceEqual(new[]{"validate","activate-health","restore-health"}),"Failed health restores prior policy before exposing success");
 calls.Clear();try{await RoutingPolicyTransaction.ApplyAsync(oldSettings,candidate,Validate,Activate,(s,ct)=>{calls.Add("save");return Task.FromException(new IOException("disk failed"));},Restore);throw new Exception("Expected failure");}catch(IOException){}
 Check(calls.SequenceEqual(new[]{"validate","activate-health","save","restore-health"}),"Failed atomic save also restores prior active policy");
 calls.Clear();using var stop=new CancellationTokenSource();
 try{await RoutingPolicyTransaction.ApplyAsync(oldSettings,candidate,Validate,(s,ct)=>{stop.Cancel();return Task.FromCanceled(stop.Token);},Save,ct=>{Check(!ct.IsCancellationRequested,"Rollback has independent token after cancelled switch");return Restore(ct);},stop.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
 Check(calls.SequenceEqual(new[]{"validate","restore-health"}),"Cancelled activation restores policy without committing candidate");
 try{await RoutingPolicyTransaction.ApplyAsync(oldSettings,candidate,Validate,(s,ct)=>Task.FromException(new IOException("activation")),Save,ct=>Task.FromException(new IOException("rollback")));throw new Exception("Expected aggregate");}catch(RoutingPolicyRollbackException e){Check(e.InnerExceptions.Count==2,"Failed rollback retains original and restore failure for critical notice");}
}
