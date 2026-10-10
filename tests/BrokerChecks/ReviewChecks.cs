using Madego.Broker;
using Madego.Core;
using Madyar.PeerChat;
using System.Security.Cryptography;
using System.Text.Json;
static class ReviewChecks
{
    public static async Task RunAsync(string test="all")
    {
        if(test is "all" or "store") {
            var root=Path.Combine(Path.GetFullPath("work"),"review-store-"+Guid.NewGuid().ToString("N"));PrivateFiles.Directory(root);var path=Path.Combine(root,"state.secure");
            var store=new SecretJsonStore<Payload>(path,new(new TestKeys(),"store"));await store.SaveAsync(new(){Text="prior"},default);var before=await File.ReadAllBytesAsync(path);bool rejected=false;
            try{await store.SaveAsync(new(){Text=new string('x',16*1024*1024)},default);}catch(InvalidDataException){rejected=true;}
            if(!rejected||!(await File.ReadAllBytesAsync(path)).SequenceEqual(before)||(await store.LoadAsync(default)).Text!="prior")throw new Exception("oversized save preserves readable old store");
            Console.WriteLine("REVIEW_STORE_PASS");
        }
        if(test is "all" or "metadata") {
            var config=new{remarks=new string('a',2*1024*1024),outbounds=new[]{new{protocol="vless",settings=new{address="example.invalid",port=443,id="11111111-1111-4111-8111-111111111111"}}}};
            var parsed=SubscriptionParser.Parse(JsonSerializer.Serialize(config));if(parsed.Count!=1||parsed[0].Name.Length>160)throw new Exception("display metadata bound");
            var invalid="{\"outbounds\":[{\"protocol\":\"vless\",\"settings\":{\"address\":\"example.invalid\",\"port\":1.5,\"id\":\"11111111-1111-4111-8111-111111111111\"}}]}";
            if(SubscriptionParser.Parse(invalid).Count!=0)throw new Exception("explicit invalid JSON port cannot default 443");
            Console.WriteLine("REVIEW_METADATA_PASS");
        }
        if(test is "all" or "recovery") {
            var root=Path.Combine(Path.GetFullPath("work"),"review-recovery-"+Guid.NewGuid().ToString("N"));var paths=MacPaths.Resolve(root);var keys=new RecoverableKeys();string peer;
            var backup=Path.Combine(root,"profile.backup");
            await using(var w=await PeerWorkspace.OpenAsync(paths.Peer,new WrappingProtector(keys))){await w.SetNicknameAsync("Fixture");peer=(await w.ReadProfileAsync()).PeerId;await w.ExportBackupAsync(backup,"fixture-password-only");}
            var opaque=Path.Combine(root,"subscriptions.secure");var bytes=RandomNumberGenerator.GetBytes(80);await File.WriteAllBytesAsync(opaque,bytes);keys.Missing=true;
            using var life=new CancellationTokenSource(TimeSpan.FromSeconds(15));await using var service=new PeerService(paths,keys,"not-started",new IpcServer(),life.Token);
            async Task<object> Call(string method,object p)=>await service.DispatchAsync(new(Guid.NewGuid().ToString("N"),method,JsonSerializer.SerializeToElement(p)),life.Token);
            var preview=JsonSerializer.SerializeToElement(await Call("backup.preview",new{path=backup,password="fixture-password-only"}));var id=preview.GetProperty("id").GetString();
            keys.Block=true;var restore=Call("backup.restore",new{id,confirmed=true});
            if(!await Task.Run(()=>keys.Entered.Wait(TimeSpan.FromSeconds(4))))throw new Exception("confirmed recovery reached protected rewrap");
            var cancel=Call("backup.cancel",new{});keys.Release.Set();await restore;await cancel;
            var snapshot=JsonSerializer.SerializeToElement(await service.SnapshotAsync(default));if(snapshot.GetProperty("peerId").GetString()!=peer||snapshot.GetProperty("enabled").GetBoolean())throw new Exception("backup restores same PeerID offline");
            if(!(await File.ReadAllBytesAsync(opaque)).SequenceEqual(bytes))throw new Exception("unavailable subscriptions preserved");
            Console.WriteLine("REVIEW_RECOVERY_CANCEL_PASS");
        }
    }
    public sealed class Payload {public string Text {get;set;}="";}
    sealed class RecoverableKeys:IWrappingKeyStore {
        byte[] key=RandomNumberGenerator.GetBytes(32);public bool Missing;public bool Block;public ManualResetEventSlim Entered=new(),Release=new();
        public byte[] Read(bool create){if(Missing)throw new CryptographicException();return key.ToArray();}
        // Called only by the explicitly confirmed broker recovery capability.
        public IWrappingKeyStore ConfirmedRecovery(){Missing=false;key=RandomNumberGenerator.GetBytes(32);return new Recovery(this);}
        sealed class Recovery(RecoverableKeys owner):IWrappingKeyStore {public byte[] Read(bool create){if(owner.Block){owner.Entered.Set();owner.Release.Wait(TimeSpan.FromSeconds(5));}return owner.key.ToArray();}}
    }
}
