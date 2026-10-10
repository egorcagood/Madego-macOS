using Madego.Broker;
using System.Text;
using System.Text.Json;
static class AppChecks
{
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetFullPath("work"),"app-check-"+Guid.NewGuid().ToString("N"));var paths=MacPaths.Resolve(root);var keys=new TestKeys();using var life=new CancellationTokenSource();
        await using var peers=new PeerService(paths,keys,"not-started",new IpcServer(),life.Token);var app=new AppService(paths,keys,peers);int count=0;
        async Task<JsonElement> Snapshot(){return JsonSerializer.SerializeToElement(await app.SnapshotAsync(life.Token),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}).GetProperty("app");}
        async Task<object> Call(string method,object p,CancellationToken token=default)=>await app.DispatchAsync(new(Guid.NewGuid().ToString("N"),method,JsonSerializer.SerializeToElement(p)),token);
        void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;}
        Check((await Snapshot()).GetProperty("mode").GetString()=="FullVpn","Mac default Full VPN");
        const string id="11111111-1111-4111-8111-111111111111";
        var uri=$"vless://{id}@example.invalid:443?security=reality&pbk=PUBLIC&type=tcp#Fixture";
        await Call("import",new{input=uri,name="Fixture"});var snapshot=await Snapshot();
        Check(snapshot.GetProperty("nodes").GetArrayLength()==1,"Mac importer state owner");
        var server=snapshot.GetProperty("selectedId").GetString();Check(server is not null,"first import selects server");
        Check(!snapshot.GetRawText().Contains(id),"UI DTO excludes server credentials");
        var secure=Path.Combine(root,"subscriptions.secure");var before=await File.ReadAllBytesAsync(secure);
        Check(!Encoding.UTF8.GetString(before).Contains(id),"subscription cache encrypted at rest");
        Check(!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(secure)&(UnixFileMode.GroupRead|UnixFileMode.OtherRead))==0,"private file modes");
        await Call("settings",new{language="en",mode="RuMode",refreshMinutes=0});
        var restored=new AppService(paths,keys,peers);var r=JsonSerializer.SerializeToElement(await restored.SnapshotAsync(life.Token),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}).GetProperty("app");
        Check(r.GetProperty("selectedId").GetString()==server&&r.GetProperty("mode").GetString()=="RuMode"&&r.GetProperty("language").GetString()=="en","restart restores settings and selected server");
        var stopped=new CancellationToken(true);bool cancelled=false;try{await Call("import",new{input=uri,name="Cancelled"},stopped);}catch(OperationCanceledException){cancelled=true;}
        Check(cancelled&&(await File.ReadAllBytesAsync(secure)).SequenceEqual(before),"cancel cannot mutate config cache");
        bool unavailable=false;try{await Call("vpn.connect",new{});}catch(BrokerFault e){unavailable=e.Code=="vpn_not_ready";}
        Check(unavailable&&(await Snapshot()).GetProperty("connection").GetProperty("status").GetString()=="Unavailable","no fabricated Connected");
        keys.Missing=true;bool closed=false;var unavailableSnapshot=JsonSerializer.SerializeToElement(await new AppService(paths,keys,peers).SnapshotAsync(life.Token),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});closed=unavailableSnapshot.GetProperty("app").GetProperty("attention").GetString()=="keychain_unavailable";
        Check(closed&&!keys.LastMayCreate&&(await File.ReadAllBytesAsync(secure)).SequenceEqual(before),"missing key never resets subscription cache");
        Console.WriteLine($"MAC_APP_STATE_CHECKS_PASS={count}");
    }
}
