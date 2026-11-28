using System.Text.Json.Serialization;

namespace MessengerDesktop;

public sealed partial class MessengerClient
{
    public bool RelaySupported { get; private set; }
    public event Action? RelayCapabilityChanged;

    private void SetRelaySupported(bool supported)
    {
        if (RelaySupported == supported) return;
        RelaySupported = supported;
        RelayCapabilityChanged?.Invoke();
    }

    internal async Task<RelayReceipt> RelayMessage(string chatId, string id, string text, CancellationToken ct = default) =>
        await Send<RelayReceipt>(HttpMethod.Post, $"chats/{chatId}/messages/relay",
            new { message_id = id, text }, ct) ?? throw new ApiException(502, "Сервер не подтвердил отправку.");
}

internal sealed record RelayReceipt(
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("sent_at")] string SentAt,
    [property: JsonPropertyName("queued_connections")] int QueuedConnections);
