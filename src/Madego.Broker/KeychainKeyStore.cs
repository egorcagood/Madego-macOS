using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace Madego.Broker;

public sealed class KeychainKeyStore(string service,Func<bool> existingSecret) : IWrappingKeyStore
{
    public IWrappingKeyStore ConfirmedRecovery()=>new KeychainKeyStore(service,()=>false);
    public byte[] Read(bool mayCreate)
    {
        var key=new byte[32];
        var status=ReadKey(service,"installation-wrap-v1",mayCreate&&!existingSecret()?1:0,key);
        if(status!=0){CryptographicOperations.ZeroMemory(key);throw new CryptographicException("Keychain unavailable.");}
        return key;
    }
    [DllImport("MadegoKeychain",EntryPoint="madego_keychain_read",CallingConvention=CallingConvention.Cdecl)]
    static extern int ReadKey([MarshalAs(UnmanagedType.LPUTF8Str)] string service,[MarshalAs(UnmanagedType.LPUTF8Str)] string account,int mayCreate,[Out] byte[] output);
}
