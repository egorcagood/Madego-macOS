using System;
using System.Collections.Generic;
using System.Linq;

namespace EgorVpn.Routing;

// One immutable domain policy is consumed by both route and DNS generation.
public sealed class RoutingPolicySnapshot
{
    public RoutingMode Mode { get; }
    public string CatalogVersion { get; }
    public IReadOnlyList<DomainRule> ProxyRules { get; }
    public IReadOnlyList<DomainRule> DirectRules { get; }
    public string? Warning { get; }

    private RoutingPolicySnapshot(RoutingMode mode, RoutingCatalog catalog, DomainRule[] proxyRules, DomainRule[] directRules)
    {
        Mode = mode; CatalogVersion = catalog.Version; Warning = catalog.Warning;
        ProxyRules = Array.AsReadOnly(proxyRules); DirectRules = Array.AsReadOnly(directRules);
    }

    public static RoutingPolicySnapshot Create(RoutingMode mode, RuRoutingPreferences? preferences, RoutingCatalog? catalog = null)
    {
        catalog ??= RoutingCatalog.LoadEmbedded();
        mode = mode == RoutingMode.RuMode ? RoutingMode.RuMode : RoutingMode.FullVpn;
        if (preferences == null && mode == RoutingMode.RuMode)
            throw new ArgumentException("RU Mode requires valid routing preferences.", nameof(preferences));
        preferences ??= new();
        var validation = preferences.Validate();
        if (validation != null) throw new ArgumentException(validation, nameof(preferences));
        preferences = preferences.Copy();
        var proxy = catalog.VpnOnlyServices.SelectMany(s => s.Rules)
            .Concat(preferences.CustomRules.Where(r => r.Route == DomainRoute.Proxy)).Distinct().ToArray();
        var disabled = new HashSet<string>(preferences.DisabledServiceIds, StringComparer.Ordinal);
        var direct = mode == RoutingMode.RuMode && catalog.IsValid
            ? preferences.CustomRules.Where(r => r.Route == DomainRoute.Direct)
                .Concat(catalog.Services.Where(s => s.EnabledInRuPreset && !disabled.Contains(s.Id)).SelectMany(s => s.Rules)).Distinct().ToArray()
            : Array.Empty<DomainRule>();
        return new(mode, catalog, proxy, direct);
    }

    public DomainRoute MatchHost(string host)
    {
        if (!DomainNames.TryNormalize(host, out var name, out _)) return DomainRoute.Proxy;
        if (ProxyRules.Any(rule => rule.MatchesNormalized(name))) return DomainRoute.Proxy;
        return DirectRules.Any(rule => rule.MatchesNormalized(name)) ? DomainRoute.Direct : DomainRoute.Proxy;
    }

    public IReadOnlyList<DomainRule> FindConflicts(DomainRule rule) => Array.AsReadOnly(
        (rule.Route == DomainRoute.Direct ? ProxyRules : DirectRules).Where(existing => rule.ConflictsWith(existing)).ToArray());
}
