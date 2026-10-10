using EgorVpn.Models;
using EgorVpn.Services;
using System.Text.Json.Nodes;
void Check(bool yes,string name){if(!yes)throw new Exception(name);Console.WriteLine("PASS "+name);}
foreach(var provider in new[]{"Cloudflare","Google","Quad9"})foreach(var endpoint in new[]{"192.0.2.10","2001:db8::1"}){
 var settings=new AppSettings{DnsProvider=provider,Mtu=1400};
 var before=JsonNode.Parse(LegacySingBoxConfigBuilder.Build(endpoint,settings:settings))!;
 var now=JsonNode.Parse(SingBoxConfigBuilder.Build(endpoint,settings:settings,retainFakeIpMappings:false))!;
 now["dns"]!.AsObject().Remove("reverse_mapping"); // Explicit false strengthens conservative domain classification; all other FullVPN settings are preserved.
 Check(now["dns"]!["rules"]!.AsArray().All(r=>r?["server"]?.GetValue<string>()=="doh"),"FullVPN policy DNS rules never select DIRECT resolver");
 now["dns"]!.AsObject().Remove("rules"); // New protected domain rules route to the same sole VPN resolver, without a DIRECT branch.
 var rules=now["route"]!["rules"]!.AsArray();
 Check(rules.Where(r=>r?["domain"]!=null||r?["domain_suffix"]!=null).All(r=>r?["outbound"]?.GetValue<string>()=="xray"),"FullVPN policy routes never select DIRECT outbound");
 for(var i=rules.Count-1;i>=0;i--)if(rules[i]?["domain"]!=null||rules[i]?["domain_suffix"]!=null)rules.RemoveAt(i); // Protected rules route to the existing final Xray outbound.
 Check(JsonNode.DeepEquals(before,now),"FullVpn preserves previous endpoint/process/TUN/IPv4IPv6/DNS/Xray config: "+provider+"/"+endpoint);
}
