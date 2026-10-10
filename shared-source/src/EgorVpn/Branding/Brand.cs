using System.Reflection;
using System.Text;

namespace EgorVpn.Branding;

public static class Brand
{
    private static readonly Lazy<BrandProfile> Profile = new(Load);
    public static BrandProfile Current => Profile.Value;
    public static string Version => typeof(Brand).Assembly.GetName().Version?.ToString(3) ?? "0.19.2";
    public static string AssemblyName => typeof(Brand).Assembly.GetName().Name ?? "MadyarVPN";

    private static BrandProfile Load()
    {
        using var stream = typeof(Brand).Assembly.GetManifestResourceStream("Madyar.BrandProfile.json")
            ?? throw new InvalidOperationException("В сборке отсутствует профиль бренда.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return BrandProfile.FromJson(reader.ReadToEnd());
    }
}
