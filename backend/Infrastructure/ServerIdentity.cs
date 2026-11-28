using System.Security.Cryptography;
using Messenger.Identity;

namespace Messenger.Api.Infrastructure;

internal sealed class ServerIdentity : IDisposable
{
    private readonly ECDsa _signer;

    internal ServerIdentity(string keyPath)
    {
        keyPath = Path.GetFullPath(keyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        using var candidate = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] privateKey;
        try
        {
            privateKey = File.ReadAllBytes(keyPath);
        }
        catch (FileNotFoundException)
        {
            privateKey = CreateOrRead(keyPath, candidate);
        }

        try
        {
            candidate.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length ||
                candidate.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new CryptographicException("Invalid server identity key.");
            PublicKey = Convert.ToBase64String(candidate.ExportSubjectPublicKeyInfo());
            _signer = ECDsa.Create();
            _signer.ImportPkcs8PrivateKey(privateKey, out _);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    internal string PublicKey { get; }

    internal string Sign(byte[] nonce)
    {
        lock (_signer)
        {
            return Convert.ToBase64String(_signer.SignData(
                ServerIdentityProof.Payload(nonce), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    private static byte[] CreateOrRead(string path, ECDsa signer)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        var key = signer.ExportPkcs8PrivateKey();
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporary, path);
                return key;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(key);
                return File.ReadAllBytes(path);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Dispose() => _signer.Dispose();
}
