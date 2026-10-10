using Madyar.Localization;
using EgorVpn.Branding;
using System.Collections.ObjectModel;
using EgorVpn.Models;
using EgorVpn.Services;

namespace EgorVpn.Presentation;

public sealed class NodeItem : ObservableObject
{
    public VpnNode Source { get; }
    public string GroupId { get; }
    public string GroupName { get; }
    public string Key => DisplayFormat.NodeKey(Source.RawUri);
    private readonly string? _displayName;
    public string OriginalName => Source.DisplayName;
    public string Name => _displayName ?? NodeLabelFormatter.ForNode(Source);
    public string Country => Source.Country;
    public string CountryCode => DisplayFormat.CountryCode(Source.CountryFlag);
    public string CountryLine => $"{Source.Country} · {GroupName}";
    public string ProtocolText => Source.Subtitle;
    public string Endpoint => DisplayFormat.Endpoint(Source.Host, Source.Port);
    public string PingText => Source.PingMs is null ? "—" : UiText.F("{0} мс", Source.PingMs);
    public string PingColor => Source.PingMs is null ? "#A2B1C7" : Source.PingMs > 300 ? "#DEB16D" : "#48C9A0";
    private bool _active, _favorite;
    public bool IsActive { get => _active; set => Set(ref _active, value); }
    public bool IsFavorite { get => _favorite; set => Set(ref _favorite, value); }
    public NodeItem(VpnNode source, string groupId, string groupName, string? displayName = null) { Source = source; GroupId = groupId; GroupName = groupName; _displayName = displayName; }
    public void SetPing(long? ping) { Source.PingMs = ping; RaiseMany(nameof(PingText), nameof(PingColor)); }
}

public sealed class SubscriptionItem : ObservableObject
{
    public SubscriptionGroup Source { get; private set; }
    public string Id => Source.Id;
    public string Name => Source.Name;
    public string Url => Source.Url;
    public ObservableCollection<NodeItem> Nodes { get; } = new();
    public string CountText => UiText.F("{0} серверов", Nodes.Count);
    public string ExpiryText => Source.ExpiresAt is null ? UiText.T("Срок не указан") : UiText.F("До {0:dd.MM.yyyy}", Source.ExpiresAt);
    public string UsageText => Source.TrafficTotalBytes is > 0
        ? UiText.F("{0} из {1}", InterfaceFormat.Bytes(Source.TrafficUsedBytes), InterfaceFormat.Bytes(Source.TrafficTotalBytes))
        : Source.TrafficUsedBytes is not null ? UiText.F("Использовано: {0}", InterfaceFormat.Bytes(Source.TrafficUsedBytes)) : UiText.T("Трафик не указан");
    public double UsagePercent => Source.TrafficTotalBytes is > 0 ? Math.Clamp(100d * (Source.TrafficUsedBytes ?? 0) / Source.TrafficTotalBytes.Value, 0, 100) : 0;
    public string PercentText => HasQuota ? $"{UsagePercent:0}%" : "";
    public bool HasQuota => Source.TrafficTotalBytes is > 0 && Source.TrafficUsedBytes is not null;
    public string UpdatedText => Source.UpdatedAt is null ? UiText.T("Ещё не обновлялась") : UiText.F("Обновлено {0:dd.MM HH:mm}", Source.UpdatedAt);
    private bool _active, _refreshing;
    private string? _error;
    public bool IsActive { get => _active; set { if (Set(ref _active, value)) RaiseMany(nameof(StatusText), nameof(StatusColor)); } }
    public bool IsRefreshing { get => _refreshing; set { if (Set(ref _refreshing, value)) RaiseMany(nameof(StatusText), nameof(StatusColor)); } }
    public string? Error { get => _error; set { if (Set(ref _error, value)) RaiseMany(nameof(StatusText), nameof(StatusColor)); } }
    public string StatusText => IsRefreshing ? UiText.T("Обновление…") : IsActive ? UiText.T("Подключена") : Error is not null ? UiText.T("Есть ошибка") : Source.ExpiresAt < DateTimeOffset.Now ? UiText.T("Истекла") : UiText.T("Готова");
    public string StatusColor => IsRefreshing ? Brand.Current.Accent : IsActive ? "#48C9A0" : Error is not null || Source.ExpiresAt < DateTimeOffset.Now ? "#DEB16D" : "#A2B1C7";
    public SubscriptionItem(SubscriptionGroup source) { Source = source; BuildNodes(); }
    public void Replace(SubscriptionGroup fresh)
    {
        fresh.Id = Id; Source = fresh; Error = null; BuildNodes();
        RaiseMany(nameof(Name),nameof(Url),nameof(CountText),nameof(ExpiryText),nameof(UsageText),nameof(UsagePercent),nameof(PercentText),nameof(HasQuota),nameof(UpdatedText),nameof(StatusText),nameof(StatusColor));
    }
    private void BuildNodes()
    {
        Nodes.Clear();
        var labels = Source.Nodes.Select(NodeLabelFormatter.ForNode).ToArray();
        var counts = labels.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Reserve provider labels before assigning ordinals, including later rows.
        var reserved = new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Source.Nodes.Count; i++)
        {
            var label = labels[i];
            if (counts[label] > 1)
            {
                ordinals.TryGetValue(label, out int ordinal);
                var originalLabel = label;
                do { label = NodeLabelFormatter.Distinguish(originalLabel, ++ordinal); }
                while (reserved.Contains(label) || used.Contains(label));
                ordinals[originalLabel] = ordinal;
            }
            used.Add(label);
            Nodes.Add(new NodeItem(Source.Nodes[i], Id, Name, label));
        }
    }
    public SavedSubscription ToSaved() => new()
    {
        Id = Id, Name = Name, Url = Url, CachedNodes = Nodes.Select(n => n.Source).ToList(),
        ExpiresAt = Source.ExpiresAt, UpdatedAt = Source.UpdatedAt,
        TrafficUsedBytes = Source.TrafficUsedBytes, TrafficTotalBytes = Source.TrafficTotalBytes
    };
    public static SubscriptionItem FromSaved(SavedSubscription s) => new(new SubscriptionGroup
    {
        Id = s.Id, Name = s.Name, Url = s.Url,
        Nodes = new ObservableCollection<VpnNode>(s.CachedNodes ?? []),
        ExpiresAt = s.ExpiresAt, UpdatedAt = s.UpdatedAt,
        TrafficUsedBytes = s.TrafficUsedBytes, TrafficTotalBytes = s.TrafficTotalBytes
    });
}

public sealed record CountryItem(string Name, string Code, int Count)
{
    public bool IsSelected { get; set; }
    public string Label => Name == "*" ? "AUTO" : Name;
    public string CountText => Count > 0 ? Count.ToString() : "";
}

public sealed record LogItem(string Time, string Text, string Color);
