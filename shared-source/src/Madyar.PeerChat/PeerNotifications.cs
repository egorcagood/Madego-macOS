namespace Madyar.PeerChat;
public readonly record struct PeerAcceptance(bool Accepted,bool Created);
public sealed record PeerIncomingActivity(string PeerId,string Id,string Kind,string Nickname,long Epoch);
public sealed partial class PeerWorkspace
{
    public Task<bool> ReadNotificationsEnabledAsync(CancellationToken ct=default)=>Locked(()=>Messages.NotificationsEnabled(),ct);
    public Task SetNotificationsEnabledAsync(bool enabled,CancellationToken ct=default)=>Locked(()=>{Messages.SetNotifications(enabled);return true;},ct);
}
