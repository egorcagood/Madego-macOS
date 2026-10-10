namespace EgorVpn.Models;

public sealed class SessionRecord
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public string ServerName { get; set; } = "";
    public string Country { get; set; } = "";
    public long? ReceivedBytes { get; set; }
    public long? SentBytes { get; set; }
    public string DateText => StartedAt.ToLocalTime().ToString("dd.MM HH:mm");
    public string DurationText => TimeSpan.FromSeconds(Math.Max(0, (EndedAt - StartedAt).TotalSeconds)).ToString(@"hh\:mm\:ss");
    public string DownloadText => Services.DisplayFormat.Bytes(ReceivedBytes);
    public string UploadText => Services.DisplayFormat.Bytes(SentBytes);
}
public sealed class StatisticsState
{
    public List<SessionRecord> Sessions { get; set; } = [];
}
