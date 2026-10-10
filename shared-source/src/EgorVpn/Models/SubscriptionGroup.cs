using System.Collections.ObjectModel;

namespace EgorVpn.Models;

public sealed class SubscriptionGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Subscription";
    public string Url { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public long? TrafficUsedBytes { get; set; }
    public long? TrafficTotalBytes { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public ObservableCollection<VpnNode> Nodes { get; set; } = new();

    public string UsageText
    {
        get
        {
            if (TrafficTotalBytes is null || TrafficTotalBytes <= 0) return $"{Nodes.Count} nodes";
            var used = TrafficUsedBytes ?? 0;
            return $"{FormatBytes(used)} / {FormatBytes(TrafficTotalBytes.Value)}";
        }
    }

    public string ExpiryText => ExpiresAt is null ? "" : $"до {ExpiresAt:dd.MM.yyyy}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return $"{value:0.#} {units[i]}";
    }
}
