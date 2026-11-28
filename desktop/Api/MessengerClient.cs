using Messenger.Identifiers;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MessengerDesktop;

/// <summary>REST-клиент backend v4. Авторизация только через Authorization: Bearer.</summary>
public sealed partial class MessengerClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public string ServerBase { get; }
    public string? Token { get; private set; }

    public MessengerClient(string serverBase, string? token = null, HttpMessageHandler? handler = null,
        string? expectedFingerprint = null)
    {
        ServerBase = NormalizeServerAddress(serverBase);
        _expectedFingerprint = expectedFingerprint;
        var address = new Uri(ServerBase + "/");
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = address.Scheme == Uri.UriSchemeHttps
        })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(30),
            // На этом nginx HTTP/2 иногда рвёт TLS посередине ответа.
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        if (!string.IsNullOrWhiteSpace(token))
            SetToken(token);
    }

    public void SetToken(string token) => Token = token;

    public Uri WebSocketUri()
    {
        var http = new Uri(ServerBase + "/ws");
        return new UriBuilder(http)
        {
            Scheme = http.Scheme == "https" ? "wss" : "ws"
        }.Uri;
    }

    public Task Register(string login, string password, CancellationToken ct = default) =>
        Send(HttpMethod.Post, "auth/register", new Dictionary<string, object?>
        {
            ["login"] = login,
            ["password"] = password
        }, ct);

    public async Task<TokenDto> Login(string login, string password, CancellationToken ct = default)
    {
        var token = await Send<TokenDto>(HttpMethod.Post, "auth/login", new Dictionary<string, object?>
        {
            ["login"] = login,
            ["password"] = password
        }, ct) ?? throw new ApiException(500, "Пустой ответ login");
        SetToken(token.UserSessionToken);
        return token;
    }

    public Task Logout(CancellationToken ct = default) =>
        Send(HttpMethod.Post, "auth/logout", new Dictionary<string, object?>(), ct);

    public async Task<ProfileDto> GetMe(CancellationToken ct = default) =>
        await Send<ProfileDto>(HttpMethod.Get, "users/me", null, ct) ?? throw new ApiException(500, "Пустой профиль");

    public async Task<ProfileDto> GetUser(string id, CancellationToken ct = default) =>
        await Send<ProfileDto>(HttpMethod.Get, $"users/{id}", null, ct) ?? throw new ApiException(500, "Пустой профиль");

    public async Task<List<UserCardDto>> Search(string query, CancellationToken ct = default, bool prefix = false) =>
        await Send<List<UserCardDto>>(HttpMethod.Get,
            "users/search?query=" + Uri.EscapeDataString(query) + (prefix ? "&prefix=true" : ""),
            null, ct) ?? new();

    public Task UpdateProfile(string? displayName, string? bio, CancellationToken ct = default) =>
        UpdateProfile(displayName, bio, null, ct);

    public Task UpdateProfile(string? displayName, string? bio, bool? allowTagSearch, CancellationToken ct = default) =>
        Send(new HttpMethod("PATCH"), "users/me", new Dictionary<string, object?>
        {
            ["display_name"] = displayName,
            ["bio"] = bio,
            ["allow_tag_search"] = allowTagSearch
        }, ct);

    public async Task UpdateProfileWithAvatar(string displayName, string bio, byte[] photo, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(displayName), "display_name");
        content.Add(new StringContent(bio), "bio");
        content.Add(new ByteArrayContent(photo), "file", fileName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "users/me/profile") { Content = content };
        using var res = await SendRequest(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw ParseError(text, res.StatusCode);
    }

    public async Task<byte[]?> GetAvatar(string userId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"users/{userId}/avatar");
        using var res = await SendRequest(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        var data = await res.Content.ReadAsByteArrayAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw ParseError(Encoding.UTF8.GetString(data), res.StatusCode);
        return data;
    }

    public async Task<List<ChatDto>> GetChats(CancellationToken ct = default) =>
        await Send<List<ChatDto>>(HttpMethod.Get, "chats", null, ct) ?? new();

    public async Task<byte[]> GetAttachment(long attachmentId, CancellationToken ct = default)
    {
        const int maxBytes = 10 * 1024 * 1024;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_http.Timeout);
        ct = timeout.Token;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"attachments/{attachmentId}");
        using var res = await SendRequest(req, ct, HttpCompletionOption.ResponseHeadersRead);
        if (!res.IsSuccessStatusCode)
            throw ParseError(await res.Content.ReadAsStringAsync(ct), res.StatusCode);
        if (res.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Изображение больше 10 МБ");
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > maxBytes) throw new InvalidDataException("Изображение больше 10 МБ");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    public async Task<string> CreateDirect(string otherUserId, CancellationToken ct = default)
    {
        var dto = await Send<ChatIdDto>(HttpMethod.Post, "chats", new Dictionary<string, object?>
        {
            ["another_user_id"] = otherUserId,
            ["chat_id"] = EntityIds.NewChat()
        }, ct);
        return dto?.ChatId ?? throw new ApiException(500, "Сервер не вернул chat_id");
    }

    public async Task<string> CreateGroup(string title, IEnumerable<string> memberIds, CancellationToken ct = default)
    {
        var dto = await Send<ChatIdDto>(HttpMethod.Post, "chats/group", new Dictionary<string, object?>
        {
            ["title"] = title,
            ["member_ids"] = memberIds.ToArray(),
            ["chat_id"] = EntityIds.NewChat()
        }, ct);
        return dto?.ChatId ?? throw new ApiException(500, "Сервер не вернул chat_id");
    }

    public async Task<ChatDto> GetChat(string chatId, CancellationToken ct = default) =>
        await Send<ChatDto>(HttpMethod.Get, $"chats/{chatId}", null, ct) ?? throw new ApiException(500, "Пустой чат");

    public Task HideChat(string chatId, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"chats/{chatId}", new Dictionary<string, object?>(), ct);

    public async Task<List<MemberDto>> GetMembers(string chatId, CancellationToken ct = default) =>
        await Send<List<MemberDto>>(HttpMethod.Get, $"chats/{chatId}/members", null, ct) ?? new();

    public Task AddMember(string chatId, string userId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/members", new Dictionary<string, object?>
        {
            ["user_id"] = userId
        }, ct);

    public Task RemoveMember(string chatId, string userId, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"chats/{chatId}/members/{userId}", null, ct);

    public Task Leave(string chatId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/leave", new Dictionary<string, object?>(), ct);

    public Task TransferOwner(string chatId, string userId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/owner", new Dictionary<string, object?>
        {
            ["user_id"] = userId
        }, ct);

    public Task SetRole(string chatId, string userId, string role, CancellationToken ct = default) =>
        Send(new HttpMethod("PATCH"), $"chats/{chatId}/members/{userId}", new Dictionary<string, object?>
        {
            ["role"] = role
        }, ct);

    /// <summary>
    /// История упорядочена по sequence. ID-курсоры оставлены для старых вызовов API.
    /// </summary>
    public async Task<List<MessageDto>> GetMessages(
        string chatId, int limit = 50, string? beforeId = null, string? afterId = null, CancellationToken ct = default,
        long? beforeSequence = null, long? afterSequence = null)
    {
        var path = $"chats/{chatId}/messages?limit={limit}";
        if (beforeId is { } before) path += "&before_id=" + before;
        if (afterId is { } after) path += "&after_id=" + after;
        if (beforeSequence is { } beforeSeq) path += "&before_sequence=" + beforeSeq;
        if (afterSequence is { } afterSeq) path += "&after_sequence=" + afterSeq;
        return await Send<List<MessageDto>>(HttpMethod.Get, path, null, ct) ?? new();
    }

    /// <summary>client_ref — идемпотентный ключ отправителя (до 64 символов), обычно Guid без дефисов.</summary>
    public async Task<string> SendMessage(
        string chatId, string text, string? replyToId = null, string? clientRef = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["text"] = text, ["message_id"] = EntityIds.NewMessage(chatId) };
        if (replyToId is { } rid) body["reply_to_id"] = rid;
        if (!string.IsNullOrWhiteSpace(clientRef)) body["client_ref"] = clientRef;
        var dto = await Send<MessageIdDto>(HttpMethod.Post, $"chats/{chatId}/messages", body, ct);
        return dto?.MessageId ?? throw new ApiException(500, "Сервер не вернул message_id");
    }

    public async Task<string> ForwardMessage(string messageId, string chatId, CancellationToken ct = default)
    {
        var dto = await Send<MessageIdDto>(HttpMethod.Post, $"messages/{messageId}/forward", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = EntityIds.NewMessage(chatId)
        }, ct);
        return dto?.MessageId ?? throw new ApiException(500, "Сервер не вернул message_id");
    }

    public Task MarkRead(string chatId, string messageId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/read", new Dictionary<string, object?>
        {
            ["message_id"] = messageId
        }, ct);

    public Task EditMessage(string messageId, string text, CancellationToken ct = default) =>
        Send(new HttpMethod("PATCH"), $"messages/{messageId}", new Dictionary<string, object?>
        {
            ["text"] = text
        }, ct);

    public Task DeleteMessage(string messageId, bool forEveryone, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"messages/{messageId}", new Dictionary<string, object?>
        {
            ["for_everyone"] = forEveryone
        }, ct);
    
    public async Task<List<PinDto>> GetPins(string chatId, CancellationToken ct = default) =>
        await Send<List<PinDto>>(HttpMethod.Get, $"chats/{chatId}/pins", null, ct) ?? new();

    public Task PinMessage(string chatId, string messageId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/pins", new Dictionary<string, object?>
        {
            ["message_id"] = messageId
        }, ct);

    public Task UnpinMessage(string chatId, string messageId, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"chats/{chatId}/pins/{messageId}", null, ct);

    public Task AddReaction(string messageId, string emoji, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"messages/{messageId}/reactions", new Dictionary<string, object?>
        {
            ["emoji"] = emoji
        }, ct);

    public Task RemoveReaction(string messageId, string emoji, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}", null, ct);

    public async Task<T?> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (AntispamBlocked && IsMessageMutation(method, path))
            throw new ApiException(429, "Подтвердите, что вы человек", "antispam_required");
        using var req = new HttpRequestMessage(method, path);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var res = await SendRequest(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = ParseError(text, res.StatusCode);
            if (error.Code == "antispam_required") { AntispamSupported = true; SetAntispamBlocked(true); }
            throw error;
        }
        if (res.StatusCode == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text))
            return default;
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    public async Task Send(HttpMethod method, string path, object? body, CancellationToken ct) =>
        await Send<object>(method, path, body, ct);

    static string? Localized(string? code) => code switch
    {
        "message_not_forwardable" => "Это сообщение нельзя переслать",
        "message_forwarded_readonly" => "Пересланное сообщение нельзя изменить",
        _ => null
    };

    static ApiException ParseError(string body, HttpStatusCode status)
    {
        try
        {
            var err = JsonSerializer.Deserialize<ErrorDto>(body, Json);
            if (err != null && (!string.IsNullOrWhiteSpace(err.Error) || !string.IsNullOrWhiteSpace(err.Code)))
                return new ApiException((int)status, Localized(err.Code) ?? err.Error ?? err.Code ?? $"Ошибка {(int)status}", err.Code);
        }
        catch { /* не JSON */ }
        return new ApiException((int)status,
            string.IsNullOrWhiteSpace(body) ? $"Ошибка {(int)status}" : body);
    }

    public void Dispose() => _http.Dispose();
}
