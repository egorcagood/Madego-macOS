using EgorVpn.Models;
using EgorVpn.Routing;
using EgorVpn.Services;
using Madego.Core;
using System.Security.Cryptography;
using System.Text.Json;
namespace Madego.Broker;

public sealed class MacSettings
{
    public string Language {get;set;}="ru";
    public RoutingMode Mode {get;set;}=RoutingMode.FullVpn;
    public RuRoutingPreferences Ru {get;set;}=new();
    public int RefreshMinutes {get;set;}=60;
}
public sealed class MacState {public List<SavedSubscription> Subscriptions {get;set;}=[];public string? SelectedId {get;set;}}

public sealed class AppService(MacPaths paths,IWrappingKeyStore keys,PeerService peers,Func<string,SubscriptionDownloader>? downloaderFactory=null)
{
    readonly SemaphoreSlim _gate=new(1,1);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string,bool> _refreshing=new();
    SubscriptionDownloader Downloader()=>downloaderFactory?.Invoke(_install)??new SubscriptionDownloader(_install);
    readonly SecretJsonStore<MacState> _store=new(Path.Combine(paths.Support,"subscriptions.secure"),new(keys,"subscriptions"));
    MacSettings _settings=new();MacState _state=new();bool _loaded;string _install="";
    async Task LoadAsync(CancellationToken ct)
    {
        if(_loaded)return;
        _state=await _store.LoadAsync(ct);
        var settings=Path.Combine(paths.Support,"settings.json");PrivateFiles.NoLinks(settings);
        if(File.Exists(settings))_settings=JsonSerializer.Deserialize<MacSettings>(await File.ReadAllTextAsync(settings,ct))??throw new InvalidDataException();
        if(!Enum.IsDefined(_settings.Mode)||_settings.Ru?.Validate() is not null){_settings.Mode=RoutingMode.FullVpn;_settings.Ru=new();}
        if(_settings.RefreshMinutes!=0&&_settings.RefreshMinutes is <5 or >1440)_settings.RefreshMinutes=60;
        var install=Path.Combine(paths.Support,"installation.id");PrivateFiles.NoLinks(install);
        _install=File.Exists(install)?await File.ReadAllTextAsync(install,ct):Guid.NewGuid().ToString("N");
        if(!Guid.TryParse(_install,out _))throw new InvalidDataException();
        if(!File.Exists(install))await PrivateFiles.SaveAsync(install,System.Text.Encoding.ASCII.GetBytes(_install),ct);
        _loaded=true;
    }
    object StateDto(MacState? candidate=null,MacSettings? preferences=null,string? attention=null) {
        var state=candidate??_state;var settings=preferences??_settings;return new {
        attention,
        version="0.1.0-dev.1",language=settings.Language,mode=settings.Mode.ToString(),refreshMinutes=settings.RefreshMinutes,
        selectedId=state.SelectedId,connection=new{status="Unavailable",reason="provider_not_implemented"},
        groups=state.Subscriptions.Select(g=>new{id=g.Id,name=DisplayLabel(g.Name,100),local=string.IsNullOrEmpty(g.Url),count=g.CachedNodes.Count,updated=g.UpdatedAt,expires=g.ExpiresAt,usedBytes=g.TrafficUsedBytes,totalBytes=g.TrafficTotalBytes}).ToArray(),
        nodes=state.Subscriptions.SelectMany(g=>g.CachedNodes.Select(n=>new{id=n.Id,groupId=g.Id,name=DisplayLabel(n.DisplayName,160),country=n.Country,flag=n.CountryFlag,protocol=n.Protocol,transport=DisplayLabel(n.Transport,40),security=DisplayLabel(n.Security,40)})).ToArray(),
        ruServices=RoutingCatalog.LoadEmbedded().Services.Select(s=>new{id=s.Id,name=s.Name,category=s.Category,enabled=!settings.Ru.DisabledServiceIds.Contains(s.Id)}).ToArray(),
        ruRules=settings.Ru.CustomRules.Select(d=>new{value=d.Value,match=d.Match.ToString(),route=d.Route.ToString()}).ToArray()
    };}
    static string DisplayLabel(string text,int length)=>new string(text.Where(c=>!char.IsControl(c)).Take(length).ToArray());
    void CheckSnapshot(MacState state,MacSettings settings) {if(JsonSerializer.SerializeToUtf8Bytes(StateDto(state,settings)).Length>1536*1024)throw new BrokerFault("snapshot_too_large");}

    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        object state;await _gate.WaitAsync(ct);try{try{await LoadAsync(ct);state=StateDto();}catch(CryptographicException){state=StateDto(new MacState(),_settings,"keychain_unavailable");}}finally{_gate.Release();}
        return new{app=state,chat=await peers.SnapshotAsync(ct)};
    }
    public async Task<object> DispatchAsync(IpcRequest request,CancellationToken ct)
    {
        if(request.Method=="snapshot")return await SnapshotAsync(ct);
        if(request.Method.StartsWith("chat.")||request.Method.StartsWith("contact.")||request.Method.StartsWith("profile.")||request.Method.StartsWith("messages.")||request.Method.StartsWith("backup.")) {
            if(request.Method=="messages.server")throw new BrokerFault("use_server_share"); // Only known selected nodes may become outgoing server payloads.
            return await peers.DispatchAsync(request,ct);
        }
        var p=request.Params;string Get(string name)=>p.GetProperty(name).GetString()??throw new ArgumentException();
        if(request.Method=="subscription.refresh"){await RefreshAsync(Get("id"),ct);return await SnapshotAsync(ct);}
        string? share=null;await _gate.WaitAsync(ct);
        try {
            await LoadAsync(ct);
            switch(request.Method) {
                case "import":
                    var input=Get("input");var name=p.TryGetProperty("name",out var n)?n.GetString():null;
                    if(input.TrimStart().StartsWith("https://",StringComparison.OrdinalIgnoreCase)||input.TrimStart().StartsWith("http://",StringComparison.OrdinalIgnoreCase)) {
                        var parsed=SubscriptionRequest.Parse(name,input);
                        var added=new List<SavedSubscription>();foreach(var url in parsed.Urls)added.Add(await Downloader().DownloadAsync(url,parsed.Name,ct));
                        if(_state.Subscriptions.Count+added.Count>100)throw new BrokerFault("subscription_limit");
                        var candidate=Clone();candidate.Subscriptions.AddRange(added);await CommitAsync(candidate,ct);
                    } else {
                        var nodes=SubscriptionParser.Parse(input).ToList();if(nodes.Count==0||nodes.Count>2000)throw new BrokerFault("invalid_config");
                        if(_state.Subscriptions.Count>=100)throw new BrokerFault("subscription_limit");
                        var candidate=Clone();candidate.Subscriptions.Add(new(){Name=string.IsNullOrWhiteSpace(name)?"Local servers":name,Url="",CachedNodes=nodes,UpdatedAt=DateTimeOffset.UtcNow});await CommitAsync(candidate,ct);
                    }
                    break;
                case "server.import":
                    var group=SingleServerShare.Import(Get("uri"));_ = CanonicalServerShare.Export(group.Nodes.Single());
                    var received=Clone();received.Subscriptions.Add(new(){Name="Shared server",Url="",CachedNodes=group.Nodes.ToList(),UpdatedAt=DateTimeOffset.UtcNow});await CommitAsync(received,ct);break;
                case "server.select":var id=Get("id");if(!_state.Subscriptions.SelectMany(g=>g.CachedNodes).Any(n=>n.Id==id))throw new BrokerFault("server_missing");var selected=Clone();selected.SelectedId=id;await CommitAsync(selected,ct);break;
                case "server.share":share=CanonicalServerShare.Export(_state.Subscriptions.SelectMany(g=>g.CachedNodes).Single(n=>n.Id==Get("id")));break;
                case "subscription.remove":var removed=Clone();removed.Subscriptions.RemoveAll(g=>g.Id==Get("id"));await CommitAsync(removed,ct);break;
                case "settings":
                    var settings=JsonSerializer.Deserialize<MacSettings>(JsonSerializer.Serialize(_settings))!;
                    if(p.TryGetProperty("language",out var language)) {if(language.GetString() is not ("ru" or "en"))throw new ArgumentException();settings.Language=language.GetString()!;}
                    if(p.TryGetProperty("mode",out var mode)){if(!Enum.TryParse<RoutingMode>(mode.GetString(),out var value)||!Enum.IsDefined(value))throw new ArgumentException();settings.Mode=value;}
                    if(p.TryGetProperty("refreshMinutes",out var refresh)){var value=refresh.GetInt32();if(value!=0&&value is <5 or >1440)throw new ArgumentException();settings.RefreshMinutes=value;}
                    _=RoutingPolicySnapshot.Create(settings.Mode,settings.Ru);await SaveSettingsAsync(settings,ct);break;
                case "ru.service":var service=Get("id");if(!RoutingCatalog.LoadEmbedded().Services.Any(s=>s.Id==service))throw new ArgumentException();var prefs=CopySettings();prefs.Ru.DisabledServiceIds.RemoveAll(x=>x==service);if(!p.GetProperty("enabled").GetBoolean())prefs.Ru.DisabledServiceIds.Add(service);await SaveSettingsAsync(prefs,ct);break;
                case "ru.rule":
                    var rules=CopySettings();if(!DomainNames.TryNormalize(Get("value"),out var domain,out _))throw new ArgumentException();
                    var match=Enum.Parse<DomainMatch>(Get("match"));var route=Enum.Parse<DomainRoute>(Get("route"));
                    rules.Ru.CustomRules.RemoveAll(x=>x.Value==domain&&x.Match==match);rules.Ru.CustomRules.Add(new(match,domain,route));
                    if(rules.Ru.Validate() is not null)throw new ArgumentException();await SaveSettingsAsync(rules,ct);break;
                case "ru.remove":var remove=CopySettings();remove.Ru.CustomRules.RemoveAll(x=>x.Value==Get("value"));await SaveSettingsAsync(remove,ct);break;
                case "vpn.connect":throw new BrokerFault("vpn_not_ready");
                case "vpn.disconnect":return new{disconnected=true};
                default:throw new BrokerFault("unknown_method");
            }
        }finally{_gate.Release();}
        if(share is not null) {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new{peer=Get("peer"),uri=share}));
            return await peers.DispatchAsync(new(request.Id,"messages.server",doc.RootElement.Clone()),ct);
        }
        return await SnapshotAsync(ct);
    }
    MacState Clone()=>JsonSerializer.Deserialize<MacState>(JsonSerializer.SerializeToUtf8Bytes(_state))!;
    MacSettings CopySettings()=>new(){Language=_settings.Language,Mode=_settings.Mode,RefreshMinutes=_settings.RefreshMinutes,Ru=_settings.Ru.Copy()};
    async Task SaveSettingsAsync(MacSettings value,CancellationToken ct){CheckSnapshot(_state,value);await PrivateFiles.SaveAsync(Path.Combine(paths.Support,"settings.json"),JsonSerializer.SerializeToUtf8Bytes(value),ct);_settings=value;}
    async Task CommitAsync(MacState candidate,CancellationToken ct)
    {
        var nodes=candidate.Subscriptions.SelectMany(g=>g.CachedNodes).ToArray();
        if(nodes.Length>2000)throw new BrokerFault("too_many_servers");
        if(!nodes.Any(n=>n.Id==candidate.SelectedId))candidate.SelectedId=nodes.FirstOrDefault()?.Id;
        CheckSnapshot(candidate,_settings);await _store.SaveAsync(candidate,ct);_state=candidate;
    }
    async Task RefreshAsync(string id,CancellationToken ct)
    {
        if(!_refreshing.TryAdd(id,true))throw new BrokerFault("refresh_in_progress");
        try {
            string url,name;await _gate.WaitAsync(ct);
            try {await LoadAsync(ct);var old=_state.Subscriptions.SingleOrDefault(g=>g.Id==id);if(old is null||string.IsNullOrEmpty(old.Url))return;url=old.Url;name=old.Name;}
            finally{_gate.Release();}
            var updated=await Downloader().DownloadAsync(url,name,ct);updated.Id=id;
            await _gate.WaitAsync(ct);
            try {
                var current=_state.Subscriptions.SingleOrDefault(g=>g.Id==id);
                if(current is null||current.Url!=url)return; // Removed/edited while downloading: do not resurrect.
                updated.Name=current.Name;
                foreach(var node in updated.CachedNodes){var previous=current.CachedNodes.FirstOrDefault(n=>ConnectionKey(n)==ConnectionKey(node));if(previous is not null)node.Id=previous.Id;}
                var candidate=Clone();candidate.Subscriptions[candidate.Subscriptions.FindIndex(g=>g.Id==id)]=updated;await CommitAsync(candidate,ct);
            }finally{_gate.Release();}
        }finally{_refreshing.TryRemove(id,out _);}
    }
    static string ConnectionKey(VpnNode n)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{n.UserId,n.Host,n.Port,n.Encryption,n.Flow,n.Security,n.Transport,n.Sni,n.Fingerprint,n.PublicKey,n.ShortId,n.SpiderX,n.Mldsa65Verify,n.Alpn,n.AllowInsecure,n.Path,n.HostHeader,n.Mode,n.ServiceName,n.ExtraJson})));
    public async Task RefreshDueAsync(CancellationToken ct)
    {
        string[] due;await _gate.WaitAsync(ct);
        try {await LoadAsync(ct);if(_settings.RefreshMinutes==0)return;
            due=_state.Subscriptions.Where(g=>!string.IsNullOrEmpty(g.Url)&&(!g.UpdatedAt.HasValue||DateTimeOffset.UtcNow-g.UpdatedAt.Value>TimeSpan.FromMinutes(_settings.RefreshMinutes))).Select(g=>g.Id).ToArray();
        }finally{_gate.Release();}
        foreach(var id in due)try{await RefreshAsync(id,ct);}catch(Exception ex) when(ex is not OperationCanceledException){/* Previous encrypted cache remains available. */}
    }
}
