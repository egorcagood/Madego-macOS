using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
namespace Madego.Broker;

public sealed record IpcRequest(string Id,string Method,JsonElement Params);
public sealed class BrokerFault(string code):Exception { public string Code {get;}=code; }

// At most eight regular requests; control requests never queue behind a 45s message ACK.
public sealed class IpcServer
{
    public const int MaxFrame=128*1024;
    readonly SemaphoreSlim _write=new(1,1);
    readonly ConcurrentDictionary<string,CancellationTokenSource> _pending=new();
    static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=32};
    Stream? _output;
    public async Task EventAsync(string name,object data,CancellationToken ct)
    {
        if(_output is null)return;
        // Coalesce stale status notifications: do not start unbounded writer tasks.
        if(!await _write.WaitAsync(0,ct))return;
        try {await WriteUnlockedAsync(new { @event=name,data },ct);}finally{_write.Release();}
    }
    public async Task RunAsync(Stream input,Stream output,Func<IpcRequest,CancellationToken,Task<object>> dispatch,CancellationToken ct)
    {
        _output=output;using var life=CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reader=new StreamReader(input,new UTF8Encoding(false,true),false,4096,true);
        var jobs=new ConcurrentDictionary<string,Task>();int regular=0,control=0;
        try {
            while(!life.IsCancellationRequested) {
                var line=await ReadBoundedAsync(reader,life.Token);if(line is null)break;
                IpcRequest request;
                try {
                    using var doc=JsonDocument.Parse(line,new JsonDocumentOptions{MaxDepth=32});var root=doc.RootElement;
                    var id=root.GetProperty("id").GetString()??"";var method=root.GetProperty("method").GetString()??"";
                    if(id.Length is <1 or >64||!id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')||method.Length is <1 or >64)throw new JsonException();
                    var parameters=root.TryGetProperty("params",out var p)?p.Clone():JsonSerializer.SerializeToElement(new{});
                    if(parameters.ValueKind!=JsonValueKind.Object)throw new JsonException();request=new(id,method,parameters);
                } catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException) {
                    await ReplyAsync(new{id=(string?)null,error=new{code="invalid_request"}},life.Token);continue;
                }
                if(request.Method=="cancel") {
                    if(request.Params.TryGetProperty("id",out var target)&&target.ValueKind==JsonValueKind.String&&_pending.TryGetValue(target.GetString()??"",out var cancel))cancel.Cancel();
                    await ReplyAsync(new{id=request.Id,result=new{cancelled=true}},life.Token);continue;
                }
                bool urgent=request.Method=="chat.disable";
                if((urgent?control:regular)>=(urgent?2:8)) {await ErrorAsync(request.Id,"busy",life.Token);continue;}
                var token=CancellationTokenSource.CreateLinkedTokenSource(life.Token);token.CancelAfter(TimeSpan.FromSeconds(120));
                if(!_pending.TryAdd(request.Id,token)){token.Dispose();await ErrorAsync(request.Id,"duplicate_id",life.Token);continue;}
                if(urgent)Interlocked.Increment(ref control);else Interlocked.Increment(ref regular);
                // Task.Run is needed: platform/SQLite/Keychain calls may block synchronously before first await.
                var task=Task.Run(async()=> {
                    try {
                        var result=await dispatch(request,token.Token);
                        await ReplyAsync(new{id=request.Id,result},life.Token);
                    } catch(Exception ex) {
                        var code=ex switch {BrokerFault f=>f.Code,OperationCanceledException=>"cancelled",System.Security.Cryptography.CryptographicException=>"keychain_unavailable",ArgumentException or InvalidDataException=>"invalid_input",_=>"operation_failed"};
                        if(!life.IsCancellationRequested)await ErrorAsync(request.Id,code,life.Token);
                    } finally {
                        _pending.TryRemove(request.Id,out _);token.Dispose();if(urgent)Interlocked.Decrement(ref control);else Interlocked.Decrement(ref regular);
                    }
                },CancellationToken.None);
                jobs[request.Id]=task;
                foreach(var pair in jobs)if(pair.Value.IsCompleted)jobs.TryRemove(pair.Key,out _);
            }
        } finally {
            life.Cancel();foreach(var request in _pending.Values)request.Cancel();
            try {await Task.WhenAll(jobs.Values).WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){}catch(TimeoutException){}
        }
    }
    Task ErrorAsync(string id,string code,CancellationToken ct)=>ReplyAsync(new{id,error=new{code}},ct);
    async Task ReplyAsync(object value,CancellationToken ct)
    {
        await _write.WaitAsync(ct);try{await WriteUnlockedAsync(value,ct);}finally{_write.Release();}
    }
    async Task WriteUnlockedAsync(object value,CancellationToken ct)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,Json);
        if(bytes.Length>2*1024*1024)throw new BrokerFault("response_too_large");
        await _output!.WriteAsync(bytes,ct);await _output.WriteAsync(new byte[]{10},ct);await _output.FlushAsync(ct);
    }
    static async Task<string?> ReadBoundedAsync(StreamReader reader,CancellationToken ct)
    {
        var line=new StringBuilder();var one=new char[1];int bytes=0;
        while(await reader.ReadAsync(one.AsMemory(),ct)!=0) {
            if(one[0]=='\n')return line.ToString().TrimEnd('\r');
            bytes+=one[0]<=0x7f?1:one[0]<=0x7ff?2:3;
            if(bytes>MaxFrame)throw new InvalidDataException("Frame exceeds bound.");line.Append(one[0]);
        }
        return line.Length==0?null:throw new InvalidDataException("Incomplete frame.");
    }
}
