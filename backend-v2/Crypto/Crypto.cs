using System.Security.Cryptography;
using System.Text;

namespace Messenger.Api.Infrastructure;

public static class Crypto
{
    private const int IvSize = 16;

    private static readonly byte[] EncKey;
    private static readonly byte[] MacKey;

    static Crypto()
    {
        var master = LoadMasterKey();
        EncKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("messenger:enc"));
        MacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("messenger:mac"));
    }

    public static string? Encrypt(string? text)
    {
        if (text is null) return null;

        var data = Encoding.UTF8.GetBytes(text);
        var iv = HMACSHA256.HashData(MacKey, data)[..IvSize];

        using var aes = Aes.Create();
        aes.Key = EncKey;
        var cipher = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);

        var result = new byte[IvSize + cipher.Length];
        iv.CopyTo(result, 0);
        cipher.CopyTo(result, IvSize);
        return Convert.ToBase64String(result);
    }

    public static string? Decrypt(string? b64)
    {
        if (b64 is null) return null;

        byte[] raw;
        try { raw = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new CryptographicException("Encrypted value is not valid base64"); }

        if (raw.Length < IvSize + 16)
            throw new CryptographicException("Encrypted value is too short");

        var iv = raw[..IvSize];
        var cipher = raw[IvSize..];

        using var aes = Aes.Create();
        aes.Key = EncKey;
        var data = aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7);

        var expectedIv = HMACSHA256.HashData(MacKey, data)[..IvSize];
        if (!CryptographicOperations.FixedTimeEquals(iv, expectedIv))
            throw new CryptographicException("Encrypted value failed integrity check");

        return Encoding.UTF8.GetString(data);
    }

    private static byte[] LoadMasterKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MESSENGER_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var key = Convert.FromBase64String(fromEnv);
            if (key.Length != 32)
                throw new InvalidOperationException("MESSENGER_KEY must be 32 bytes in base64");
            return key;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "app.key");
        if (!File.Exists(path))
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 32)
            throw new InvalidOperationException("app.key must contain exactly 32 bytes");
        return bytes;
    }
}
