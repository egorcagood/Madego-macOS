using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace Madyar.PeerChat;
public sealed class PeerLinkProcess : IPeerLinkTransport, IPeerIntroductionTransport
{
    readonly string _executable, _arguments;
    readonly bool _internet;
    readonly SemaphoreSlim _write=new(1,1);
    readonly CancellationTokenSource _life=new();
    readonly TaskCompletionSource<string> _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    Process? _process; Task? _reading; bool _started; volatile bool _stopping;
    public Func<PeerLinkEvent,Task>? HandleEvent {get;set;}
    public PeerLinkProcess(string executable,bool internet=true,string arguments="") { _executable=executable;_internet=internet;_arguments=arguments; }
    public async Task StartAsync(PeerLinkCredentials identity,IReadOnlyList<PeerContact> contacts,CancellationToken ct)
    {
        if(_started||_stopping)throw new InvalidOperationException("Сетевой модуль уже запускался.");_started=true;
        ct.ThrowIfCancellationRequested();
        var p=new Process {StartInfo=new ProcessStartInfo(_executable,_arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=new UTF8Encoding(false,true),StandardErrorEncoding=Encoding.UTF8}};
        using var cancelled=ct.Register(()=>{_life.Cancel();Kill(p);});
        try {
            p.Start();_process=p;_reading=ReadEventsAsync(p);
            // No stderr content is logged: helper faults must not echo identity or a message.
            _=DrainErrorsAsync(p);
            await WriteAsync(new {op="start",seed=Convert.ToBase64String(identity.Seed),contacts=contacts.Select(c=>c.PeerId).ToArray(),internet=_internet},ct);
            var peer=await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20),ct);
            if(peer!=identity.PeerId)throw new InvalidDataException("Сетевой модуль открыл другой профиль.");
        } catch { await StopAsync();throw; }
    }
    async Task ReadEventsAsync(Process p)
    {
        try {
            while(!_life.IsCancellationRequested) {
                var line=await ReadLineBoundedAsync(p.StandardOutput,131072,_life.Token);if(line is null)break;
                using var doc=JsonDocument.Parse(line,new JsonDocumentOptions {MaxDepth=8});var e=doc.RootElement;
                string Str(string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
                bool Flag(string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
                var op=Str("op");
                if(op=="ready") {
                    Addresses=e.TryGetProperty("addresses",out var addresses)&&addresses.ValueKind==JsonValueKind.Array
                        ?addresses.EnumerateArray().Where(a=>a.ValueKind==JsonValueKind.String).Select(a=>a.GetString()!).ToArray():[];
                    _ready.TrySetResult(Str("peer"));continue;
                }
                if(op is not ("received" or "sent" or "peer" or "state" or "intro_received" or "intro_sent"))continue;
                if(HandleEvent is { } receive)await receive(new(op,Str("peer"),Str("id"),Str("kind"),Str("text"),Str("receipt"),Flag("online"),Flag("delivered"),Str("detail"),Str("invite")));
            }
            if(!_stopping)throw new IOException("Сетевой модуль завершился.");
        } catch(Exception) {
            _ready.TrySetException(new IOException("Не удалось запустить сетевой модуль чата."));
            if(!_stopping&&HandleEvent is { } receive) {
                try { await receive(new("state",Detail:"process-exited")); }catch(Exception){}
            }
        }
    }
    static async Task<string?> ReadLineBoundedAsync(StreamReader reader,int max,CancellationToken ct)
    {
        var result=new StringBuilder();var one=new char[1];
        while(await reader.ReadAsync(one.AsMemory(),ct)!=0) {
            if(one[0]=='\n')return result.ToString().TrimEnd('\r');
            if(result.Length>=max)throw new InvalidDataException("Сетевой ответ превышает лимит.");result.Append(one[0]);
        }
        return result.Length==0?null:throw new InvalidDataException("Неполный сетевой ответ.");
    }
    async Task DrainErrorsAsync(Process p) {
        try {var buffer=new char[4096];while(await p.StandardError.ReadAsync(buffer.AsMemory(),_life.Token)!=0){}}catch(Exception){}
    }
    async Task WriteAsync<T>(T value,CancellationToken ct)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,_life.Token);
        await _write.WaitAsync(cancel.Token);
        try {if(_stopping||_process is null||_process.HasExited)throw new IOException("Чат отключён.");
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(),cancel.Token);
            await _process.StandardInput.FlushAsync(cancel.Token);
        } finally {_write.Release();}
    }
    public Task UpdateContactsAsync(IReadOnlyList<PeerContact> contacts,CancellationToken ct)=>WriteAsync(new {op="contacts",contacts=contacts.Select(c=>c.PeerId).ToArray()},ct);
    public Task SendAsync(PeerMessage m,CancellationToken ct)=>WriteAsync(new {op="send",id=m.Id,to=m.PeerId,kind=m.Kind,text=m.Text},ct);
    public Task AcceptAsync(string receipt,bool accepted,CancellationToken ct)=>WriteAsync(new {op="accept",receipt,accepted},ct);
    public Task UpdateInvitesAsync(IReadOnlyList<PeerIssuedInvite> invites,CancellationToken ct)=>WriteAsync(new {op="invites",invites=invites.Select(i=>new{id=i.Id,expiresUtc=i.ExpiresUtc.ToUniversalTime().ToString("O")}).ToArray()},ct);
    public Task SendIntroductionAsync(string id,string recipient,string targetInviteId,string signedReturnUri,CancellationToken ct)=>WriteAsync(new {op="intro_send",id,to=recipient,invite=targetInviteId,text=signedReturnUri},ct);
    public IReadOnlyList<string> Addresses {get;private set;}=[];
    internal Task FixturePeerAsync(string peer,IReadOnlyList<string> addresses,CancellationToken ct) {
        if(_internet)throw new InvalidOperationException("Fixture endpoints are unavailable in internet mode.");
        return WriteAsync(new {op="fixtureaddr",peer,addresses},ct);
    }
    static void Kill(Process p){try{if(!p.HasExited)p.Kill(entireProcessTree:true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
    public async Task StopAsync() {
        _stopping=true;_life.Cancel();_ready.TrySetCanceled();var p=_process;if(p is null)return;
        try {p.StandardInput.Close();}catch(Exception){}
        try {await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){Kill(p);await p.WaitForExitAsync();}
        if(_reading is not null&&!_reading.IsCompleted) {
            // Never await our own reader callback. Cancellation makes the reader unwind independently.
        }
    }
    public async ValueTask DisposeAsync(){await StopAsync();_process?.Dispose();}
}
