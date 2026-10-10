using System;

namespace EgorVpn.Routing;

public enum DomainMatch { Exact = 1, Suffix = 2 }
public enum DomainRoute { Direct = 1, Proxy = 2 }
public sealed record DomainRule(DomainMatch Match, string Value, DomainRoute Route)
{
    public bool Matches(string host) => DomainNames.TryNormalize(host, out var name, out _) && MatchesNormalized(name);
    internal bool MatchesNormalized(string host) => string.Equals(host, Value, StringComparison.Ordinal) ||
        (Match == DomainMatch.Suffix && host.EndsWith("." + Value, StringComparison.Ordinal));

    public bool Overlaps(DomainRule other)
    {
        if (other == null) return false;
        return MatchesNormalized(other.Value) || other.MatchesNormalized(Value);
    }
    public bool ConflictsWith(DomainRule other) => other != null && Route != other.Route && Overlaps(other);
}
