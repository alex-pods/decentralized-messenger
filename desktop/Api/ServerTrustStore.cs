using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Messenger.Identity;

namespace MessengerDesktop;

internal static class ServerTrustStore
{
    internal static string? TestFilePath { get; set; }

    private static string FilePath => TestFilePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "messenger-desktop", "server-trust.json");

    internal static string? Lookup(string address) =>
        Read().TryGetValue(address, out var key) ? KeyFingerprint(key) : null;

    internal static string[] AddressesFor(string fingerprint) =>
        Read().Where(pin => KeyFingerprint(pin.Value) == fingerprint).Select(pin => pin.Key).ToArray();

    internal static string Accept(string address, byte[] publicKey, string? expected)
    {
        var fingerprint = ServerIdentityProof.Fingerprint(publicKey);
        var path = Path.GetFullPath(FilePath);
        var lockPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        using var gate = new Mutex(false, "MessengerDesktop.ServerTrust." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockPath))));
        try
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(3)))
                throw StoreError(new TimeoutException("Server trust file is busy"));
        }
        catch (AbandonedMutexException)
        {
            // The previous writer may have stopped before the atomic replace.
        }
        try
        {
            var pins = Read();
            var known = pins.TryGetValue(address, out var key) ? KeyFingerprint(key) : null;
            if ((expected != null && expected != fingerprint) || (known != null && known != fingerprint))
                throw new ServerIdentityException("Ключ сервера изменился. Подключение заблокировано.");

            if (known == null)
            {
                pins.Add(address, Convert.ToBase64String(publicKey));
                try
                {
                    WriteJson(path, pins);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw StoreError(ex);
                }
            }
        }
        finally
        {
            gate.ReleaseMutex();
        }
        return fingerprint;
    }

    private static Dictionary<string, string> Read()
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new JsonException("Missing server pins");
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw StoreError(ex);
        }
    }

    private static string KeyFingerprint(string? key)
    {
        try
        {
            if (string.IsNullOrEmpty(key)) throw new FormatException("Missing public key");
            var der = Convert.FromBase64String(key);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(der, out var used);
            if (used != der.Length) throw new FormatException("Invalid public key");
            return ServerIdentityProof.Fingerprint(der);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw StoreError(ex);
        }
    }

    private static ServerIdentityException StoreError(Exception cause) =>
        new("Не удалось прочитать или сохранить доверенный ключ сервера. Подключение заблокировано.", cause);

    internal static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
