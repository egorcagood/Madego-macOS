namespace Madyar.PeerChat;
public sealed record PeerProfile(string PeerId, byte[] PublicKey, string Nickname);
public interface IIdentityProtector {
    byte[] Protect(byte[] bytes);
    byte[] Unprotect(byte[] bytes);
}
public sealed record PeerContact(string PeerId, byte[] PublicKey, string Nickname, string? Alias, DateTimeOffset AddedUtc, DateTimeOffset? LastSeenUtc);
public sealed class VerifiedContactInvite {
    readonly byte[] _key;
    internal string? SignedUri { get; }
    public string PeerId { get; }
    public byte[] PublicKey => _key.ToArray();
    public string Nickname { get; }
    public Guid InviteId { get; }
    public DateTimeOffset CreatedUtc { get; }
    internal VerifiedContactInvite(string peerId, byte[] key, string nickname, Guid inviteId, DateTimeOffset createdUtc, string? signedUri=null) { SignedUri=signedUri; PeerId=peerId; _key=key.ToArray(); Nickname=nickname; InviteId=inviteId; CreatedUtc=createdUtc; }
}

public sealed record PeerBlockedContact(string PeerId, byte[] PublicKey, string Nickname, DateTimeOffset BlockedUtc);
