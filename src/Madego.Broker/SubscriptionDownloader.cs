using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EgorVpn.Models;
using Madego.Core;
namespace Madego.Broker;

public sealed class SubscriptionDownloader(string installationId,Func<HttpMessageHandler>? handlerFactory=null,TimeSpan? profileTimeout=null)
{
    static readonly string[] Profiles=["MadyarVPN/0.4.1","EgorMadyarVPN/0.2.2","Happ/4.3.0","v2rayN/7.0.0"];
    public async Task<SavedSubscription> DownloadAsync(string url,string? title,CancellationToken ct)
    {
        _=SubscriptionRequest.Parse(title,url,true);
        foreach(var profile in Profiles) {
            ct.ThrowIfCancellationRequested();
            try {
                using var handler=handlerFactory?.Invoke()??new SocketsHttpHandler{AutomaticDecompression=DecompressionMethods.All,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(10)};
                var timeout=profileTimeout??TimeSpan.FromSeconds(25);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout);var attempt=deadline.Token;
                using var client=new HttpClient(handler){Timeout=timeout};var current=new Uri(url);
                for(int redirects=0;redirects<=5;redirects++) {
                    using var request=new HttpRequestMessage(HttpMethod.Get,current);
                    request.Headers.UserAgent.ParseAdd(profile);request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                    request.Headers.Add("x-hwid",installationId);request.Headers.Add("x-device-os","macOS");request.Headers.Add("x-device-model","Mac");request.Headers.Add("x-ver-os",Environment.OSVersion.Version.ToString());
                    using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,attempt);
                    if((int)response.StatusCode is >=300 and <=399) {
                        if(response.Headers.Location is null)throw new BrokerFault("subscription_failed");
                        var next=new Uri(current,response.Headers.Location);
                        _=SubscriptionRequest.Parse(null,next.AbsoluteUri,true);
                        if(current.Scheme=="https"&&next.Scheme!="https")throw new BrokerFault("insecure_redirect");
                        current=next;continue;
                    }
                    if(HeaderTrue(response,"x-hwid-max-devices-reached")||HeaderTrue(response,"x-hwid-limit"))throw new BrokerFault("device_limit");
                    response.EnsureSuccessStatusCode();
                    await using var stream=await response.Content.ReadAsStreamAsync(attempt);using var bytes=new MemoryStream();var buffer=new byte[8192];int count;
                    while((count=await stream.ReadAsync(buffer,attempt))!=0){if(bytes.Length+count>8*1024*1024)throw new BrokerFault("subscription_too_large");await bytes.WriteAsync(buffer.AsMemory(0,count),attempt);}
                    var text=Encoding.UTF8.GetString(bytes.ToArray());var nodes=SubscriptionParser.Parse(text).ToList();
                    if(nodes.Count==0||text.Contains("application is not supported",StringComparison.OrdinalIgnoreCase)||text.Contains("Приложение не поддерживается",StringComparison.OrdinalIgnoreCase))break;
                    if(nodes.Count>2000)throw new BrokerFault("too_many_servers");
                    var name=title??Header(response,"profile-title")??current.Host;if(name.StartsWith("base64:",StringComparison.OrdinalIgnoreCase))try{name=Encoding.UTF8.GetString(Convert.FromBase64String(name[7..]));}catch(FormatException){}
                    name=Uri.UnescapeDataString(name);if(name.Length>100||name.Any(char.IsControl))name=current.Host;
                    var group=new SavedSubscription{Name=name,Url=url,CachedNodes=nodes,UpdatedAt=DateTimeOffset.UtcNow};
                    ApplyUsage(Header(response,"subscription-userinfo"),group);return group;
                }
            } catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(BrokerFault){throw;}
            catch(Exception ex) when(ex is HttpRequestException or IOException or OperationCanceledException or ArgumentException) { /* Do not leak URLs/provider error bodies. */ }
        }
        throw new BrokerFault("subscription_failed");
    }
    static string? Header(HttpResponseMessage r,string key)=>r.Headers.TryGetValues(key,out var values)?values.FirstOrDefault():null;
    static bool HeaderTrue(HttpResponseMessage r,string key)=>Header(r,key) is "true" or "1";
    static void ApplyUsage(string? text,SavedSubscription group)
    {
        long upload=0,download=0;
        foreach(var entry in (text??"").Split(';')) {
            var pair=entry.Trim().Split('=',2);if(pair.Length!=2||!long.TryParse(pair[1],out var value)||value<0)continue;
            switch(pair[0]) {case "upload":upload=value;break;case "download":download=value;break;case "total":group.TrafficTotalBytes=value;break;case "expire":if(value is >0 and <=253402300799)group.ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(value);break;}
        }
        group.TrafficUsedBytes=upload>long.MaxValue-download?long.MaxValue:upload+download;
    }
}
