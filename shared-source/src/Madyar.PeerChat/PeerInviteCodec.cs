using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Madyar.PeerChat;
public sealed record ContactInviteDisplay(string Uri, string Code);
internal static class PeerWire {
    internal static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    internal static byte[] Decode(string value,int max) {
        if(value.Length==0||value.Length>max||value.Any(c=>!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))throw new InvalidDataException("Неверный код контакта.");
        var bytes=Convert.FromBase64String(value.Replace('-','+').Replace('_','/')+new string('=',(4-value.Length%4)%4));
        if(Encode(bytes)!=value)throw new InvalidDataException("Неверное кодирование контакта.");return bytes;
    }
    internal static JsonDocument Object(byte[] bytes, params string[] fields) {
        _=new UTF8Encoding(false,true).GetString(bytes);
        var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions {MaxDepth=8});
        try { Fields(doc.RootElement,fields);return doc; } catch {doc.Dispose();throw;}
    }
    internal static void Fields(JsonElement value,params string[] fields) {
        if(value.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Неверный формат.");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in value.EnumerateObject()) if(!fields.Contains(p.Name,StringComparer.Ordinal)||!seen.Add(p.Name))throw new InvalidDataException("Неизвестное или повторное поле.");
        if(seen.Count!=fields.Length)throw new InvalidDataException("Не хватает обязательного поля.");
    }
    internal static string String(JsonElement o,string field) => o.GetProperty(field).ValueKind==JsonValueKind.String?o.GetProperty(field).GetString()!:throw new InvalidDataException("Неверный формат поля.");
}
public static class PeerInviteCodec {
    const string UriPrefix="madyar://contact/",CodePrefix="MDC1-";
    static readonly byte[] Domain=Encoding.UTF8.GetBytes("madyar-contact-v1\n");
    public static VerifiedContactInvite Parse(string input) {
        if(input==null||input.Length>4096)throw new InvalidDataException("Код слишком длинный.");
        string encoded;
        if(input.StartsWith(UriPrefix,StringComparison.Ordinal))encoded=input[UriPrefix.Length..];
        else {
            var code=new string(input.Where(c=>c is not (' ' or '\t' or '\r' or '\n')).ToArray());
            if(!code.StartsWith(CodePrefix,StringComparison.Ordinal))throw new InvalidDataException("Нужна ссылка madyar://contact/… или код MDC1-…");encoded=code[CodePrefix.Length..];
        }
        using var outer=PeerWire.Object(PeerWire.Decode(encoded,2048),"version","payload","signature");
        var o=outer.RootElement;
        if(!o.GetProperty("version").TryGetInt32(out var version)||version!=1)throw new InvalidDataException("Эта версия приглашения не поддерживается.");
        var payload=PeerWire.Decode(PeerWire.String(o,"payload"),1366);if(payload.Length>1024)throw new InvalidDataException("Приглашение слишком большое.");
        var sig=PeerWire.Decode(PeerWire.String(o,"signature"),86);
        using var document=PeerWire.Object(payload,"peerId","publicKey","nickname","inviteId","createdUtc");var p=document.RootElement;
        var id=PeerWire.String(p,"peerId");var key=PeerWire.Decode(PeerWire.String(p,"publicKey"),43);var name=PeerWire.String(p,"nickname");
        if(!PeerIdCodec.Matches(id,key)||PeerFiles.Name(name)!=name)throw new InvalidDataException("Контакт не соответствует открытому ключу.");
        if(!Guid.TryParseExact(PeerWire.String(p,"inviteId"),"D",out var inviteId)||inviteId==Guid.Empty)throw new InvalidDataException("Неверный ID приглашения.");
        if(!DateTimeOffset.TryParseExact(PeerWire.String(p,"createdUtc"),"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var created)||created.Offset!=TimeSpan.Zero)throw new InvalidDataException("Неверная дата приглашения.");
        if(!PeerSigning.Verify(key,Domain.Concat(payload).ToArray(),sig))throw new InvalidDataException("Подпись контакта не прошла проверку.");
        return new(id,key,name,inviteId,created,UriPrefix+encoded);
    }
    internal static ContactInviteDisplay Create(PeerProfile profile,byte[] seed) {
        PeerFiles.Name(profile.Nickname);
        var payload=JsonSerializer.SerializeToUtf8Bytes(new {peerId=profile.PeerId,publicKey=PeerWire.Encode(profile.PublicKey),nickname=profile.Nickname,inviteId=Guid.NewGuid().ToString("D"),createdUtc=DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture)});
        var encoded=PeerWire.Encode(JsonSerializer.SerializeToUtf8Bytes(new {version=1,payload=PeerWire.Encode(payload),signature=PeerWire.Encode(PeerSigning.Sign(seed,Domain.Concat(payload).ToArray()))}));
        var result=new ContactInviteDisplay(UriPrefix+encoded,CodePrefix+encoded);_=Parse(result.Uri);return result;
    }
}
public sealed partial class PeerWorkspace {
    public Task<ContactInviteDisplay> CreateInviteAsync(CancellationToken ct=default)=>Locked(()=> {
        var seed=ReadSeed();try {
            var display=PeerInviteCodec.Create(Profile(),seed);var verified=PeerInviteCodec.Parse(display.Uri);
            Introductions.Register(new(verified.InviteId.ToString("D"),verified.CreatedUtc.AddDays(90)));
            return display;
        }finally {CryptographicOperations.ZeroMemory(seed);}
    },ct);
}
