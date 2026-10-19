using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MessengerDesktop;

/// <summary>REST-клиент backend v4. Авторизация только через Authorization: Bearer.</summary>
public sealed class MessengerClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public string ServerBase { get; }
    public string? Token { get; private set; }

    public MessengerClient(string serverBase, string? token = null)
    {
        ServerBase = serverBase.Trim().TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(ServerBase + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (!string.IsNullOrWhiteSpace(token))
            SetToken(token);
    }

    public void SetToken(string token)
    {
        Token = token;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Uri WebSocketUri()
    {
        var http = new Uri(ServerBase + "/ws");
        return new UriBuilder(http)
        {
            Scheme = http.Scheme == "https" ? "wss" : "ws",
            Query = "user_session_token=" + Uri.EscapeDataString(Token ?? "")
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

    public async Task<ProfileDto> GetUser(long id, CancellationToken ct = default) =>
        await Send<ProfileDto>(HttpMethod.Get, $"users/{id}", null, ct) ?? throw new ApiException(500, "Пустой профиль");

    public async Task<List<UserCardDto>> Search(string query, CancellationToken ct = default) =>
        await Send<List<UserCardDto>>(HttpMethod.Get, "users/search?query=" + Uri.EscapeDataString(query), null, ct) ?? new();

    public Task UpdateProfile(string? displayName, string? bio, CancellationToken ct = default) =>
        Send(new HttpMethod("PATCH"), "users/me", new Dictionary<string, object?>
        {
            ["display_name"] = displayName,
            ["bio"] = bio
        }, ct);

    public async Task UploadAvatar(string filePath, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        await using var file = File.OpenRead(filePath);
        using var fileContent = new StreamContent(file);
        content.Add(fileContent, "file", Path.GetFileName(filePath));
        using var req = new HttpRequestMessage(HttpMethod.Post, "users/me/avatar") { Content = content };
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw ParseError(text, res.StatusCode);
    }

    public async Task<byte[]?> GetAvatar(long userId, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"users/{userId}/avatar", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        var data = await res.Content.ReadAsByteArrayAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw ParseError(Encoding.UTF8.GetString(data), res.StatusCode);
        return data;
    }

    public async Task<List<ChatDto>> GetChats(CancellationToken ct = default) =>
        await Send<List<ChatDto>>(HttpMethod.Get, "chats", null, ct) ?? new();

    public async Task<long> CreateDirect(long otherUserId, CancellationToken ct = default)
    {
        var dto = await Send<ChatIdDto>(HttpMethod.Post, "chats", new Dictionary<string, object?>
        {
            ["another_user_id"] = otherUserId
        }, ct);
        return dto?.ChatId ?? throw new ApiException(500, "Сервер не вернул chat_id");
    }

    public async Task<long> CreateGroup(string title, IEnumerable<long> memberIds, CancellationToken ct = default)
    {
        var dto = await Send<ChatIdDto>(HttpMethod.Post, "chats/group", new Dictionary<string, object?>
        {
            ["title"] = title,
            ["member_ids"] = memberIds.ToArray()
        }, ct);
        return dto?.ChatId ?? throw new ApiException(500, "Сервер не вернул chat_id");
    }

    public async Task<ChatDto> GetChat(long chatId, CancellationToken ct = default) =>
        await Send<ChatDto>(HttpMethod.Get, $"chats/{chatId}", null, ct) ?? throw new ApiException(500, "Пустой чат");

    public Task HideChat(long chatId, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"chats/{chatId}", new Dictionary<string, object?>(), ct);

    public async Task<List<MemberDto>> GetMembers(long chatId, CancellationToken ct = default) =>
        await Send<List<MemberDto>>(HttpMethod.Get, $"chats/{chatId}/members", null, ct) ?? new();

    public Task AddMember(long chatId, long userId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/members", new Dictionary<string, object?>
        {
            ["user_id"] = userId
        }, ct);

    public Task RemoveMember(long chatId, long userId, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"chats/{chatId}/members/{userId}", null, ct);

    public Task Leave(long chatId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/leave", new Dictionary<string, object?>(), ct);

    public Task TransferOwner(long chatId, long userId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/owner", new Dictionary<string, object?>
        {
            ["user_id"] = userId
        }, ct);

    public Task SetRole(long chatId, long userId, string role, CancellationToken ct = default) =>
        Send(new HttpMethod("PATCH"), $"chats/{chatId}/members/{userId}", new Dictionary<string, object?>
        {
            ["role"] = role
        }, ct);

    /// <summary>
    /// История: before_id — страница назад; after_id — догон после reconnect (id строго больше курсора, по возрастанию).
    /// </summary>
    public async Task<List<MessageDto>> GetMessages(
        long chatId, int limit = 50, long? beforeId = null, long? afterId = null, CancellationToken ct = default)
    {
        var path = $"chats/{chatId}/messages?limit={limit}";
        if (beforeId is { } before) path += "&before_id=" + before;
        if (afterId is { } after) path += "&after_id=" + after;
        return await Send<List<MessageDto>>(HttpMethod.Get, path, null, ct) ?? new();
    }

    /// <summary>client_ref — идемпотентный ключ отправителя (до 64 символов), обычно Guid без дефисов.</summary>
    public async Task<long> SendMessage(
        long chatId, string text, long? replyToId = null, string? clientRef = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["text"] = text };
        if (replyToId is { } rid) body["reply_to_id"] = rid;
        if (!string.IsNullOrWhiteSpace(clientRef)) body["client_ref"] = clientRef;
        var dto = await Send<MessageIdDto>(HttpMethod.Post, $"chats/{chatId}/messages", body, ct);
        return dto?.MessageId ?? throw new ApiException(500, "Сервер не вернул message_id");
    }

    public Task MarkRead(long chatId, long messageId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"chats/{chatId}/read", new Dictionary<string, object?>
        {
            ["message_id"] = messageId
        }, ct);

    public Task EditMessage(long messageId, string text, CancellationToken ct = default) =>
        Send(new HttpMethod("PATCH"), $"messages/{messageId}", new Dictionary<string, object?>
        {
            ["text"] = text
        }, ct);

    public Task DeleteMessage(long messageId, bool forEveryone, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"messages/{messageId}", new Dictionary<string, object?>
        {
            ["for_everyone"] = forEveryone
        }, ct);
    
    public Task AddReaction(long messageId, string emoji, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"messages/{messageId}/reactions", new Dictionary<string, object?>
        {
            ["emoji"] = emoji
        }, ct);

    public Task RemoveReaction(long messageId, string emoji, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}", null, ct);

    public async Task<T?> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw ParseError(text, res.StatusCode);
        if (res.StatusCode == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text))
            return default;
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    public async Task Send(HttpMethod method, string path, object? body, CancellationToken ct) =>
        await Send<object>(method, path, body, ct);

    static ApiException ParseError(string body, HttpStatusCode status)
    {
        try
        {
            var err = JsonSerializer.Deserialize<ErrorDto>(body, Json);
            if (err != null && (!string.IsNullOrWhiteSpace(err.Error) || !string.IsNullOrWhiteSpace(err.Code)))
                return new ApiException((int)status, err.Error ?? err.Code ?? $"Ошибка {(int)status}", err.Code);
        }
        catch { /* не JSON */ }
        return new ApiException((int)status,
            string.IsNullOrWhiteSpace(body) ? $"Ошибка {(int)status}" : body);
    }

    public void Dispose() => _http.Dispose();
}
