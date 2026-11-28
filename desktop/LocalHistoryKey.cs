using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessengerDesktop;

internal sealed class LocalHistoryKey
{
    const int NonceSize = 12;
    const int TagSize = 16;
    private readonly byte[] _key;
    private readonly string _scope;

    internal LocalHistoryKey(string keyPath, string scope)
    {
        _scope = scope;
        _key = ReadOrCreate(keyPath);
        if (_key.Length != 32) throw new CryptographicException("Invalid local history key.");
    }

    internal byte[] Encrypt<T>(string slot, T value)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, MessengerClient.Json);
        try
        {
            var data = new byte[NonceSize + TagSize + plaintext.Length];
            RandomNumberGenerator.Fill(data.AsSpan(0, NonceSize));
            using var cipher = new AesGcm(_key, TagSize);
            cipher.Encrypt(data.AsSpan(0, NonceSize), plaintext, data.AsSpan(NonceSize + TagSize),
                data.AsSpan(NonceSize, TagSize), Context(slot));
            return data;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal T Decrypt<T>(string slot, byte[] data)
    {
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("Invalid local history record.");
        var plaintext = new byte[data.Length - NonceSize - TagSize];
        try
        {
            using var cipher = new AesGcm(_key, TagSize);
            cipher.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize),
                data.AsSpan(NonceSize, TagSize), plaintext, Context(slot));
            return JsonSerializer.Deserialize<T>(plaintext, MessengerClient.Json)
                ?? throw new JsonException("Empty local history record.");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Context(string slot) => Encoding.UTF8.GetBytes("messenger-desktop-history-v1\0" + _scope + "\0" + slot);

    private static byte[] ReadOrCreate(string path)
    {
        byte[] Load()
        {
            var stored = File.ReadAllBytes(path);
            return OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser) : stored;
        }

        try { return Load(); }
        catch (FileNotFoundException) { }

        var key = RandomNumberGenerator.GetBytes(32);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                file.Write(OperatingSystem.IsWindows() ? ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser) : key);
                file.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporary, path);
                return key;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(key);
                return Load();
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally { File.Delete(temporary); }
    }
}
