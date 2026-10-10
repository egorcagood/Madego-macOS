using Madego.Broker;
using System.Net;
using System.Text;
static class DownloadChecks
{
    public static async Task RunAsync()
    {
        using var watchdog=new CancellationTokenSource(TimeSpan.FromSeconds(2));bool bounded=false;
        try{await new SubscriptionDownloader("fixture-installation",()=>new Handler(true),TimeSpan.FromMilliseconds(40)).DownloadAsync("https://fixture.invalid/sub",null,watchdog.Token);}
        catch(BrokerFault e){bounded=e.Code=="subscription_failed"&&!watchdog.IsCancellationRequested;}
        catch(OperationCanceledException){}
        if(!bounded)throw new Exception("response body deadline before caller lifetime cancellation");
        var group=await new SubscriptionDownloader("fixture-installation",()=>new Handler(false)).DownloadAsync("https://fixture.invalid/sub",null,default);
        if(group.CachedNodes.Count!=1)throw new Exception("fixture HTTP body parsed");
        Console.WriteLine("REVIEW_HTTP_BODY_DEADLINE_PASS");
    }
    sealed class Handler(bool stall):HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            if(request.Headers.GetValues("x-device-os").Single()!="macOS"||request.Headers.GetValues("x-hwid").Single()!="fixture-installation")throw new Exception("Mac HWID headers");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=stall?new StreamContent(new Stall()):new StringContent("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?type=tcp#Fixture",Encoding.UTF8)});
        }
    }
    sealed class Stall:Stream {
        public override async ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default){await Task.Delay(Timeout.Infinite,ct);return 0;}
        public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override bool CanRead=>true;public override bool CanWrite=>false;public override bool CanSeek=>false;public override long Length=>throw new NotSupportedException();public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
}
