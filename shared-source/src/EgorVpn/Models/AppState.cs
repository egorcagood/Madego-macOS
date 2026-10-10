namespace EgorVpn.Models;

// Additive schema: the original 0.1–0.3 state file remains readable.
public sealed class AppState
{
    public List<SavedSubscription> Subscriptions { get; set; } = [];
    public string? LastNodeRawUri { get; set; }
    public string? SelectedGroupId { get; set; }
    public string SelectedCountry { get; set; } = "*";
    public List<string> FavoriteNodeKeys { get; set; } = [];
}

public sealed class SavedSubscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Subscription";
    public string Url { get; set; } = "";
    public List<VpnNode> CachedNodes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public long? TrafficUsedBytes { get; set; }
    public long? TrafficTotalBytes { get; set; }
}
