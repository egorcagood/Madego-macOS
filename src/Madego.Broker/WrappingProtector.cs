using System.Security.Cryptography;
using System.Text;
using Madyar.PeerChat;
namespace Madego.Broker;

public interface IWrappingKeyStore { byte[] Read(bool mayCreate); IWrappingKeyStore ConfirmedRecovery()=>throw new CryptographicException("Explicit recovery unavailable."); }

public sealed class WrappingProtector(IWrappingKeyStore store,string purpose="peer-identity") : IIdentityProtector
{
    readonly byte[] _aad=Encoding.UTF8.GetBytes("Madego/macOS/v1/"+purpose);
    public byte[] Protect(byte[] bytes)
    {
        var key=store.Read(true);
        try {
            var result=new byte[29+bytes.Length];result[0]=1;
            RandomNumberGenerator.Fill(result.AsSpan(1,12));
            using var aes=new AesGcm(key,16);
            aes.Encrypt(result.AsSpan(1,12),bytes,result.AsSpan(29),result.AsSpan(13,16),_aad);
            return result;
        } finally {CryptographicOperations.ZeroMemory(key);}
    }
    public byte[] Unprotect(byte[] bytes)
    {
        if(bytes.Length<29||bytes[0]!=1)throw new CryptographicException("Invalid envelope.");
        var key=store.Read(false);
        try {
            var plain=new byte[bytes.Length-29];using var aes=new AesGcm(key,16);
            try {aes.Decrypt(bytes.AsSpan(1,12),bytes.AsSpan(29),bytes.AsSpan(13,16),plain,_aad);return plain;}
            catch {CryptographicOperations.ZeroMemory(plain);throw;}
        } finally {CryptographicOperations.ZeroMemory(key);}
    }
}
