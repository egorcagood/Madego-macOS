using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
namespace Madyar.PeerChat;
internal static class PeerSigning {
    internal static byte[] PublicKey(byte[] seed) {
        if (seed.Length != 32) throw new InvalidDataException("Неверный размер ключа.");
        return new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();
    }
    internal static byte[] Sign(byte[] seed, byte[] bytes) {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.GenerateSignature();
    }
    internal static bool Verify(byte[] key, byte[] bytes, byte[] signature) {
        if (key.Length != 32 || signature.Length != 64) return false;
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(key, 0));
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.VerifySignature(signature);
    }
}
