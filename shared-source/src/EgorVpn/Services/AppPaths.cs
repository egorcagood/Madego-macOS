using EgorVpn.Branding;

namespace EgorVpn.Services;

public static class AppPaths
{
    public static string ProductName => Brand.Current.ProductName;
    public static string ExecutableName => Brand.Current.ExecutableName + ".exe";
    public static string TunInterfaceName => Brand.Current.TunName;

    private static string? _startupProbeDataRoot;
    internal static void ConfigureStartupProbeData(string root) => _startupProbeDataRoot=Path.GetFullPath(root);
    public static string LocalAppData => _startupProbeDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string DataDirectory => Path.Combine(LocalAppData, Brand.Current.DataFolder);
    public static string StatePath => Path.Combine(DataDirectory, "state.json");
    public static string RuntimeDirectory => Path.Combine(DataDirectory, "runtime");
    public static string DeviceIdPath => Path.Combine(DataDirectory, "device-id.txt");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string StatisticsPath => Path.Combine(DataDirectory, "statistics.json");
    public static string StartupLogPath => Path.Combine(DataDirectory, "startup-error.log");

    public static void EnsureMigrated() => EnsureMigrated(LocalAppData);

    internal static void EnsureMigrated(string localAppData, BrandProfile? profile = null)
    {
        var brand = profile ?? Brand.Current;
        brand.Validate();
        var destinationRoot = Path.Combine(localAppData, brand.DataFolder);
        Directory.CreateDirectory(destinationRoot);
        if (!brand.MigrateLegacyData) return; // A provider build must not inherit personal subscriptions.

        // v0.1-v0.4 kept the subscription state separately from settings/device identity.
        // Copy only when the new destination is missing: never overwrite newer Madyar VPN data.
        CopyIfMissing(Path.Combine(localAppData, "FuseVPN", "state.json"), Path.Combine(destinationRoot, "state.json"));
        CopyIfMissing(Path.Combine(localAppData, "FuseVPN", "state.json.bak"), Path.Combine(destinationRoot, "state.json.bak"));
        CopyIfMissing(Path.Combine(localAppData, "EgorMadyarVPN", "device-id.txt"), Path.Combine(destinationRoot, "device-id.txt"));
        CopyIfMissing(Path.Combine(localAppData, "EgorMadyarVPN", "settings.json"), Path.Combine(destinationRoot, "settings.json"));
        CopyIfMissing(Path.Combine(localAppData, "EgorMadyarVPN", "settings.json.bak"), Path.Combine(destinationRoot, "settings.json.bak"));
        CopyIfMissing(Path.Combine(localAppData, "EgorMadyarVPN", "statistics.json"), Path.Combine(destinationRoot, "statistics.json"));
        CopyIfMissing(Path.Combine(localAppData, "EgorMadyarVPN", "statistics.json.bak"), Path.Combine(destinationRoot, "statistics.json.bak"));
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (File.Exists(destination) || !File.Exists(source)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
    }
}
