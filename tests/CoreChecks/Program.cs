using EgorVpn.Models;
using EgorVpn.Services;
using EgorVpn.Routing;
using System.Text;
using System.Text.Json;
using Madego.Core;
int checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
void Reject(Action action,string name){try{action();}catch(ArgumentException){checks++;return;}throw new Exception(name);}
const string id="11111111-1111-4111-8111-111111111111";
var raw=$"vless://{id}@example.invalid:443?type=xhttp&security=reality&pbk=PUBLIC&sid=ab&spx=%2Fprobe&pqv=MLDSA&path=%2Fx&mode=packet-up#Fixture";
var nodes=SubscriptionParser.Parse(raw+"\n"+raw);Check(nodes.Count==1,"deduplicate lines");
var node=nodes[0];Check(node.SpiderX=="/probe"&&node.Mldsa65Verify=="MLDSA"&&node.Transport=="xhttp","REALITY XHTTP preserved");
Check(SubscriptionParser.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))).Count==1,"base64");
var cfg=new {remarks="Fixture",outbounds=new[]{new {protocol="vless",settings=new {address="example.invalid",port=443,id,encryption="none"},streamSettings=new {network="xhttp",security="reality",realitySettings=new {serverName="sni.invalid",publicKey="PUBLIC",shortId="ab",spiderX="/probe",mldsa65Verify="MLDSA"},xhttpSettings=new {path="/x",mode="packet-up",host="cdn.invalid"}}}}};
Check(SubscriptionParser.Parse(JsonSerializer.Serialize(cfg))[0].Mldsa65Verify=="MLDSA","JSON REALITY");
Check(SubscriptionParser.Parse("{bad json").Count==0,"malformed JSON");
Check(SubscriptionParser.Parse("[42,null,{\"outbounds\":[null,1]}]").Count==0,"malformed JSON shape");
Check(SubscriptionParser.Parse($"vless://not-a-uuid@example.invalid:443").Count==0,"invalid UUID");
Check(SubscriptionParser.Parse($"vless://{id}@example.invalid:0").Count==0,"port zero");
node.RawUri += "&subscription=https%3A%2F%2Fsecret.invalid%2Ftoken";node.Name="https://secret.invalid/token";
var exported=CanonicalServerShare.Export(node);Check(!exported.Contains("secret",StringComparison.Ordinal)&&!exported.Contains("subscription",StringComparison.Ordinal),"no raw metadata/name leak");
var back=SingleServerShare.Import(exported).Nodes.Single();Check(back.SpiderX=="/probe"&&back.Mldsa65Verify=="MLDSA"&&back.Mode=="packet-up","safe export parity");
node.ExtraJson="{\"downloadSettings\":{\"subscription\":\"https://secret.invalid/token\"}}";
Reject(()=>CanonicalServerShare.Export(node),"opaque extra rejected");
node.ExtraJson="";node.Path="/https://secret.invalid/token";Reject(()=>CanonicalServerShare.Export(node),"nested URL rejected");
var policy=RoutingPolicySnapshot.Create(RoutingMode.RuMode,new());Check(policy.MatchHost("unknown.invalid")==DomainRoute.Proxy,"unknown proxy");Check(policy.MatchHost("youtube.com")==DomainRoute.Proxy,"protected proxy");
Console.WriteLine($"CORE_CHECKS_PASS={checks}");
