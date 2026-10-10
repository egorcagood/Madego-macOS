using System.Security.Cryptography;
namespace Madyar.PeerChat;
public sealed record PeerMessage(string Id, string PeerId, bool Outgoing, string Kind, string Text, DateTimeOffset CreatedUtc, string Status);
public sealed class PeerLinkCredentials : IDisposable {
    public string PeerId { get; }
    public byte[] Seed { get; }
    internal PeerLinkCredentials(string peerId, byte[] seed) { PeerId=peerId; Seed=seed; }
    public void Dispose() => CryptographicOperations.ZeroMemory(Seed);
}
