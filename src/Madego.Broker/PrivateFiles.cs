using System.Runtime.InteropServices;
namespace Madego.Broker;

public static class PrivateFiles
{
    [DllImport("libc",EntryPoint="umask")] static extern uint Umask(uint mask);
    public static void SetProcessMask()=>Umask(0x3f); // 077: applies before any PeerWorkspace SQLite/journal creation.
    public static void NoLinks(string path)
    {
        for(var p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
            if(new FileInfo(p).LinkTarget is not null||new DirectoryInfo(p).LinkTarget is not null)throw new IOException("Symlink path rejected.");
    }
    public static void Directory(string path)
    {
        NoLinks(path);System.IO.Directory.CreateDirectory(path);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }
    public static async Task SaveAsync(string path,byte[] bytes,CancellationToken ct)
    {
        NoLinks(path);Directory(Path.GetDirectoryName(path)!);var temp=path+".tmp-"+Guid.NewGuid().ToString("N");
        try {
            await File.WriteAllBytesAsync(temp,bytes,ct);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            using(var file=new FileStream(temp,FileMode.Open,FileAccess.ReadWrite,FileShare.None))file.Flush(true);
            if(File.Exists(path)) {NoLinks(path+".bak");File.Copy(path,path+".bak",true);}
            File.Move(temp,path,true);
        } finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
