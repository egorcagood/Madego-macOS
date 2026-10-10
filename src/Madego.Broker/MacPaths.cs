namespace Madego.Broker;

public sealed record MacPaths(string Support,string Cache,string Logs)
{
    public string Peer=>Path.Combine(Support,"PeerChat");
    public static MacPaths Resolve(string? testRoot=null)
    {
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths=testRoot is null?new MacPaths(Path.Combine(home,"Library/Application Support/Madego"),Path.Combine(home,"Library/Caches/Madego"),Path.Combine(home,"Library/Logs/Madego")):
            new MacPaths(Path.GetFullPath(testRoot),Path.Combine(Path.GetFullPath(testRoot),"Caches"),Path.Combine(Path.GetFullPath(testRoot),"Logs"));
        PrivateFiles.Directory(paths.Support);PrivateFiles.Directory(paths.Cache);PrivateFiles.Directory(paths.Logs);return paths;
    }
}
