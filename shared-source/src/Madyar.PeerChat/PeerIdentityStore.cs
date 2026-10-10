using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Madyar.PeerChat;
internal static class PeerFiles {
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    internal static string Name(string value, bool allowEmpty = false) {
        var name = value.Trim();
        if ((!allowEmpty && name.Length == 0) || name.Length > 40 || name.Any(char.IsControl)) throw new InvalidDataException("Имя должно содержать от 1 до 40 символов без управляющих знаков.");
        _ = new UTF8Encoding(false, true).GetBytes(name);
        return name;
    }
    internal static void NoLinks(string path) {
        for (var p = new DirectoryInfo(Path.GetFullPath(path)); p != null; p = p.Parent)
            if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки в пути профиля не поддерживаются.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Файл является ссылкой.");
    }
    internal static byte[] Read(string path, int max = 65536) {
        NoLinks(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (f.Length > max) throw new InvalidDataException("Файл превышает допустимый размер.");
        var bytes = new byte[(int)f.Length]; f.ReadExactly(bytes); return bytes;
    }
    internal static void Write(string path, byte[] bytes, bool overwrite = true) {
        NoLinks(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try {
            using (var f = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes); f.Flush(true); }
            File.Move(temporary, path, overwrite);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static T Load<T>(string path) => JsonSerializer.Deserialize<T>(Read(path), Json) ?? throw new InvalidDataException("Повреждённый профиль.");
    internal static void Save<T>(string path, T value, bool overwrite = true) => Write(path, JsonSerializer.SerializeToUtf8Bytes(value, Json), overwrite);
}
internal sealed record IdentityDocument(int SchemaVersion, string PeerId, byte[] PublicKey, byte[] ProtectedSeed);
internal sealed record ProfileDocument(int SchemaVersion, string Nickname);
internal static class PeerIdentityStore {
    internal static IdentityDocument Create(byte[] seed, IIdentityProtector protector) {
        var key = PeerSigning.PublicKey(seed);
        return new(1, PeerIdCodec.FromPublicKey(key), key, protector.Protect(seed));
    }
    internal static (IdentityDocument Identity, string Name) Load(string path, IIdentityProtector protector) {
        var identity = PeerFiles.Load<IdentityDocument>(Path.Combine(path, "identity.json"));
        if (identity.SchemaVersion != 1 || identity.PublicKey == null || identity.ProtectedSeed == null || identity.ProtectedSeed.Length > 16384 || !PeerIdCodec.Matches(identity.PeerId, identity.PublicKey)) throw new InvalidDataException("Повреждённый ключ. Восстановите резервную копию; новый ключ не создан.");
        var seed = protector.Unprotect(identity.ProtectedSeed);
        try { if (!PeerSigning.PublicKey(seed).SequenceEqual(identity.PublicKey)) throw new InvalidDataException("Ключ профиля не совпадает."); }
        finally { CryptographicOperations.ZeroMemory(seed); }
        var profile = PeerFiles.Load<ProfileDocument>(Path.Combine(path, "profile.json"));
        if (profile.SchemaVersion != 1 || profile.Nickname == null || PeerFiles.Name(profile.Nickname, true) != profile.Nickname) throw new InvalidDataException("Повреждённое имя профиля.");
        return (identity, profile.Nickname);
    }
}
