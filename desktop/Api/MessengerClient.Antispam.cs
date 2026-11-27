namespace MessengerDesktop;

public sealed record AntispamCapability(bool Enabled, int Protocol);
public sealed record ServerCapabilities(AntispamCapability? Antispam);
public sealed record AntispamStatus(bool Enabled, bool Required);
public sealed record AntispamChallenge(string Id, int CountdownMs, int DurationMs, int RequiredScore);

public sealed partial class MessengerClient
{
    int _antispamBlocked;
    public bool AntispamBlocked => Volatile.Read(ref _antispamBlocked) != 0;
    public bool AntispamSupported { get; private set; }
    public event Action<bool>? AntispamChanged;

    void SetAntispamBlocked(bool blocked)
    {
        if (Interlocked.Exchange(ref _antispamBlocked, blocked ? 1 : 0) != (blocked ? 1 : 0)) AntispamChanged?.Invoke(blocked);
    }

    internal static bool IsMessageMutation(HttpMethod method, string path) =>
        method == HttpMethod.Post && (path.StartsWith("chats/") && path.EndsWith("/messages") || path.StartsWith("messages/") && (path.EndsWith("/forward") || path.EndsWith("/attachments"))) ||
        method == HttpMethod.Patch && path.StartsWith("messages/");

    public async Task DiscoverAntispam(CancellationToken ct = default)
    {
        try
        {
            var capabilities = await Send<ServerCapabilities>(HttpMethod.Get, "capabilities", null, ct);
            // Missing /capabilities, missing property and an omitted enabled flag all mean off.
            AntispamSupported = capabilities?.Antispam is { Enabled: true, Protocol: 1 };
            if (AntispamSupported) await RefreshAntispam(ct);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { /* Legacy servers keep working. A received antispam_required is never cleared by a failed discovery. */ }
    }

    public async Task RefreshAntispam(CancellationToken ct = default)
    {
        var status = await Send<AntispamStatus>(HttpMethod.Get, "antispam/status", null, ct);
        if (status != null) SetAntispamBlocked(status.Enabled && status.Required);
    }

    public async Task<AntispamChallenge> CreateAntispamChallenge(CancellationToken ct = default) =>
        await Send<AntispamChallenge>(HttpMethod.Post, "antispam/challenges", new { }, ct)
        ?? throw new ApiException(500, "Сервер не вернул проверку");

    public Task AbortAntispamChallenge(string id, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, "antispam/challenges/" + Uri.EscapeDataString(id), null, ct);

    public Uri AntispamWebSocketUri(string id)
    {
        var uri = new Uri(ServerBase + "/antispam/challenges/" + Uri.EscapeDataString(id) + "/play");
        return new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" }.Uri;
    }
}
