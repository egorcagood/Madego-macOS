using System.Security.Cryptography;
using System.Text.Json;
namespace Madego.Broker;

public sealed class SecretJsonStore<T>(string path,WrappingProtector protector) where T:class,new()
{
    const int MaximumBytes=16*1024*1024;
    public async Task<T> LoadAsync(CancellationToken ct)
    {
        PrivateFiles.NoLinks(path);if(!File.Exists(path))return new();
        if(new FileInfo(path).Length>MaximumBytes)throw new InvalidDataException("Store too large.");
        var plain=protector.Unprotect(await File.ReadAllBytesAsync(path,ct));
        try {return JsonSerializer.Deserialize<T>(plain)??throw new InvalidDataException("Empty store.");}
        finally {CryptographicOperations.ZeroMemory(plain);}
    }
    public async Task SaveAsync(T value,CancellationToken ct)
    {
        var plain=JsonSerializer.SerializeToUtf8Bytes(value);
        try {if(plain.Length>MaximumBytes-29)throw new InvalidDataException("Store too large.");await PrivateFiles.SaveAsync(path,protector.Protect(plain),ct);}
        finally {CryptographicOperations.ZeroMemory(plain);}
    }
}
