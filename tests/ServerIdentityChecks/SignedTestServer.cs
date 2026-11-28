using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

internal abstract class SignedTestServer : HttpMessageHandler
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private byte[]? _lastSignature;

    internal bool Replay { get; set; }
    internal bool MissingProof { get; set; }
    internal int ProofRequests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/identity/proof", StringComparison.Ordinal))
            return await SendVerified(request, ct);

        ProofRequests++;
        if (request.Headers.Authorization != null) throw new InvalidOperationException("Credential leaked in proof request");
        if (MissingProof) return new HttpResponseMessage(HttpStatusCode.NotFound);

        using var challenge = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var nonce = challenge.RootElement.GetProperty("nonce").GetBytesFromBase64();
        if (nonce.Length != 32) throw new InvalidOperationException("Unexpected challenge size");
        var payload = "messenger-server-identity-v1\0"u8.ToArray().Concat(nonce).ToArray();
        if (!Replay)
            _lastSignature = _key.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                public_key = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
                signature = Convert.ToBase64String(_lastSignature!)
            })
        };
    }

    protected abstract Task<HttpResponseMessage> SendVerified(HttpRequestMessage request, CancellationToken ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _key.Dispose();
        base.Dispose(disposing);
    }
}
