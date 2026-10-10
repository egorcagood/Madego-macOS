using Madyar.PeerChat;
using System.Text;
using System.Text.Json;
namespace PeerChatChecks;
internal static partial class Program {
 static string B64(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
 static byte[] Unb64(string s)=>Convert.FromBase64String(s.Replace('-','+').Replace('_','/')+new string('=',(4-s.Length%4)%4));
 static string Forge(string payload,byte[] seed,int version=1) {
 var bytes=Encoding.UTF8.GetBytes(payload);var signature=PeerSigning.Sign(seed,Encoding.UTF8.GetBytes("madyar-contact-v1\n").Concat(bytes).ToArray());return "MDC1-"+B64(JsonSerializer.SerializeToUtf8Bytes(new{version,payload=B64(bytes),signature=B64(signature)})); }
 static string ValidPayload(byte[] seed,string name="Егор") {var k=PeerSigning.PublicKey(seed);return JsonSerializer.Serialize(new{peerId=PeerIdCodec.FromPublicKey(k),publicKey=B64(k),nickname=name,inviteId=Guid.NewGuid(),createdUtc=DateTimeOffset.UtcNow.ToString("O")});}
 static Task RejectInvite(string s)=>Throws(()=>{PeerInviteCodec.Parse(s);return Task.CompletedTask;});
 static void InviteTests() {
 Tests.Add(("invites","SignedInviteRoundTrip",async()=>{await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());await w.SetNicknameAsync("Егор");var i=await w.CreateInviteAsync();var p=PeerInviteCodec.Parse(i.Uri);Check(p.PeerId==(await w.ReadProfileAsync()).PeerId&&p.Nickname=="Егор");Check(p.PublicKey.SequenceEqual((await w.ReadProfileAsync()).PublicKey));}));
 Tests.Add(("invites","BitFlipRejects",async()=>{var seed=new byte[32];var i=Forge(ValidPayload(seed),seed);var outer=Unb64(i[5..]);outer[^4]^=1;await RejectInvite("MDC1-"+B64(outer));}));
 Tests.Add(("invites","WrongPeerIdRejects",async()=>{var seed=new byte[32];var p=ValidPayload(seed).Replace(PeerIdCodec.FromPublicKey(PeerSigning.PublicKey(seed)),Contact().PeerId);await RejectInvite(Forge(p,seed));}));
 Tests.Add(("invites","InvalidUtf8DuplicateUnknownJsonRejects",async()=>{var seed=new byte[32];var p=ValidPayload(seed);await RejectInvite(Forge(p.Insert(1,"\"nickname\":\"duplicate\","),seed));await RejectInvite(Forge(p.Insert(1,"\"other\":1,"),seed));var b=new byte[]{0xff,0xff};var s=PeerSigning.Sign(seed,Encoding.UTF8.GetBytes("madyar-contact-v1\n").Concat(b).ToArray());await RejectInvite("MDC1-"+B64(JsonSerializer.SerializeToUtf8Bytes(new{version=1,payload=B64(b),signature=B64(s)})));}));
 Tests.Add(("invites","OversizeAndMalformedUriReject",async()=>{var seed=new byte[32];var i=Forge(ValidPayload(seed),seed)[5..];foreach(var bad in new[]{new string('x',4097),"madyar://contact:80/"+i,"madyar://user@contact/"+i,"madyar://contact/"+i+"?q=1","madyar://contact/"+i+"#x","madyar://contact/%"+i," madyar://contact/"+i})await RejectInvite(bad);}));
 Tests.Add(("invites","WrappedCodeEqualsUri",async()=>{await using var w=await PeerWorkspace.OpenAsync(Temp(),new TestProtector());await w.SetNicknameAsync("Иван");var i=await w.CreateInviteAsync();var code=i.Code.Insert(50,"\r\n\t ");Check(PeerInviteCodec.Parse(code).PeerId==PeerInviteCodec.Parse(i.Uri).PeerId);}));
 Tests.Add(("invites","PrivateSeedNeverInInvite",async()=>{var path=Temp();await using var w=await PeerWorkspace.OpenAsync(path,new TestProtector());await w.SetNicknameAsync("Егор");var i=await w.CreateInviteAsync();using var identity=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(path,"identity.json")));var seed=new TestProtector().Unprotect(identity.RootElement.GetProperty("protectedSeed").GetBytesFromBase64());using var outer=JsonDocument.Parse(Unb64(i.Code[5..]));var payload=Encoding.UTF8.GetString(Unb64(outer.RootElement.GetProperty("payload").GetString()!));Check(!payload.Contains(B64(seed))&&!payload.Contains("protectedSeed")&&!payload.Contains("address"));}));
 Tests.Add(("invites","OldAndFutureProtocolReject",async()=>{var seed=new byte[32];foreach(var v in new[]{0,2,int.MaxValue})await RejectInvite(Forge(ValidPayload(seed),seed,v));}));
 Tests.Add(("invites","UnicodeNamesRoundTrip",()=>{var seed=new byte[32];foreach(var name in new[]{"Егор","李明","🕊 Наташа"})Check(PeerInviteCodec.Parse(Forge(ValidPayload(seed,name),seed)).Nickname==name);return Task.CompletedTask;}));
 }
}
