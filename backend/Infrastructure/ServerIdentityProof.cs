using System.Security.Cryptography;

namespace Messenger.Identity;

internal static class ServerIdentityProof
{
    internal const string Path = "identity/proof";
    internal const int NonceSize = 32;

    internal static bool TryReadNonce(string? value, out byte[] nonce)
    {
        nonce = new byte[NonceSize];
        return value is { Length: 44 }
            && Convert.TryFromBase64String(value, nonce, out var count)
            && count == NonceSize
            && Convert.ToBase64String(nonce) == value;
    }

    internal static byte[] Payload(ReadOnlySpan<byte> nonce)
    {
        var domain = "messenger-server-identity-v1\0"u8;
        var payload = new byte[domain.Length + nonce.Length];
        domain.CopyTo(payload);
        nonce.CopyTo(payload.AsSpan(domain.Length));
        return payload;
    }

    internal static string Fingerprint(ReadOnlySpan<byte> publicKey) =>
        Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
}
