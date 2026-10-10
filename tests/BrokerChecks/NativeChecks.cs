using Madego.Broker;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
static class NativeChecks
{
    [DllImport("MadegoKeychain",EntryPoint="madego_keychain_delete_test")]
    static extern int Delete([MarshalAs(UnmanagedType.LPUTF8Str)]string service);
    public static void Run(string path)
    {
        var native=NativeLibrary.Load(Path.GetFullPath(path));
        NativeLibrary.SetDllImportResolver(typeof(KeychainKeyStore).Assembly,(name,_,_)=>name=="MadegoKeychain"?native:IntPtr.Zero);
        NativeLibrary.SetDllImportResolver(typeof(NativeChecks).Assembly,(name,_,_)=>name=="MadegoKeychain"?native:IntPtr.Zero);
        var service="dev.madego.test."+Guid.NewGuid().ToString("N");
        try {
            var store=new KeychainKeyStore(service,()=>false);var key=store.Read(true);var restored=store.Read(false);
            if(!key.SequenceEqual(restored)||key.Length!=32)throw new Exception("native Keychain key restart");
            CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(restored);
            var missing=new KeychainKeyStore(service+".missing",()=>true);bool rejected=false;
            try{_=missing.Read(true);}catch(CryptographicException){rejected=true;}
            if(!rejected)throw new Exception("existing identity cannot create wrapping key");
            Console.WriteLine("NATIVE_KEYCHAIN_CHECKS_PASS=2");
        }finally {Delete(service);}
    }
}
