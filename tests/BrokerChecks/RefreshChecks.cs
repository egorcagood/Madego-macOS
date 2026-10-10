using Madego.Broker;
using System.Net;
using System.Text.Json;
static class RefreshChecks
{
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetFullPath("work"),"refresh-check-"+Guid.NewGuid().ToString("N"));var paths=MacPaths.Resolve(root);var keys=new TestKeys();using var life=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        bool slow=false;var entered=new TaskCompletionSource();var release=new TaskCompletionSource();
        await using var peers=new PeerService(paths,keys,"unused",new IpcServer(),life.Token);
        var app=new AppService(paths,keys,peers,id=>new(id,()=>new Fixture(slow,entered,release),TimeSpan.FromSeconds(3)));
        async Task<object> Call(string method,object p)=>await app.DispatchAsync(new(Guid.NewGuid().ToString("N"),method,JsonSerializer.SerializeToElement(p)),life.Token);
        await Call("import",new{input="https://fixture.invalid/sub",name="Fixture"});
        var state=JsonSerializer.SerializeToElement(await app.SnapshotAsync(life.Token));var group=state.GetProperty("app").GetProperty("groups")[0].GetProperty("id").GetString();
        slow=true;var refresh=Call("subscription.refresh",new{id=group});await entered.Task.WaitAsync(life.Token);
        _=await app.SnapshotAsync(life.Token).WaitAsync(TimeSpan.FromMilliseconds(300));
        await Call("subscription.remove",new{id=group}).WaitAsync(TimeSpan.FromMilliseconds(300));release.TrySetResult();await refresh;
        var after=JsonSerializer.SerializeToElement(await app.SnapshotAsync(life.Token));if(after.GetProperty("app").GetProperty("groups").GetArrayLength()!=0)throw new Exception("refresh resurrected removed subscription");
        Console.WriteLine("REVIEW_REFRESH_NONBLOCKING_NO_RESURRECTION_PASS");
    }
    sealed class Fixture(bool slow,TaskCompletionSource entered,TaskCompletionSource release):HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            if(slow){entered.TrySetResult();await release.Task.WaitAsync(ct);}
            return new(HttpStatusCode.OK){Content=new StringContent("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?type=tcp#Fixture")};
        }
    }
}
