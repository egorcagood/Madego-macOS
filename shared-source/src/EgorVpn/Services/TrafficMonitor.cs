using System.Net.NetworkInformation;

namespace EgorVpn.Services;

public sealed record TrafficSample(bool Available, long ReceivedBytes, long SentBytes, double DownloadRate, double UploadRate);

// Counters come from the Windows TUN NIC. They are not subscription billing counters.
public sealed class TrafficAccumulator
{
    private long? _lastRx, _lastTx;
    private DateTimeOffset? _lastAt;
    public long ReceivedBytes { get; private set; }
    public long SentBytes { get; private set; }
    public TrafficSample Add(long rx, long tx, DateTimeOffset at)
    {
        var seconds = Math.Max(0.001, _lastAt is null ? 1 : (at - _lastAt.Value).TotalSeconds);
        var dr = _lastRx is null ? 0 : Math.Max(0, rx - _lastRx.Value);
        var dt = _lastTx is null ? 0 : Math.Max(0, tx - _lastTx.Value);
        ReceivedBytes += dr; SentBytes += dt;
        _lastRx = rx; _lastTx = tx; _lastAt = at;
        return new TrafficSample(true, ReceivedBytes, SentBytes, dr / seconds, dt / seconds);
    }
}

public sealed class TrafficMonitor
{
    private readonly object _gate = new();
    private TrafficAccumulator _accumulator = new();
    private string? _interfaceId;
    public void Reset() { lock(_gate) { _accumulator = new(); _interfaceId = null; } }
    public TrafficSample Read() { lock(_gate) return ReadCore(); }
    private TrafficSample ReadCore()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.Name.Equals(AppPaths.TunInterfaceName, StringComparison.OrdinalIgnoreCase));
            if (nic is null) return Unavailable();
            if (_interfaceId != nic.Id) { _interfaceId = nic.Id; _accumulator = new(); }
            var counters = nic.GetIPStatistics();
            return _accumulator.Add(counters.BytesReceived, counters.BytesSent, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return Unavailable(); }
    }
    private TrafficSample Unavailable() => new(false, _accumulator.ReceivedBytes, _accumulator.SentBytes, 0, 0);
}
