using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Messenger.Identity;

namespace MessengerDesktop;

public sealed class ServerIdentityException(string message, Exception? cause = null) : Exception(message, cause);

public sealed partial class MessengerClient
{
    private readonly SemaphoreSlim _identityGate = new(1);
    private readonly string? _expectedFingerprint;
    private bool _identityVerified;

    public string? ServerFingerprint { get; private set; }

    internal static string NormalizeServerAddress(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Host.Length == 0
            || (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !IsLoopback(uri))))
            throw new ServerIdentityException("Укажите HTTPS-адрес сервера. HTTP доступен только для локального сервера.");

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IdnHost == "localhost" || (IPAddress.TryParse(uri.IdnHost, out var address) && IPAddress.IsLoopback(address));

    private async Task VerifyServerIdentity(bool refresh, CancellationToken ct)
    {
        await _identityGate.WaitAsync(ct);
        try
        {
            if (!refresh && _identityVerified) return;
            _identityVerified = false;

            var nonce = RandomNumberGenerator.GetBytes(ServerIdentityProof.NonceSize);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_http.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerIdentityProof.Path)
            {
                Content = JsonContent.Create(new { nonce = Convert.ToBase64String(nonce) })
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Server identity proof temporarily unavailable.", null, response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new ServerIdentityException("Сервер не подтвердил свою подлинность. Подключение заблокировано.");

            var publicKey = await ReadProof(response, nonce, timeout.Token);
            ServerFingerprint = ServerTrustStore.Accept(ServerBase, publicKey, ServerFingerprint ?? _expectedFingerprint);
            _identityVerified = true;
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private static async Task<byte[]> ReadProof(HttpResponseMessage response, byte[] nonce, CancellationToken ct)
    {
        try
        {
            var bytes = new byte[2049];
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var size = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
            if (size == bytes.Length) throw new JsonException("Server proof is too large");
            var proof = JsonSerializer.Deserialize<Proof>(bytes.AsSpan(0, size), Json)
                ?? throw new JsonException("Empty server proof");
            if (proof.PublicKey is null || proof.Signature is null) throw new JsonException("Missing server proof");

            var publicKey = Convert.FromBase64String(proof.PublicKey);
            var signature = Convert.FromBase64String(proof.Signature);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var used);
            if (used != publicKey.Length || signature.Length != 64 ||
                verifier.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                !verifier.VerifyData(ServerIdentityProof.Payload(nonce), signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException("Invalid server proof signature");
            return verifier.ExportSubjectPublicKeyInfo();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            throw new ServerIdentityException("Не удалось проверить подпись сервера. Подключение заблокировано.", ex);
        }
    }

    private sealed record Proof([property: JsonPropertyName("public_key")] string? PublicKey, string? Signature);

    private async Task<HttpResponseMessage> SendRequest(HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var path = request.RequestUri?.OriginalString;
        var target = new Uri(_http.BaseAddress!, request.RequestUri!);
        if (!_http.BaseAddress!.IsBaseOf(target))
            throw new ArgumentException("The request is outside the server API.", nameof(request));
        await VerifyServerIdentity(path is "auth/login" or "auth/register", ct);
        if (Token is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, completion, ct);
    }

    internal async Task ConnectAuthenticatedSocket(ClientWebSocket socket, Uri uri, CancellationToken ct)
    {
        var httpUri = new UriBuilder(uri) { Scheme = uri.Scheme == "wss" ? "https" : "http" }.Uri;
        if (uri.Scheme is not ("ws" or "wss") || uri.UserInfo.Length != 0 || !_http.BaseAddress!.IsBaseOf(httpUri))
            throw new ArgumentException("The socket is outside the server API.", nameof(uri));
        await VerifyServerIdentity(refresh: true, ct);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + Token);
        await socket.ConnectAsync(uri, _http, ct);
    }
}
