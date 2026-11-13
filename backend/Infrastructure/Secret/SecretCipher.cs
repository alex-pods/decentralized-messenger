using System.Security.Cryptography;
using System.Text;

namespace Messenger.Api.Infrastructure;

public sealed class SecretCipher
{
  private const int IvSize = 16;

  private readonly byte[] encKey;
  private readonly byte[] macKey;

  public SecretCipher()
  {
    var master = LoadMasterKey();
    encKey = DeriveKey(master, "messenger:enc");
    macKey = DeriveKey(master, "messenger:mac");
  }

  public string Seal(string text)
  {
    var data = Encoding.UTF8.GetBytes(text);
    var iv = HMACSHA256.HashData(macKey, data)[..IvSize];

    using var aes = Aes.Create();
    aes.Key = encKey;
    var cipher = aes.EncryptCbc(data, iv);

    return Convert.ToBase64String(iv.Concat(cipher).ToArray());
  }

  public string Open(string b64)
  {
    byte[] raw;
    try { raw = Convert.FromBase64String(b64); }
    catch (FormatException) { throw new CryptographicException("Encrypted value is not valid base64"); }

    if (raw.Length < IvSize + 16)
      throw new CryptographicException("Encrypted value is too short");

    var iv = raw[..IvSize];

    using var aes = Aes.Create();
    aes.Key = encKey;
    var data = aes.DecryptCbc(raw[IvSize..], iv);

    if (!CryptographicOperations.FixedTimeEquals(iv, HMACSHA256.HashData(macKey, data)[..IvSize]))
      throw new CryptographicException("Encrypted value failed integrity check");

    return Encoding.UTF8.GetString(data);
  }

  private static byte[] DeriveKey(byte[] master, string purpose) =>
      HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes(purpose));

  private static byte[] LoadMasterKey()
  {
    var fromEnv = Environment.GetEnvironmentVariable("MESSENGER_KEY");
    if (!string.IsNullOrWhiteSpace(fromEnv))
      return ValidKey(Convert.FromBase64String(fromEnv), "MESSENGER_KEY");

    var path = Path.Combine(AppContext.BaseDirectory, "app.key");
    if (!File.Exists(path))
      File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));

    return ValidKey(File.ReadAllBytes(path), "app.key");
  }

  private static byte[] ValidKey(byte[] key, string source) =>
      key.Length == 32 ? key : throw new InvalidOperationException($"{source} must contain exactly 32 bytes");
}
