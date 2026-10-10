using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Madyar.PeerChat;
internal sealed record BackupSnapshot(int SchemaVersion, byte[] Seed, PeerProfile Profile, PeerContact[] Contacts, [property: System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PeerBlockedContact[]? BlockedContacts=null) : IDisposable {
    public void Dispose() => CryptographicOperations.ZeroMemory(Seed);
}
public sealed class PeerBackupPreview : IDisposable {
    readonly object _sync = new();
    readonly BackupSnapshot _snapshot;
    readonly DateTimeOffset _expires;
    readonly TimeProvider _clock;
    readonly ITimer _expiry;
    bool _disposed;
    internal PeerBackupPreview(BackupSnapshot snapshot):this(snapshot,TimeProvider.System){}
    internal PeerBackupPreview(BackupSnapshot snapshot,TimeProvider clock) {
        _snapshot=snapshot;_clock=clock;_expires=clock.GetUtcNow().AddMinutes(5);
        _expiry=clock.CreateTimer(s=>((PeerBackupPreview)s!).Dispose(),this,TimeSpan.FromMinutes(5),Timeout.InfiniteTimeSpan);
    }
    public PeerProfile Profile => new(_snapshot.Profile.PeerId, _snapshot.Profile.PublicKey.ToArray(), _snapshot.Profile.Nickname);
    public int ContactCount => _snapshot.Contacts.Length;
    internal T Use<T>(Func<BackupSnapshot,T> action) {
        lock(_sync) {
            if(_disposed || _clock.GetUtcNow() > _expires) { Dispose(); throw new InvalidOperationException("Предпросмотр истёк. Откройте резервную копию снова."); }
            return action(_snapshot);
        }
    }
    public void Dispose() { lock(_sync) { if(_disposed)return;_disposed=true;_snapshot.Dispose();_expiry.Dispose(); } }
}
internal static class PeerBackupCodec {
    internal const int MaximumFile = 16 * 1024 * 1024;
    const int HeaderLength=48,Iterations=600000;
    static byte[] PasswordBytes(string password) {
        if(password.Length is < 10 or > 256)throw new InvalidDataException("Пароль должен содержать от 10 до 256 символов.");
        return new UTF8Encoding(false,true).GetBytes(password);
    }
    internal static void ValidatePassword(string password) {
        var bytes=PasswordBytes(password);CryptographicOperations.ZeroMemory(bytes);
    }
    internal static byte[] Encrypt(BackupSnapshot snapshot,string password,CancellationToken ct) {
        ct.ThrowIfCancellationRequested();Validate(snapshot);
        byte[] pass=PasswordBytes(password),plain=[],key=[];
        try {
            plain=JsonSerializer.SerializeToUtf8Bytes(snapshot,PeerFiles.Json);
            if(plain.Length>MaximumFile-HeaderLength-16)throw new InvalidDataException("Резервная копия слишком большая.");
            var output=new byte[HeaderLength+plain.Length+16];"MADYARB1"u8.CopyTo(output);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8,4),1);BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12,4),Iterations);BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16,4),(uint)plain.Length);
            RandomNumberGenerator.Fill(output.AsSpan(20,28));
            key=Rfc2898DeriveBytes.Pbkdf2(pass,output.AsSpan(20,16),Iterations,HashAlgorithmName.SHA256,32);
            ct.ThrowIfCancellationRequested();using var aes=new AesGcm(key,16);
            aes.Encrypt(output.AsSpan(36,12),plain,output.AsSpan(HeaderLength,plain.Length),output.AsSpan(HeaderLength+plain.Length,16),output.AsSpan(0,HeaderLength));return output;
        } finally {CryptographicOperations.ZeroMemory(plain);CryptographicOperations.ZeroMemory(pass);CryptographicOperations.ZeroMemory(key);}
    }
    internal static BackupSnapshot Decrypt(byte[] input,string password,CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if(input.Length is < 65 or > MaximumFile || !input.AsSpan(0,8).SequenceEqual("MADYARB1"u8) || BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8,4))!=1 || BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(12,4))!=Iterations)throw new InvalidDataException("Неподдерживаемый или повреждённый backup.");
        var length=BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(16,4));
        if(length>MaximumFile-64 || length!=input.Length-64)throw new InvalidDataException("Неверный размер backup.");
        byte[] pass=PasswordBytes(password),plain=new byte[(int)length],key=[];
        BackupSnapshot? snapshot=null;
        try {
            key=Rfc2898DeriveBytes.Pbkdf2(pass,input.AsSpan(20,16),Iterations,HashAlgorithmName.SHA256,32);ct.ThrowIfCancellationRequested();
            using var aes=new AesGcm(key,16);
            try {aes.Decrypt(input.AsSpan(36,12),input.AsSpan(48,(int)length),input.AsSpan(48+(int)length,16),plain,input.AsSpan(0,48));}
            catch(AuthenticationTagMismatchException) {throw new InvalidDataException("Неверный пароль или повреждённая резервная копия.");}
            ct.ThrowIfCancellationRequested();
            using var doc=JsonDocument.Parse(plain,new JsonDocumentOptions {MaxDepth=8});
            var schema=doc.RootElement.GetProperty("schemaVersion").GetInt32();
            if(schema==1)PeerWire.Fields(doc.RootElement,"schemaVersion","seed","profile","contacts");
            else if(schema==2)PeerWire.Fields(doc.RootElement,"schemaVersion","seed","profile","contacts","blockedContacts");
            else throw new InvalidDataException("Неподдерживаемая версия backup.");
            if(schema==2){var blocked=doc.RootElement.GetProperty("blockedContacts");if(blocked.ValueKind!=JsonValueKind.Array||blocked.GetArrayLength()>10000)throw new InvalidDataException("Неверный список блокировок.");foreach(var c in blocked.EnumerateArray())PeerWire.Fields(c,"peerId","publicKey","nickname","blockedUtc");}
            PeerWire.Fields(doc.RootElement.GetProperty("profile"),"peerId","publicKey","nickname");
            var contacts=doc.RootElement.GetProperty("contacts");if(contacts.ValueKind!=JsonValueKind.Array||contacts.GetArrayLength()>10000)throw new InvalidDataException("Неверный список контактов.");
            foreach(var contact in contacts.EnumerateArray())PeerWire.Fields(contact,"peerId","publicKey","nickname","alias","addedUtc","lastSeenUtc");
            snapshot=JsonSerializer.Deserialize<BackupSnapshot>(plain,PeerFiles.Json)??throw new InvalidDataException("Неверный backup.");Validate(snapshot);return snapshot;
        } catch {snapshot?.Dispose();throw;} finally {CryptographicOperations.ZeroMemory(pass);CryptographicOperations.ZeroMemory(plain);CryptographicOperations.ZeroMemory(key);}
    }
    internal static void Validate(BackupSnapshot snapshot) {
        if(snapshot.SchemaVersion is not (1 or 2)||snapshot.Seed==null||snapshot.Seed.Length!=32||snapshot.Profile==null||snapshot.Contacts==null||snapshot.Contacts.Length>10000)throw new InvalidDataException("Неверный состав backup.");
        if(snapshot.SchemaVersion==1&&snapshot.BlockedContacts!=null||snapshot.SchemaVersion==2&&(snapshot.BlockedContacts==null||snapshot.BlockedContacts.Length>10000))throw new InvalidDataException("Неверный список блокировок backup.");
        var key=PeerSigning.PublicKey(snapshot.Seed);var p=snapshot.Profile;
        if(p.PublicKey==null||!key.SequenceEqual(p.PublicKey)||!PeerIdCodec.Matches(p.PeerId,key)||PeerFiles.Name(p.Nickname,true)!=p.Nickname)throw new InvalidDataException("Ключ backup не совпадает с профилем.");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var c in snapshot.Contacts) {PeerContactStore.Validate(c);if(c.PeerId==p.PeerId||!ids.Add(c.PeerId))throw new InvalidDataException("Повторный или собственный контакт в backup.");}
        foreach(var c in snapshot.BlockedContacts??[]) {PeerContactStore.ValidateBlock(c);if(c.PeerId==p.PeerId||!ids.Add(c.PeerId))throw new InvalidDataException("Повторная, собственная или добавленная блокировка в backup.");}
    }
}
public sealed partial class PeerWorkspace {

    public async Task ExportBackupAsync(string path,string password,CancellationToken ct=default) {
        PeerBackupCodec.ValidatePassword(password);
        PeerFiles.NoLinks(path);if(File.Exists(path))throw new IOException("Файл уже существует. Выберите новое имя.");
        using var snapshot=await Locked(()=>new BackupSnapshot(2,ReadSeed(),Profile(),Contacts.List().ToArray(),Contacts.Blocked().ToArray()),ct).ConfigureAwait(false);
        var encrypted=await Task.Run(()=>PeerBackupCodec.Encrypt(snapshot,password,ct),ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();PeerFiles.Write(path,encrypted,false);
    }
    public Task<PeerBackupPreview> PreviewBackupAsync(string path,string password,CancellationToken ct=default) => PreviewBackupFileAsync(path,password,ct);
    public static Task<PeerBackupPreview> PreviewBackupFileAsync(string path,string password,CancellationToken ct=default) => Task.Run(()=> {
        ct.ThrowIfCancellationRequested();var input=PeerFiles.Read(path,PeerBackupCodec.MaximumFile);
        return new PeerBackupPreview(PeerBackupCodec.Decrypt(input,password,ct));
    },ct);
}
