using System.Numerics;
namespace Madyar.PeerChat;
/// <summary>Standard libp2p Ed25519 protobuf key, identity multihash, base58btc.</summary>
public static class PeerIdCodec {
    const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    public static string FromPublicKey(ReadOnlySpan<byte> key) {
        if (key.Length != 32) throw new InvalidDataException("Неверный открытый ключ.");
        byte[] data = new byte[38];
        new byte[] { 0, 36, 8, 1, 18, 32 }.CopyTo(data, 0);
        key.CopyTo(data.AsSpan(6));
        var number = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        string result = "";
        while (number > 0) { number = BigInteger.DivRem(number, 58, out var rest); result = Alphabet[(int)rest] + result; }
        return "1" + result; // Exactly one leading zero in this fixed encoding.
    }
    public static bool Matches(string id, ReadOnlySpan<byte> key) => key.Length == 32 && string.Equals(id, FromPublicKey(key), StringComparison.Ordinal);
}
