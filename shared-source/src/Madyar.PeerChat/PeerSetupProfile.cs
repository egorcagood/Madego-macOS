using System.Text;
using System.Text.Json;
namespace Madyar.PeerChat;
public static class PeerSetupProfile
{
    public const string PendingNameFile="pending-chat-name.json";
    public static string NormalizeName(string name)
    {
        try{return PeerFiles.Name(name);}
        catch(EncoderFallbackException error){throw new InvalidDataException("Имя содержит некорректный символ. Введите его заново.",error);}
    }
    public static string EncodeName(string name)=>Convert.ToBase64String(new UTF8Encoding(false,true).GetBytes(NormalizeName(name)));
    public static string DecodeName(string encoded)
    {
        if(encoded.Length is <1 or >256)throw new InvalidDataException("Неверное имя для установки.");
        try{return NormalizeName(new UTF8Encoding(false,true).GetString(Convert.FromBase64String(encoded)));}
        catch(Exception error) when(error is FormatException or DecoderFallbackException){throw new InvalidDataException("Неверное имя для установки.",error);}
    }
    public static async Task ExportPublicContactAsync(PeerWorkspace workspace,string path,string? name,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(name is not null)await workspace.SetNicknameAsync(NormalizeName(name),ct);
        else NormalizeName((await workspace.ReadProfileAsync(ct)).Nickname);
        var invite=await workspace.CreateInviteAsync(ct);
        await PeerClientPackage.WritePublicContactAsync(path,invite,ct);
    }
    public static void WritePendingName(string directory,string name)
    {
        name=NormalizeName(name);
        PeerFiles.Write(Path.Combine(directory,PendingNameFile),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,nickname=name}));
    }
    public static async Task ApplyPendingNameAsync(string directory,PeerWorkspace workspace,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();var path=Path.Combine(directory,PendingNameFile);
        if(!File.Exists(path))return;
        using var doc=PeerWire.Object(PeerFiles.Read(path,1024),"schemaVersion","nickname");
        if(!doc.RootElement.GetProperty("schemaVersion").TryGetInt32(out var version)||version!=1)throw new InvalidDataException("Неверные настройки имени установщика.");
        var name=NormalizeName(PeerWire.String(doc.RootElement,"nickname"));
        await workspace.SetNicknameAsync(name,ct);
        File.Delete(path);
    }
}
