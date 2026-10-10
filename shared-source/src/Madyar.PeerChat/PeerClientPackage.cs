using System.IO.Compression;
using System.Text.Json;
namespace Madyar.PeerChat;
public sealed record PeerPublicContact(VerifiedContactInvite Invite,string InviteUri) {
    public DateTimeOffset ExpiresUtc=>PeerInvitationPolicy.Expiry(Invite);
    public bool IsExpired=>ExpiresUtc<=DateTimeOffset.UtcNow;
}
public static class PeerClientPackage {
    public const string PublicContactFileName="madyar-contact.json";
    public const string InstallerFileName="Madego-Setup.exe";
    const int MaxPublicBytes=8192;
    const long MaxInstallerBytes=512L*1024*1024;
    public static PeerPublicContact ParsePublicContact(byte[] bytes) {
        try{return ParseCore(bytes);}
        catch(Exception e)when(e is JsonException or FormatException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException) {
            throw new InvalidDataException("Публичный контакт повреждён или имеет неверный формат.",e);
        }
    }
    static PeerPublicContact ParseCore(byte[] bytes) {
        if(bytes is null||bytes.Length==0||bytes.Length>MaxPublicBytes)throw new InvalidDataException("Публичный контакт превышает допустимый размер.");
        using var document=PeerWire.Object(bytes,"schemaVersion","inviteUri");var root=document.RootElement;
        if(!root.GetProperty("schemaVersion").TryGetInt32(out var schema)||schema!=1)throw new InvalidDataException("Неподдерживаемая версия публичного контакта.");
        var uri=PeerWire.String(root,"inviteUri");
        if(!uri.StartsWith("madyar://contact/",StringComparison.Ordinal))throw new InvalidDataException("Нужна подписанная ссылка публичного контакта.");
        var invite=PeerInviteCodec.Parse(uri);PeerInvitationPolicy.Validate(invite,allowExpired:true);
        return new(invite,uri);
    }
    public static byte[] SerializePublicContact(ContactInviteDisplay invite) {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,inviteUri=invite.Uri});
        PeerInvitationPolicy.Validate(ParsePublicContact(bytes).Invite);return bytes;
    }
    // Bounded public sidecar read for constructors/validators before a UI message loop exists.
    // Reuses the same no-links, size and signed-contact parser rules; never opens a private profile.
    public static PeerPublicContact? ReadPublicContact(string path) {
        PeerFiles.NoLinks(path);
        if(!File.Exists(path))return null;
        return ParsePublicContact(PeerFiles.Read(path,MaxPublicBytes));
    }
    public static async Task<PeerPublicContact?> ReadPublicContactAsync(string path,CancellationToken ct=default) {
        ct.ThrowIfCancellationRequested();PeerFiles.NoLinks(path);
        if(!File.Exists(path))return null;
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>MaxPublicBytes)throw new InvalidDataException("Публичный контакт превышает допустимый размер.");
        var bytes=new byte[(int)stream.Length];await stream.ReadExactlyAsync(bytes,ct);return ParsePublicContact(bytes);
    }
    public static async Task WritePublicContactAsync(string path,ContactInviteDisplay invite,CancellationToken ct=default) {
        ct.ThrowIfCancellationRequested();var bytes=SerializePublicContact(invite);PeerFiles.NoLinks(path);
        var temp=path+".tmp-"+Guid.NewGuid().ToString("N");bool created=false;
        try {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                created=true;await stream.WriteAsync(bytes,ct);stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();PeerFiles.NoLinks(path);File.Move(temp,path,overwrite:false);
        }finally{if(created&&File.Exists(temp))File.Delete(temp);}
    }
    public static async Task CreateShareZipAsync(string installerPath,string destination,ContactInviteDisplay invite,CancellationToken ct=default) {
        ct.ThrowIfCancellationRequested();var publicBytes=SerializePublicContact(invite);
        PeerFiles.NoLinks(installerPath);PeerFiles.NoLinks(destination);
        if(!Path.GetExtension(installerPath).Equals(".exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(installerPath)||
            (File.GetAttributes(installerPath)&(FileAttributes.Directory|FileAttributes.Device|FileAttributes.ReparsePoint))!=0)
            throw new InvalidDataException("Выберите обычный файл установщика .exe.");
        if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("Файл назначения уже существует. Выберите новое имя.");
        using var source=new FileStream(installerPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(source.Length<2||source.Length>MaxInstallerBytes)throw new InvalidDataException("Установщик пуст или превышает 512 МиБ.");
        var header=new byte[2];await source.ReadExactlyAsync(header,ct);
        if(header[0]!='M'||header[1]!='Z')throw new InvalidDataException("Выбранный файл не является Windows EXE.");
        var expectedLength=source.Length;source.Position=0;var temp=destination+".tmp-"+Guid.NewGuid().ToString("N");bool created=false;
        try {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                created=true;
                using(var archive=new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:true)) {
                    using(var entry=archive.CreateEntry(InstallerFileName,CompressionLevel.NoCompression).Open()) {
                        var buffer=new byte[131072];long copied=0;int read;
                        while((read=await source.ReadAsync(buffer,ct))!=0) {
                            copied+=read;if(copied>expectedLength||copied>MaxInstallerBytes)throw new IOException("Файл установщика изменился при упаковке.");
                            await entry.WriteAsync(buffer.AsMemory(0,read),ct);
                        }
                        if(copied!=expectedLength||source.Length!=expectedLength)throw new IOException("Файл установщика изменился при упаковке.");
                    }
                    using(var entry=archive.CreateEntry(PublicContactFileName,CompressionLevel.NoCompression).Open())await entry.WriteAsync(publicBytes,ct);
                }
                output.Flush(true);
            }
            ct.ThrowIfCancellationRequested();PeerFiles.NoLinks(destination);File.Move(temp,destination,overwrite:false);
        }finally{if(created&&File.Exists(temp))File.Delete(temp);}
    }
}
