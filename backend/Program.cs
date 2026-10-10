using System.Data;
using System.Net.WebSockets;
using System.Text.Json;
using Dapper;
using MessengerBackend;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var filesRoot = Environment.GetEnvironmentVariable("MESSENGER_FILES") ?? "/var/lib/messenger/files";
foreach (var sub in new[] { "attachments", "avatars" })
    Directory.CreateDirectory(Path.Combine(filesRoot, sub));

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = 64_000_000; // выше лимитов файлов, чтобы отдавать 413/400 своими проверками
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new()
    {
        Title = "Messenger Backend API",
        Version = "3.0.0",
        Description = "Мессенджер: сессии (logout закрывает WS этой сессии), профили, аватары, настройки, " +
                      "личные чаты (один на пару), группы (роли owner/admin/member, передача владения), " +
                      "сообщения (ответы, редактирование, удаление для всех и скрытие у себя, content_state), " +
                      "реакции, вложения, история с курсорной пагинацией, прочтения, WebSocket follow(chat_id).\n\n" +
                      "Основной способ авторизации REST — Authorization: Bearer <user_session_token>. " +
                      "Устаревшие альтернативы (X-Session-Token, query, JSON-поле) поддерживаются для совместимости.\n\n" +
                      "Описание протокола WebSocket (события, close codes, повторный follow): GET /ws/protocol."
    });
    o.OperationFilter<AuthAndErrorsOperationFilter>();
    o.SchemaFilter<DtoSchemaFilter>();
    o.AddSecurityDefinition("SessionBearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "user_session_token",
        Description = "Основной способ: Authorization: Bearer <user_session_token>"
    });
    o.AddSecurityDefinition("SessionHeader", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-Session-Token",
        Description = "Устаревшая альтернатива: заголовок X-Session-Token"
    });
});
builder.Services.AddSingleton<Hub>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ---------- единый формат ошибок ----------
// {"code":"...","error":"...","details":null,"trace_id":"..."}; error сохранён для совместимости.
static IResult Err(int status, string msg, string? code = null)
{
    code ??= status switch
    {
        400 => "validation_error",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        409 => "conflict",
        413 => "file_too_large",
        415 => "unsupported_media_type",
        429 => "rate_limited",
        _ => "error"
    };
    return Results.Json(new ErrorResponse(code, msg, null, Guid.NewGuid().ToString("N")), statusCode: status);
}

app.Use(async (ctx, next) =>
{
    try { await next(ctx); }
    catch (HttpException ex)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = ex.Status;
        ctx.Response.ContentType = "application/json";
        ctx.Items["ErrorBodyWritten"] = true;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(
            new ErrorResponse(ex.Code ?? DefaultCode(ex.Status), ex.Message, null, Guid.NewGuid().ToString("N"))));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "unhandled error on {Path}", ctx.Request.Path);
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json";
        ctx.Items["ErrorBodyWritten"] = true;
        await ctx.Response.WriteAsync("{\"code\":\"internal_error\",\"error\":\"internal error\",\"details\":null,\"trace_id\":\"" +
                                      Guid.NewGuid().ToString("N") + "\"}");
    }

    // framework-ответы без тела (binding 400, 404 маршрута, 415) приводим к единому формату
    if (ctx.Items["ErrorBodyWritten"] is null && !ctx.Response.HasStarted &&
        ctx.Response.StatusCode is 400 or 404 or 415 && ctx.Response.ContentLength is null or 0 &&
        ctx.Response.ContentType is null)
    {
        ctx.Items["ErrorBodyWritten"] = true;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ErrorResponse(
            ctx.Response.StatusCode switch
            {
                415 => "unsupported_media_type",
                404 => "not_found",
                _ => "validation_error"
            },
            ctx.Response.StatusCode switch
            {
                415 => "Content-Type не поддерживается; ожидается application/json или multipart/form-data",
                404 => "Маршрут не найден",
                _ => "Некорректное тело запроса или обязательные поля отсутствуют"
            },
            null, Guid.NewGuid().ToString("N"))));
    }
});

static string DefaultCode(int status) => status switch
{
    400 => "validation_error", 401 => "unauthorized", 403 => "forbidden", 404 => "not_found",
    409 => "conflict", 413 => "file_too_large", 415 => "unsupported_media_type",
    429 => "rate_limited", _ => "error"
};

var pathBase = Environment.GetEnvironmentVariable("MESSENGER_PATH_BASE") ?? "/messenger";
if (string.IsNullOrWhiteSpace(pathBase) || pathBase[0] != '/') pathBase = "/messenger";
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
app.UsePathBase(pathBase);
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/auth"))
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!app.Services.GetRequiredService<RateLimiter>().Allow(ip, 30, TimeSpan.FromMinutes(1)))
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = "60";
            ctx.Items["ErrorBodyWritten"] = true;
            await ctx.Response.WriteAsJsonAsync(
                new ErrorResponse("rate_limited", "too many requests, try later", null, Guid.NewGuid().ToString("N")));
            return;
        }
    }
    await next(ctx);
});

Db.Init();
var hub = app.Services.GetRequiredService<Hub>();

// фон: раз в минуту закрываем WS с истёкшими/удалёнными сессиями (close 4002)
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        try
        {
            var dead = Db.Read(c => c.Query<string>(
                "SELECT token_hash FROM user_sessions WHERE expires_at <= @now", new { now = Util.NowIso() }).ToHashSet());
            if (dead.Count > 0)
                await hub.CloseWhere(cn => dead.Contains(cn.TokenHash), Hub.CloseSessionExpired, "session expired");
        }
        catch { /* не критично */ }
    }
});

// ================== helpers ==================

SessionUser RequireUser(HttpContext ctx, string? bodyToken = null)
{
    var token = Auth.TokenFromContext(ctx) ?? bodyToken;
    if (string.IsNullOrWhiteSpace(token))
        throw new HttpException(401, "missing user_session_token");
    var user = Db.Read(c => c.QueryFirstOrDefault<SessionUser>(
        """
        SELECT s.user_id AS Id, u.tag AS Login, s.token_hash AS TokenHash FROM user_sessions s
        JOIN users u ON u.id = s.user_id
        WHERE s.token_hash = @h AND s.expires_at > @now
        """, new { h = Auth.TokenHash(token), now = Util.NowIso() }));
    if (user is null) throw new HttpException(401, "invalid or expired user_session_token");
    TouchLastSeen(user.Id);
    return user;
}

void TouchLastSeen(long userId) => Task.Run(() =>
{
    try
    {
        Db.Write(c => { c.Execute("UPDATE users SET last_seen_at=@t WHERE id=@u",
            new { t = Util.NowIso(), u = userId }); return 0; });
    }
    catch { /* не критично */ }
});

UserRow UserById(IDbConnection c, long id) =>
    c.QueryFirstOrDefault<UserRow>("SELECT * FROM users WHERE id=@id", new { id })
    ?? throw new HttpException(404, "user not found");

static UserCard Card(UserRow u) => new(u.Id, u.Tag, u.Display_Name);
static ProfileOut Profile(UserRow u) =>
    new(u.Id, u.Tag, u.Display_Name, u.Path_To_Avatar_File, u.Bio, u.Join_Date, u.Last_Seen_At);

static string SafeFileName(string name)
{
    var clean = System.IO.Path.GetFileName(name ?? "");
    foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
        clean = clean.Replace(ch, '_');
    return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
}

static string SafeExtension(string fileName)
{
    var ext = System.IO.Path.GetExtension(fileName ?? "");
    return !string.IsNullOrEmpty(ext) && ext.Length <= 10 && ext.All(ch => char.IsLetterOrDigit(ch) || ch == '.')
        ? ext.ToLowerInvariant() : "";
}

static string Trunc(string? s, int max) => string.IsNullOrEmpty(s) ? "application/octet-stream"
    : (s.Length <= max ? s : s[..max]);

static (int? Limit, long? Before, IResult? Error) ParsePaging(HttpContext ctx)
{
    int? limit = null; long? before = null;
    var ls = ctx.Request.Query["limit"].ToString();
    if (ls.Length > 0)
    {
        if (!int.TryParse(ls, out var lv) || lv < 1 || lv > 100)
            return (null, null, Err(400, "limit must be an integer between 1 and 100"));
        limit = lv;
    }
    var bs = ctx.Request.Query["before_id"].ToString();
    if (bs.Length > 0)
    {
        if (!long.TryParse(bs, out var bv) || bv < 1)
            return (null, null, Err(400, "before_id must be a positive integer message id"));
        before = bv;
    }
    return (limit, before, null);
}

List<AttachmentRow> AttachmentsOf(IDbConnection c, IEnumerable<long> messageIds)
{
    var ids = messageIds.ToList();
    if (ids.Count == 0) return [];
    return c.Query<AttachmentRow>(
        "SELECT * FROM attachments WHERE message_id IN @ids ORDER BY id", new { ids }).ToList();
}

List<ReactionOut> ReactionsOf(IDbConnection c, IEnumerable<long> messageIds)
{
    var ids = messageIds.ToList();
    if (ids.Count == 0) return [];
    var rows = c.Query<ReactionRow>(
        "SELECT * FROM message_reactions WHERE message_id IN @ids ORDER BY id", new { ids }).ToList();
    return rows.GroupBy(r => r.Emoji)
        .Select(g => new ReactionOut(g.Key, g.Select(r => r.User_Id).ToList()))
        .ToList();
}

/// <summary>read_at (агрегат): сообщение считается прочитанным, когда ВСЕ текущие участники,
/// кроме отправителя, подтвердили прочтение этого или более позднего сообщения.</summary>
static string? AggregateReadAt(long messageId, long senderId, List<MemberRow> members)
{
    var others = members.Where(m => m.User_Id != senderId).ToList();
    if (others.Count == 0) return null;
    if (others.Any(m => (m.Last_Read_Message_Id ?? 0) < messageId)) return null;
    return others.Where(m => m.Read_At is not null).Max(m => m.Read_At);
}

/// <summary>Контракт содержимого: text=="" в БД — маркер not_stored (пустой текст запрещён при отправке).</summary>
MessageOut MsgOut(IDbConnection c, MessageRow m, List<MemberRow>? members = null, string? textOverride = null)
{
    var deleted = m.Deleted_At is not null;
    var notStored = !deleted && m.Text.Length == 0;
    var atts = deleted ? [] : AttachmentsOf(c, [m.Id]);
    var reacts = deleted ? [] : ReactionsOf(c, [m.Id]);
    members ??= MembersOf(c, m.Chat_Id);
    return new MessageOut(m.Id, m.Chat_Id, m.Sender_Id, m.Reply_To_Id,
        deleted || notStored ? (textOverride is null ? null : textOverride) : (textOverride ?? m.Text),
        deleted ? "deleted" : notStored ? "not_stored" : "stored",
        m.Sent_At, m.Delivered_At, m.Edited_At,
        AggregateReadAt(m.Id, m.Sender_Id, members), m.Deleted_At,
        atts.Select(a => new AttachmentOut(a.Id, a.Message_Id, a.File_Name, a.File_Type, a.File_Size_Bytes)).ToList(),
        reacts, m.Client_Id);
}

ChatRow ChatById(IDbConnection c, long id) =>
    c.QueryFirstOrDefault<ChatRow>("SELECT * FROM chats WHERE id=@id", new { id })
    ?? throw new HttpException(404, "chat not found");

static string RoleOf(IDbConnection c, long chatId, long userId) =>
    c.ExecuteScalar<string?>("SELECT role FROM chat_members WHERE chat_id=@c AND user_id=@u",
        new { c = chatId, u = userId }) ?? "";

static void RequireMember(IDbConnection c, long chatId, long userId)
{
    if (RoleOf(c, chatId, userId) == "")
        throw new HttpException(403, "not a member of this chat");
}

static List<MemberRow> MembersOf(IDbConnection c, long chatId) =>
    c.Query<MemberRow>(
        """
        SELECT m.chat_id AS Chat_Id, m.user_id AS User_Id, m.role AS Role, m.joined_at AS Joined_At,
               m.last_read_message_id AS Last_Read_Message_Id, m.read_at AS Read_At,
               u.tag AS Tag, u.display_name AS Display_Name, u.path_to_avatar_file AS Path_To_Avatar_File
        FROM chat_members m JOIN users u ON u.id = m.user_id
        WHERE m.chat_id=@c ORDER BY m.joined_at, m.user_id
        """, new { c = chatId }).ToList();

static List<MemberOut> MembersOut(IDbConnection c, long chatId) =>
    MembersOf(c, chatId).Select(m => new MemberOut(m.User_Id, m.Tag!, m.Display_Name!, m.Role, m.Joined_At, m.Last_Read_Message_Id, m.Read_At)).ToList();

static long[] MemberIds(IDbConnection c, long chatId) =>
    c.Query<long>("SELECT user_id FROM chat_members WHERE chat_id=@c ORDER BY joined_at", new { c = chatId })
     .ToArray();

MessageRow? LastVisibleMessageOf(IDbConnection c, long chatId, long viewerId) =>
    c.QueryFirstOrDefault<MessageRow>(
        """
        SELECT * FROM messages WHERE chat_id=@c
           AND id NOT IN (SELECT message_id FROM message_hidden WHERE user_id=@u)
           ORDER BY id DESC LIMIT 1
        """, new { c = chatId, u = viewerId });

object ChatInfoOut(IDbConnection c, ChatRow chat, long viewerId, bool withLastMessage)
{
    var last = withLastMessage ? LastVisibleMessageOf(c, chat.Id, viewerId) : null;
    var lastOut = last is { } l ? MsgOut(c, l) : null;
    if (chat.Type == "direct")
    {
        var other = c.QuerySingle<long?>(
            "SELECT user_id FROM chat_members WHERE chat_id=@c AND user_id<>@u LIMIT 1",
            new { c = chat.Id, u = viewerId });
        var otherCard = other is { } oid ? Card(UserById(c, oid)) : null;
        return new DirectChatOut(chat.Id, "direct", otherCard!, chat.Created_At, chat.Created_By, lastOut);
    }
    return new GroupChatOut(chat.Id, "group", chat.Title ?? "", chat.Created_By, chat.Created_At, lastOut);
}

static bool IsValidImage(byte[] data, out string reason)
{
    reason = "";
    if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
    {
        var w = BitConverter.ToInt32(new[] { data[19], data[18], data[17], data[16] }, 0);
        var h = BitConverter.ToInt32(new[] { data[23], data[22], data[21], data[20] }, 0);
        if (w is < 1 or > 4096 || h is < 1 or > 4096) { reason = "PNG dimensions out of limits (max 4096x4096)"; return false; }
        return true;
    }
    if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true; // JPEG
    if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
        && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P') return true;
    reason = "only PNG, JPEG or WebP images are allowed";
    return false;
}

// ================== Health ==================

app.MapGet("/health", () => Results.Json(new
{ status = "ok", service = "messenger-backend", version = "3.0.0" }))
   .WithTags("Health").WithName("health").WithSummary("Живость сервиса");

// ================== Auth ==================

app.MapPost("/auth/register", ([FromBody] RegisterIn r) =>
{
    var login = r.Login?.Trim() ?? "";
    if (!System.Text.RegularExpressions.Regex.IsMatch(login, @"^[A-Za-z0-9_.\-]{3,16}$"))
        return Err(400, "login (tag) must be 3-16 chars: letters, digits, _ . -");
    if (string.IsNullOrEmpty(r.Password) || r.Password.Length < 8)
        return Err(400, "password must be at least 8 characters");
    return Db.Write<IResult>(c =>
    {
        if (c.ExecuteScalar<long?>("SELECT 1 FROM users WHERE tag=@t COLLATE NOCASE LIMIT 1", new { t = login }) == 1)
            return Err(409, "Указанный login уже занят", "login_taken");
        var id = c.ExecuteScalar<long>(
            "INSERT INTO users(tag, password_hash, display_name, join_date) VALUES(@t, @p, @d, @j); " +
            "SELECT last_insert_rowid()",
            new { t = login, p = Auth.HashPassword(r.Password), d = login, j = Util.NowIso() });
        c.Execute("INSERT INTO user_settings(user_id, save_history) VALUES(@u, 1)", new { u = id });
        return Results.StatusCode(StatusCodes.Status201Created);
    });
}).WithTags("Auth").WithName("register")
.WithSummary("Регистрация")
.WithDescription("Создаёт пользователя: tag = login (3-16: буквы, цифры, _ . -), пароль >= 8 символов, " +
                 "display_name = login. Ответ 201 без тела. 409 — login уже занят.")
.Produces(201);

app.MapPost("/auth/login", ([FromBody] LoginIn r) =>
{
    if (string.IsNullOrEmpty(r?.Login)) return Err(400, "login required");
    return Db.Read<IResult>(c =>
    {
        var u = c.QueryFirstOrDefault<UserRow>("SELECT * FROM users WHERE tag=@t COLLATE NOCASE",
            new { t = r.Login.Trim() });
        if (u is null || !Auth.VerifyPassword(r.Password ?? "", u.Password_Hash))
            return Err(401, "Неверный login или password");
        return Results.Json(new TokenOut(Auth.CreateSession(c, u.Id), Util.NowIso(TimeSpan.FromDays(30))));
    });
}).WithTags("Auth").WithName("login")
.WithSummary("Вход")
.WithDescription("Возвращает user_session_token (срок 30 дней) и expires_at. 401 — неверный login или password.")
.Produces<TokenOut>(200);

app.MapPost("/auth/logout", async (HttpContext ctx, [FromBody] LogoutIn? r) =>
{
    var me = RequireUser(ctx, r?.UserSessionToken);
    var token = Auth.TokenFromContext(ctx) ?? r?.UserSessionToken!;
    var hash = Auth.TokenHash(token);
    await Db.WriteAsync(async c =>
    {
        Auth.DropSession(c, hash);
        return 0;
    });
    _ = hub.CloseSession(hash, Hub.CloseSessionRevoked, "session revoked"); // close-фрейм уходит сразу, ответ не ждём
    return Results.NoContent();
}).WithTags("Auth").WithName("logout")
.WithSummary("Выход")
.WithDescription("Удаляет ТОЛЬКО эту сессию и закрывает все её WebSocket-соединения (close code 4001). " +
                 "Другие сессии того же пользователя остаются действительными.")
.Produces(204);

// ================== Profiles ==================

app.MapGet("/users/me", (HttpContext ctx) =>
{
    var me = RequireUser(ctx);
    return Db.Read(c => Results.Json(Profile(UserById(c, me.Id))));
}).WithTags("Profiles").WithName("getMe").WithSummary("Мой профиль")
.WithDescription("tag, display_name, path_to_avatar_file, bio, join_date, last_seen_at (обновляется при запросах).")
.Produces<ProfileOut>(200);

app.MapPatch("/users/me", (HttpContext ctx, [FromBody] UpdateProfileIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    var displayName = r.DisplayName?.Trim();
    if (displayName is { Length: 0 } or { Length: > 32 })
        return Err(400, "display_name must be 1-32 characters");
    if (displayName is null && r.Bio is null)
        return Err(400, "nothing to update: pass display_name and/or bio");
    if (r.Bio is { Length: > 500 })
        return Err(400, "bio too long (max 500)");
    return Db.Write<IResult>(c =>
    {
        // null/отсутствующее поле = без изменений; пустая строка bio = очистить bio
        c.Execute("UPDATE users SET display_name=COALESCE(@d, display_name), bio=@b WHERE id=@u",
            new { d = displayName, b = r.Bio ?? NoTouchBio(c, me.Id), u = me.Id });
        return Results.NoContent();
    });
}).WithTags("Profiles").WithName("updateMe").WithSummary("Частичное изменение профиля")
.WithDescription("Частичная семантика: отсутствующее или null поле НЕ меняется. bio=\"\" (пустая строка) " +
                 "очищает bio; display_name пустым быть не может (400). Токен можно передать в теле.")
.Produces(204);

static string? NoTouchBio(IDbConnection c, long userId) =>
    c.ExecuteScalar<string?>("SELECT bio FROM users WHERE id=@u", new { u = userId });

app.MapGet("/users/search", (HttpContext ctx, [FromQuery] string query) =>
{
    RequireUser(ctx);
    var q = (query ?? "").Trim();
    if (q.Length < 1) return Err(400, "query required");
    var like = $"%{q.Replace("@", "@@").Replace("%", "@%").Replace("_", "@_")}%";
    return Db.Read(c => Results.Json(c.Query<UserRow>(
        """
        SELECT * FROM users WHERE tag LIKE @q ESCAPE '@' OR display_name LIKE @q ESCAPE '@'
        ORDER BY tag LIMIT 20
        """, new { q = like }).Select(Card).ToList()));
}).WithTags("Profiles").WithName("searchUsers").WithSummary("Поиск пользователей")
.WithDescription("Подстрока по tag или display_name, до 20 карточек [{id, tag, display_name}].")
.Produces<List<UserCard>>(200);

app.MapGet("/users/{user_id:long}", (HttpContext ctx, long user_id) =>
{
    RequireUser(ctx);
    return Db.Read(c => Results.Json(Profile(UserById(c, user_id))));
}).WithTags("Profiles").WithName("getUser").WithSummary("Профиль по id")
.WithDescription("Публичный профиль пользователя. 404 — пользователь не найден.")
.Produces<ProfileOut>(200);

// ================== Avatars ==================

app.MapPost("/users/me/avatar", async (HttpContext ctx) =>
{
    var me = RequireUser(ctx);
    IFormCollection form;
    try { form = await ctx.Request.ReadFormAsync(); }
    catch (InvalidOperationException) { return Err(400, "multipart/form-data with field 'file' required"); }
    var file = form.Files.FirstOrDefault(f => f.Name == "file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0) return Err(400, "multipart field 'file' required");
    if (file.Length > 5_000_000) return Err(413, $"avatar too large: {file.Length} bytes (max 5 MB)");
    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var data = ms.ToArray();
    if (!IsValidImage(data, out var reason))
        return Err(400, $"invalid_avatar: {reason}", "invalid_avatar");
    var dir = Path.Combine(filesRoot, "avatars");
    var rel = $"avatars/u{me.Id}_{Guid.NewGuid():N}{SafeExtension(file.FileName)}";
    var abs = Path.Combine(dir, System.IO.Path.GetFileName(rel));
    await File.WriteAllBytesAsync(abs, data);
    return Db.Write<IResult>(c =>
    {
        c.Execute("UPDATE users SET path_to_avatar_file=@p WHERE id=@u", new { p = rel, u = me.Id });
        return Results.Json(new { path_to_avatar_file = rel }, statusCode: 201);
    });
}).WithTags("Avatars").WithName("uploadAvatar")
.WithSummary("Загрузка своего аватара")
.WithDescription("multipart/form-data, поле file (обязательное, один файл, до 5 МБ, 413 при превышении). " +
                 "Содержимое проверяется по сигнатуре: разрешены PNG/JPEG/WebP (PNG — ещё и габариты до 4096x4096). " +
                 "Текст, HTML, SVG и повреждённые изображения отклоняются 400 invalid_avatar; прежний аватар при ошибке сохраняется.")
.DisableAntiforgery()
.Produces(201);

app.MapGet("/users/{user_id:long}/avatar", (HttpContext ctx, long user_id) =>
{
    RequireUser(ctx);
    var path = Db.Read(c => c.ExecuteScalar<string?>(
        "SELECT path_to_avatar_file FROM users WHERE id=@u", new { u = user_id }));
    if (string.IsNullOrWhiteSpace(path)) return Err(404, "avatar not set");
    var abs = Path.Combine(filesRoot, "avatars", System.IO.Path.GetFileName(path));
    return File.Exists(abs) ? Results.File(abs) : Err(404, "avatar file missing");
}).WithTags("Avatars").WithName("getAvatar").WithSummary("Файл аватара")
.WithDescription("Бинарный ответ (фактический Content-Type файла). 404 — аватар не установлен.")
.Produces(200);

// ================== Settings ==================

app.MapGet("/users/me/settings", (HttpContext ctx) =>
{
    var me = RequireUser(ctx);
    return Db.Read(c => Results.Json(new SettingsOut(
        c.ExecuteScalar<long>("SELECT save_history FROM user_settings WHERE user_id=@u", new { u = me.Id }) == 1)));
}).WithTags("Settings").WithName("getSettings").WithSummary("Мои настройки")
.WithDescription("save_history: сохранять ли ИСХОДЯЩИЕ сообщения этого пользователя на сервере.")
.Produces<SettingsOut>(200);

app.MapPatch("/users/me/settings", (HttpContext ctx, [FromBody] SettingsIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    if (r.SaveHistory is not true and not false)
        return Err(400, "save_history must be an explicit true or false; null or missing is not allowed");
    return Db.Write<IResult>(c =>
    {
        c.Execute("INSERT INTO user_settings(user_id, save_history) VALUES(@u, @s) " +
                  "ON CONFLICT(user_id) DO UPDATE SET save_history=@s",
            new { u = me.Id, s = r.SaveHistory.Value ? 1 : 0 });
        return Results.NoContent();
    });
}).WithTags("Settings").WithName("updateSettings").WithSummary("Изменить save_history")
.WithDescription("Только явное true/false; {} или null -> 400, настройки не меняются. " +
                 "Семантика: настройка ОТПРАВИТЕЛЯ. При false сообщение хранится как заглушка " +
                 "(id, отправитель, чат, время) без текста; текст доставляется только онлайн через WS. " +
                 "Настройка не влияет на прошлые сообщения и не скрывает входящие.")
.Produces(204);

// ================== Chats ==================

app.MapGet("/chats", (HttpContext ctx) =>
{
    var me = RequireUser(ctx);
    return Db.Read(c => Results.Json(c.Query<ChatRow>(
        """
        SELECT ch.* FROM chats ch
        JOIN chat_members m ON m.chat_id = ch.id
        WHERE m.user_id=@u
           AND ch.id NOT IN (SELECT chat_id FROM chat_hidden WHERE user_id=@u)
        ORDER BY ch.id DESC
        """, new { u = me.Id })
        .Select(ch => ChatInfoOut(c, ch, me.Id, withLastMessage: true)).ToList()));
}).WithTags("Chats").WithName("listChats").WithSummary("Мои чаты")
.WithDescription("Скрытые через DELETE /chats/{id} чаты не показываются. direct -> other_user; group -> title. " +
                 "last_message учитывает скрытые «у себя» сообщения (message_hidden) текущего пользователя.")
.Produces(200);

app.MapPost("/chats", async (HttpContext ctx, [FromBody] CreateChatIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    if (r.AnotherUserId is not > 0)
        return Err(400, "another_user_id must be a positive user id");
    if (r.AnotherUserId == me.Id) return Err(400, "cannot create direct chat with yourself");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var other = UserById(c, r.AnotherUserId.Value);
        var a = Math.Min(me.Id, other.Id);
        var b = Math.Max(me.Id, other.Id);
        var existing = c.ExecuteScalar<long?>(
            "SELECT chat_id FROM direct_chats WHERE user1_id=@a AND user2_id=@b", new { a, b });
        if (existing is { } e)
        {
            // повторное открытие той же переписки: снять скрытие у вызывающего
            c.Execute("DELETE FROM chat_hidden WHERE chat_id=@c AND user_id=@u", new { c = e, u = me.Id });
            return Results.Json(new ChatIdOut(e));
        }
        var id = c.ExecuteScalar<long>(
            "INSERT INTO chats(type, title, created_at, created_by) VALUES('direct', NULL, @ca, @cb); " +
            "SELECT last_insert_rowid()", new { ca = Util.NowIso(), cb = me.Id });
        c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @u, 'member', @j)",
            new { c = id, u = other.Id, j = Util.NowIso() });
        c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @m, 'member', @j)",
            new { c = id, m = me.Id, j = Util.NowIso() });
        c.Execute("INSERT INTO direct_chats(chat_id, user1_id, user2_id) VALUES(@c, @a, @b)", new { c = id, a, b });
        await hub.PublishToUsers(new[] { me.Id, other.Id },
            new WsEvent("chat_created", id) { AlwaysDeliver = true, Chat = ChatInfoOut(c, ChatById(c, id), me.Id, false) });
        return Results.Json(new ChatIdOut(id), statusCode: 201);
    });
}).WithTags("Chats").WithName("createDirectChat")
.WithSummary("Личный чат (единственный на пару)")
.WithDescription("201 — создан личный чат; 200 — личный чат этой пары уже существует, возвращён его chat_id " +
                 "(пара хранится нормализованно user1<user2 в DirectChats, повтор с любой стороны не создаёт второй чат " +
                 "и снимает скрытие у вызывающего). Событие WS chat_created обоим (без follow).")
.Produces<ChatIdOut>(201).Produces<ChatIdOut>(200);

app.MapPost("/chats/group", async (HttpContext ctx, [FromBody] CreateGroupIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    var title = r.Title?.Trim() ?? "";
    if (title.Length is < 1 or > 64) return Err(400, "title must be 1-64 characters");
    var ids = (r.MemberIds ?? []).Distinct().Where(id => id > 0 && id != me.Id).ToList();
    return await Db.WriteAsync<IResult>(async c =>
    {
        foreach (var id in ids) UserById(c, id);
        var chatId = c.ExecuteScalar<long>(
            "INSERT INTO chats(type, title, created_at, created_by) VALUES('group', @t, @ca, @cb); " +
            "SELECT last_insert_rowid()", new { t = title, ca = Util.NowIso(), cb = me.Id });
        c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @me, 'owner', @j)",
            new { c = chatId, me = me.Id, j = Util.NowIso() });
        foreach (var id in ids)
            c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @u, 'member', @j)",
                new { c = chatId, u = id, j = Util.NowIso() });
        await hub.PublishToUsers(MemberIds(c, chatId),
            new WsEvent("chat_created", chatId) { AlwaysDeliver = true, Chat = ChatInfoOut(c, ChatById(c, chatId), me.Id, false) });
        return Results.Json(new ChatIdOut(chatId), statusCode: 201);
    });
}).WithTags("Chats").WithName("createGroupChat")
.WithSummary("Создать группу")
.WithDescription("Создаёт НОВУЮ группу при каждом вызове, даже с тем же составом; автор становится owner " +
                 "(role=owner), остальные member. member_ids дедуплицируются; несуществующий user_id -> 404. " +
                 "title 1-64 символа. Событие WS chat_created всем участникам (без follow).")
.Produces<ChatIdOut>(201);

app.MapGet("/chats/{chat_id:long}", (HttpContext ctx, long chat_id) =>
{
    var me = RequireUser(ctx);
    return Db.Read(c =>
    {
        var chat = ChatById(c, chat_id);
        RequireMember(c, chat.Id, me.Id);
        return Results.Json(ChatInfoOut(c, chat, me.Id, withLastMessage: false));
    });
}).WithTags("Chats").WithName("getChat").WithSummary("Карточка чата")
.WithDescription("direct -> other_user; group -> title/created_by/created_at. Только участник (иначе 403).")
.Produces(200);

app.MapDelete("/chats/{chat_id:long}", async (HttpContext ctx, long chat_id, [FromBody] LogoutIn? r) =>
{
    var me = RequireUser(ctx, r?.UserSessionToken);
    return await Db.WriteAsync<IResult>(async c =>
    {
        var chat = ChatById(c, chat_id);
        RequireMember(c, chat.Id, me.Id);
        c.Execute("INSERT OR IGNORE INTO chat_hidden(chat_id, user_id) VALUES(@c, @u)",
            new { c = chat.Id, u = me.Id });
        await hub.PublishToUsers([me.Id],
            new WsEvent("chat_hidden", chat.Id) { AlwaysDeliver = true });
        return Results.NoContent();
    });
}).WithTags("Chats").WithName("hideChat")
.WithSummary("Скрыть чат у себя")
.WithDescription("Скрывает чат только у вызывающего (chat_hidden); участники, сообщения и вложения не затрагиваются. " +
                 "НЕ выход из группы (leave) и НЕ удаление группы. Для direct повторное POST /chats возвращает тот же " +
                 "chat_id и снимает скрытие у вызывающего. Идемпотентно (204).")
.Produces(204);

// ================== Members ==================

app.MapGet("/chats/{chat_id:long}/members", (HttpContext ctx, long chat_id) =>
{
    var me = RequireUser(ctx);
    return Db.Read(c =>
    {
        var chat = ChatById(c, chat_id);
        RequireMember(c, chat.Id, me.Id);
        return Results.Json(MembersOut(c, chat.Id));
    });
}).WithTags("Members").WithName("getMembers").WithSummary("Участники чата")
.WithDescription("[{id, tag, display_name, role: member|admin|owner, joined_at}]. Только участник.")
.Produces<List<MemberOut>>(200);

app.MapPost("/chats/{chat_id:long}/members", async (HttpContext ctx, long chat_id, [FromBody] AddMemberIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    if (r.UserId <= 0) return Err(400, "user_id must be a positive user id");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var chat = ChatById(c, chat_id);
        if (chat.Type != "group") return Err(400, "members can only be added to a group chat");
        if (RoleOf(c, chat.Id, me.Id) is not ("owner" or "admin"))
            return Err(403, "only owner or admin can add members");
        var target = UserById(c, r.UserId);
        if (RoleOf(c, chat.Id, target.Id) != "") return Results.NoContent(); // уже участник — идемпотентно
        c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @u, 'member', @j)",
            new { c = chat.Id, u = target.Id, j = Util.NowIso() });
        await hub.PublishToUsers(MemberIds(c, chat.Id),
            new WsEvent("member_added", chat.Id) { UserId = target.Id, User = Card(target) });
        await hub.PublishToUsers([target.Id],
            new WsEvent("chat_created", chat.Id) { AlwaysDeliver = true, Chat = ChatInfoOut(c, chat, target.Id, false) });
        return Results.NoContent();
    });
}).WithTags("Members").WithName("addMember").WithSummary("Добавить участника в группу")
.WithDescription("Только owner/admin и только для группы; повторное добавление не создаёт дубль (204). " +
                 "События: member_added участникам (follow), chat_added добавленному (без follow).")
.Produces(204);

app.MapDelete("/chats/{chat_id:long}/members/{user_id:long}", async (HttpContext ctx, long chat_id, long user_id) =>
{
    var me = RequireUser(ctx);
    return await Db.WriteAsync<IResult>(async c =>
    {
        var chat = ChatById(c, chat_id);
        if (chat.Type != "group") return Err(400, "cannot remove members from a direct chat");
        var myRole = RoleOf(c, chat.Id, me.Id);
        if (myRole is not ("owner" or "admin")) return Err(403, "only owner or admin can remove members");
        var targetRole = RoleOf(c, chat.Id, user_id);
        if (targetRole == "") return Err(404, "user is not a member of this chat");
        if (targetRole == "owner") return Err(403, "the owner cannot be removed");
        if (targetRole == "admin" && myRole != "owner") return Err(403, "only the owner can remove an admin");
        if (user_id == me.Id) return Err(400, "use POST /chats/{id}/leave to leave");
        c.Execute("DELETE FROM chat_members WHERE chat_id=@c AND user_id=@u", new { c = chat.Id, u = user_id });
        await hub.PublishToUsers(MemberIds(c, chat.Id).Append(user_id),
            new WsEvent("member_removed", chat.Id) { UserId = user_id });
        return Results.NoContent();
    });
}).WithTags("Members").WithName("removeMember").WithSummary("Исключить участника из группы")
.WithDescription("Owner/admin (owner снимает и admin; owner не может быть исключён). Исключённый сразу теряет " +
                 "доступ по HTTP и WS (перестаёт получать события чата). 404 — пользователь не в группе.")
.Produces(204);

app.MapPost("/chats/{chat_id:long}/leave", async (HttpContext ctx, long chat_id, [FromBody] LogoutIn? r) =>
{
    var me = RequireUser(ctx, r?.UserSessionToken);
    return await Db.WriteAsync<IResult>(async c =>
    {
        var chat = ChatById(c, chat_id);
        RequireMember(c, chat.Id, me.Id);
        if (chat.Type != "group") return Err(400, "cannot leave a direct chat");
        var myRole = RoleOf(c, chat.Id, me.Id);
        var others = c.Query<long>(
            "SELECT user_id FROM chat_members WHERE chat_id=@c AND user_id<>@u", new { c = chat.Id, u = me.Id }).ToList();
        if (myRole == "owner" && others.Count > 0)
            return Err(409, "owner must transfer ownership before leaving (POST /chats/{id}/owner)",
                "owner_transfer_required");
        c.Execute("DELETE FROM chat_members WHERE chat_id=@c AND user_id=@u", new { c = chat.Id, u = me.Id });
        if (myRole == "owner" && others.Count == 0)
        {
            // последний участник вышел — закрываем группу целиком (каскад: сообщения, вложения, реакции)
            c.Execute("DELETE FROM chats WHERE id=@c", new { c = chat.Id });
            await hub.PublishToUsers([me.Id], new WsEvent("chat_deleted", chat.Id) { AlwaysDeliver = true });
            return Results.NoContent();
        }
        await hub.PublishToUsers(others.Append(me.Id), new WsEvent("member_left", chat.Id) { UserId = me.Id });
        return Results.NoContent();
    });
}).WithTags("Members").WithName("leaveChat").WithSummary("Выйти из группы")
.WithDescription("Владелец не может выйти, пока в группе есть другие участники: 409 owner_transfer_required " +
                 "(сначала POST /chats/{id}/owner). Выход последнего закрывает группу. ЛС покинуть нельзя (400).")
.Produces(204);

app.MapPost("/chats/{chat_id:long}/owner", async (HttpContext ctx, long chat_id, [FromBody] TransferOwnerIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    if (r.UserId <= 0) return Err(400, "user_id must be a positive user id");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var chat = ChatById(c, chat_id);
        if (chat.Type != "group") return Err(400, "ownership applies only to group chats");
        if (RoleOf(c, chat.Id, me.Id) != "owner") return Err(403, "only the current owner can transfer ownership");
        var target = UserById(c, r.UserId);
        if (RoleOf(c, chat.Id, target.Id) == "")
            return Err(404, "new owner must be a member of this chat");
        if (target.Id == me.Id) return Err(400, "owner is already you; transfer to another member");
        c.Execute("UPDATE chat_members SET role='admin' WHERE chat_id=@c AND user_id=@me", new { c = chat.Id, me = me.Id });
        c.Execute("UPDATE chat_members SET role='owner' WHERE chat_id=@c AND user_id=@u", new { c = chat.Id, u = target.Id });
        await hub.PublishToUsers(MemberIds(c, chat.Id),
            new WsEvent("owner_changed", chat.Id) { AlwaysDeliver = true, UserId = target.Id });
        return Results.NoContent();
    });
}).WithTags("Members").WithName("transferOwner").WithSummary("Передать владение группой")
.WithDescription("Только текущий owner и только действующему участнику; атомарно: прежний owner становится admin, " +
                 "новый — owner (в группе всегда ровно один owner). Событие owner_changed всем участникам (без follow).")
.Produces(204);

app.MapPatch("/chats/{chat_id:long}/members/{user_id:long}", async (HttpContext ctx, long chat_id, long user_id,
    [FromBody] SetRoleIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    var role = r.Role?.Trim();
    if (role is not ("admin" or "member"))
        return Err(400, "role must be 'admin' or 'member'");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var chat = ChatById(c, chat_id);
        if (chat.Type != "group") return Err(400, "roles apply only to group chats");
        if (RoleOf(c, chat.Id, me.Id) != "owner") return Err(403, "only the owner can change roles");
        var targetRole = RoleOf(c, chat.Id, user_id);
        if (targetRole == "") return Err(404, "user is not a member of this chat");
        if (targetRole == "owner") return Err(403, "cannot change the owner's role; transfer ownership instead");
        c.Execute("UPDATE chat_members SET role=@r WHERE chat_id=@c AND user_id=@u",
            new { r = role, c = chat.Id, u = user_id });
        await hub.PublishToUsers(MemberIds(c, chat.Id),
            new WsEvent("member_role_changed", chat.Id) { AlwaysDeliver = true, UserId = user_id, Role = role });
        return Results.NoContent();
    });
}).WithTags("Members").WithName("setMemberRole").WithSummary("Назначить/снять admin")
.WithDescription("Только owner; цель — действующий участник, не owner. role: admin | member. " +
                 "Событие member_role_changed всем участникам (без follow).")
.Produces(204);

// ================== Messages ==================

app.MapGet("/chats/{chat_id:long}/messages", (HttpContext ctx, long chat_id) =>
{
    var me = RequireUser(ctx);
    var (limitQ, beforeQ, perr) = ParsePaging(ctx);
    if (perr is not null) return perr;
    var lim = limitQ ?? 50;
    return Db.Read<IResult>(c =>
    {
        var chat = ChatById(c, chat_id);
        RequireMember(c, chat.Id, me.Id);
        var rows = c.Query<MessageRow>(
            """
            SELECT * FROM messages WHERE chat_id=@c
               AND (@b IS NULL OR id < @b)
               AND id NOT IN (SELECT message_id FROM message_hidden WHERE user_id=@u)
               ORDER BY id DESC LIMIT @l
            """,
            new { c = chat.Id, b = beforeQ, u = me.Id, l = lim }).Reverse().ToList();
        var members = MembersOf(c, chat.Id);
        return Results.Json(rows.Select(m => MsgOut(c, m, members)).ToList());
    });
}).WithTags("Messages").WithName("getHistory").WithSummary("История сообщений")
.WithDescription("Страница до limit (1..100, default 50) сообщений со id СТРОГО меньше before_id, " +
                 "от старых к новым внутри страницы; без before_id — начиная с самых последних. " +
                 "before_id и limit некорректны -> 400. Удалённые для всех — заглушки (content_state=deleted, " +
                 "text=null, без вложений/реакций); не сохранённые — content_state=not_stored, text=null. " +
                 "Скрытые «у себя» сообщения не возвращаются. read_at — агрегат: прочитано ВСЕМИ участниками, " +
                 "кроме отправителя; delivered_at — доставлено хотя бы одному онлайн-получателю.")
.Produces<List<MessageOut>>(200);

app.MapPost("/chats/{chat_id:long}/messages", async (HttpContext ctx, long chat_id, [FromBody] SendMessageIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    if (r.ClientId == Guid.Empty) return Err(400, "client_id must not be empty");
    var text = r.Text ?? "";
    if (text.Trim().Length < 1) return Err(400, "text must not be empty");
    if (text.Length > 4096) return Err(400, "text too long (max 4096)");
    if (r.ReplyToId is < 1) return Err(400, "reply_to_id must be a positive message id");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var chat = ChatById(c, chat_id);
        RequireMember(c, chat.Id, me.Id);
        if (r.ClientId is { } clientId)
        {
            var prior = c.QueryFirstOrDefault<MessageRow>(
                "SELECT * FROM messages WHERE sender_id=@u AND client_id=@key",
                new { u = me.Id, key = clientId.ToString("D") });
            if (prior is not null)
            {
                if (prior.Chat_Id != chat.Id) return Err(409, "client_id belongs to another chat");
                return Results.Json(new MessageIdOut(prior.Id, prior.Sent_At));
            }
        }
        if (r.ReplyToId is { } rid)
        {
            var target = c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = rid });
            if (target is null) return Err(404, "reply_to_id: message not found");
            if (target.Chat_Id != chat.Id) return Err(400, "reply_to_id: message is from another chat");
            // ответ на удалённое/несохранённое сообщение разрешён: ссылка ведёт на заглушку
        }
        var saveHistory = c.ExecuteScalar<long>(
            "SELECT save_history FROM user_settings WHERE user_id=@u", new { u = me.Id }) == 1;
        var id = c.ExecuteScalar<long>(
            "INSERT INTO messages(chat_id, sender_id, reply_to_id, text, sent_at, client_id) VALUES(@c, @s, @r, @t, @sa, @key); " +
            "SELECT last_insert_rowid()",
            new { c = chat.Id, s = me.Id, r = r.ReplyToId, t = saveHistory ? text : "", sa = Util.NowIso(), key = r.ClientId?.ToString("D") });
        var members = MembersOf(c, chat.Id);
        var memberIds = members.Select(m => m.User_Id).ToArray();
        var delivered = await hub.PublishToChat(chat.Id, memberIds, me.Id,
            new WsEvent("message", chat.Id)
            {
                // событие строим из исходного запроса: получатель видит текст даже при save_history=false
                Message = MsgOut(c, c.QueryFirst<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = id }),
                    members, textOverride: text)
            });
        if (delivered > 0)
            c.Execute("UPDATE messages SET delivered_at=@d WHERE id=@i", new { d = Util.NowIso(), i = id });
        return Results.Json(new MessageIdOut(id, c.ExecuteScalar<string>("SELECT sent_at FROM messages WHERE id=@id", new { id })), statusCode: 201);
    });
}).WithTags("Messages").WithName("sendMessage").WithSummary("Отправить сообщение")
.WithDescription("text 1..4096, reply_to_id? (положительный id сообщения ЭТОГО чата; ответ на заглушку удалённого " +
                 "разрешён). client_id? — UUID клиента: повтор для того же отправителя и чата возвращает прежний результат (200); " +
                 "новая отправка — 201. Ответ: message_id, sent_at. Настройка save_history ОТПРАВИТЕЛЯ: при false текст не сохраняется " +
                 "(заглушка с content_state=not_stored), но онлайн-получатели получают полный текст в WS-событии. " +
                 "Событие message — подписанным (follow).")
.Produces<MessageIdOut>(201);

app.MapPost("/chats/{chat_id:long}/read", async (HttpContext ctx, long chat_id, [FromBody] ReadIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    if (r.MessageId <= 0) return Err(400, "message_id must be a positive message id");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var chat = ChatById(c, chat_id);
        RequireMember(c, chat.Id, me.Id);
        var msg = c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = r.MessageId });
        if (msg is null || msg.Chat_Id != chat.Id)
            return Err(404, "message_id not found in this chat");
        c.Execute("""
            UPDATE chat_members SET last_read_message_id=@m, read_at=@ra
            WHERE chat_id=@c AND user_id=@u
              AND (last_read_message_id IS NULL OR last_read_message_id < @m)
            """, new { m = r.MessageId, ra = Util.NowIso(), c = chat.Id, u = me.Id });
        await hub.PublishToChat(chat.Id, MemberIds(c, chat.Id), me.Id,
            new WsEvent("read", chat.Id) { UserId = me.Id, LastReadMessageId = r.MessageId });
        return Results.NoContent();
    });
}).WithTags("Messages").WithName("markRead").WithSummary("Подтвердить прочтение")
.WithDescription("Курсор прочтения по last message_id: не уменьшается, распространяется на более ранние сообщения. " +
                 "GET истории само по себе ничего не помечает. Исключённый/посторонний не может подтверждать (403). " +
                 "Событие read (user_id, last_read_message_id) — подписанным участникам.")
.Produces(204);

app.MapPatch("/messages/{message_id:long}", async (HttpContext ctx, long message_id, [FromBody] EditMessageIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    var text = r.Text ?? "";
    if (text.Trim().Length < 1) return Err(400, "text must not be empty");
    if (text.Length > 4096) return Err(400, "text too long (max 4096)");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var m = c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = message_id })
            ?? throw new HttpException(404, "message not found");
        RequireMember(c, m.Chat_Id, me.Id);
        if (m.Sender_Id != me.Id) return Err(403, "only the author can edit a message");
        if (m.Deleted_At is not null) return Err(409, "message_deleted", "message_deleted");
        if (m.Text.Length == 0) return Err(409, "content_not_stored", "content_not_stored");
        c.Execute("UPDATE messages SET text=@t, edited_at=@ea WHERE id=@i",
            new { t = text, ea = Util.NowIso(), i = message_id });
        var upd = c.QueryFirst<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = message_id });
        await hub.PublishToChat(m.Chat_Id, MemberIds(c, m.Chat_Id), me.Id,
            new WsEvent("message_edited", m.Chat_Id) { Message = MsgOut(c, upd) });
        return Results.NoContent();
    });
}).WithTags("Messages").WithName("editMessage").WithSummary("Редактировать своё сообщение")
.WithDescription("Только автор; меняются text и edited_at. 409 message_deleted — сообщение удалено; " +
                 "409 content_not_stored — текст не сохранялся (save_history был выключен). Событие message_edited.")
.Produces(204);

app.MapDelete("/messages/{message_id:long}", async (HttpContext ctx, long message_id, [FromBody] DeleteMessageIn? r) =>
{
    var me = RequireUser(ctx, r?.UserSessionToken);
    var forEveryone = r?.ForEveryone;
    if (forEveryone is null)
        return Err(400, "for_everyone must be an explicit true (delete for everyone) or false (hide for me)");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var m = c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = message_id })
            ?? throw new HttpException(404, "message not found");
        RequireMember(c, m.Chat_Id, me.Id);
        if (forEveryone.Value)
        {
            var chat = ChatById(c, m.Chat_Id);
            var myRole = RoleOf(c, m.Chat_Id, me.Id);
            if (m.Sender_Id != me.Id && myRole is not ("owner" or "admin"))
                return Err(403, "only the author or the group owner/admin can delete for everyone");
            if (m.Deleted_At is not null) return Results.NoContent(); // идемпотентно
            // в одной записи: пометка удаления + отзыв вложений (строки и файлы) + снятие реакций
            var files = c.Query<string>("SELECT path_to_file FROM attachments WHERE message_id=@m", new { m = message_id }).ToList();
            c.Execute("UPDATE messages SET deleted_at=@d, text='' WHERE id=@i", new { d = Util.NowIso(), i = message_id });
            c.Execute("DELETE FROM attachments WHERE message_id=@m", new { m = message_id });
            c.Execute("DELETE FROM message_reactions WHERE message_id=@m", new { m = message_id });
            foreach (var f in files)
                try { if (File.Exists(f)) File.Delete(f); } catch { /* фоновая уборка подберёт */ }
            var upd = c.QueryFirst<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = message_id });
            await hub.PublishToChat(m.Chat_Id, MemberIds(c, m.Chat_Id), me.Id,
                new WsEvent("message_deleted", m.Chat_Id)
                {
                    MessageId = message_id, Message = MsgOut(c, upd), ForEveryone = true
                });
            return Results.NoContent();
        }
        // скрытие только у себя: событие всем действующим сессиям этого пользователя, без follow
        c.Execute("INSERT OR IGNORE INTO message_hidden(message_id, user_id) VALUES(@m, @u)",
            new { m = message_id, u = me.Id });
        await hub.PublishToUsers([me.Id],
            new WsEvent("message_deleted", m.Chat_Id)
            {
                AlwaysDeliver = true, MessageId = message_id, ForEveryone = false
            });
        return Results.NoContent();
    });
}).WithTags("Messages").WithName("deleteMessage").WithSummary("Удалить сообщение")
.WithDescription("for_everyone ОБЯЗАТЕЛЕН. true — для всех (автор или owner/admin группы): выставляется deleted_at, " +
                 "вложения отзываются (GET вложения -> 404, файлы удаляются), реакции снимаются, в истории остаётся " +
                 "заглушка без содержимого; повторный вызов идемпотентен. false — скрыть только у себя: сообщение и " +
                 "вложения остаются доступны остальным; событие message_deleted (for_everyone=false) уходит только " +
                 "действующим сессиям этого пользователя. Редактирование/вложения/реакции на удалённое -> 409 message_deleted.")
.Produces(204);

// ================== Reactions ==================

app.MapPost("/messages/{message_id:long}/reactions", async (HttpContext ctx, long message_id, [FromBody] ReactionIn r) =>
{
    var me = RequireUser(ctx, r.UserSessionToken);
    var emoji = (r.Emoji ?? "").Trim();
    if (emoji.Length is < 1 or > 32) return Err(400, "emoji must be 1-32 characters");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var m = c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = message_id })
            ?? throw new HttpException(404, "message not found");
        RequireMember(c, m.Chat_Id, me.Id);
        if (m.Deleted_At is not null) return Err(409, "message_deleted", "message_deleted");
        c.Execute("INSERT OR IGNORE INTO message_reactions(message_id, user_id, emoji) VALUES(@m, @u, @e)",
            new { m = message_id, u = me.Id, e = emoji });
        await hub.PublishToChat(m.Chat_Id, MemberIds(c, m.Chat_Id), me.Id,
            new WsEvent("reaction_added", m.Chat_Id) { MessageId = message_id, UserId = me.Id, Emoji = emoji });
        return Results.NoContent();
    });
}).WithTags("Reactions").WithName("addReaction").WithSummary("Поставить реакцию")
.WithDescription("Идемпотентно: (message, user, emoji) не дублируется. Участник чата; 409 message_deleted — " +
                 "на удалённое сообщение. Реакции на несохранённое (not_stored) сообщение разрешены — запись-заглушка существует.")
.Produces(204);

app.MapDelete("/messages/{message_id:long}/reactions/{emoji}", async (HttpContext ctx, long message_id, string emoji) =>
{
    var me = RequireUser(ctx);
    var em = Uri.UnescapeDataString(emoji ?? "").Trim();
    return await Db.WriteAsync<IResult>(async c =>
    {
        var m = c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = message_id })
            ?? throw new HttpException(404, "message not found");
        RequireMember(c, m.Chat_Id, me.Id);
        var removed = c.Execute("DELETE FROM message_reactions WHERE message_id=@m AND user_id=@u AND emoji=@e",
            new { m = message_id, u = me.Id, e = em });
        if (removed == 0) return Err(404, "reaction not found");
        await hub.PublishToChat(m.Chat_Id, MemberIds(c, m.Chat_Id), me.Id,
            new WsEvent("reaction_removed", m.Chat_Id) { MessageId = message_id, UserId = me.Id, Emoji = em });
        return Results.NoContent();
    });
}).WithTags("Reactions").WithName("removeReaction").WithSummary("Убрать свою реакцию")
.WithDescription("Удаляет только СВОЮ реакцию с данной emoji. 404 — такой реакции не было.")
.Produces(204);

// ================== Attachments ==================

app.MapPost("/messages/{message_id:long}/attachments", async (HttpContext ctx, long message_id) =>
{
    var me = RequireUser(ctx);
    IFormCollection form;
    try { form = await ctx.Request.ReadFormAsync(); }
    catch (InvalidOperationException) { return Err(400, "multipart/form-data with field 'file' required"); }
    if (form.Files.Count == 0) return Err(400, "multipart field 'file' required (можно несколько)");
    foreach (var f in form.Files)
        if (f.Length > 10_000_000)
            return Err(413, $"file {f.FileName} too large: {f.Length} bytes (max 10 MB)");
    return await Db.WriteAsync<IResult>(async c =>
    {
        var m = c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = message_id })
            ?? throw new HttpException(404, "message not found");
        RequireMember(c, m.Chat_Id, me.Id);
        if (m.Sender_Id != me.Id) return Err(403, "only the author can attach files to a message");
        if (m.Deleted_At is not null) return Err(409, "message_deleted", "message_deleted");
        if (m.Text.Length == 0) return Err(409, "content_not_stored", "content_not_stored");
        var results = new List<AttachmentOut>();
        foreach (var f in form.Files)
        {
            var dir = Path.Combine(filesRoot, "attachments", m.Id.ToString());
            Directory.CreateDirectory(dir);
            var diskName = $"{Guid.NewGuid():N}_{SafeFileName(f.FileName)}";
            var abs = Path.Combine(dir, diskName);
            await using (var fs = File.Create(abs)) await f.CopyToAsync(fs);
            var id = c.ExecuteScalar<long>(
                "INSERT INTO attachments(message_id, file_name, path_to_file, file_type, file_size_bytes) " +
                "VALUES(@m, @n, @p, @t, @s); SELECT last_insert_rowid()",
                new { m = m.Id, n = SafeFileName(f.FileName), p = abs, t = Trunc(f.ContentType, 32), s = f.Length });
            results.Add(new AttachmentOut(id, m.Id, SafeFileName(f.FileName), Trunc(f.ContentType, 32), f.Length));
        }
        foreach (var att in results)
            await hub.PublishToChat(m.Chat_Id, MemberIds(c, m.Chat_Id), me.Id,
                new WsEvent("attachment_added", m.Chat_Id) { MessageId = m.Id, Attachment = att });
        return Results.Json(results, statusCode: 201);
    });
}).WithTags("Attachments").WithName("uploadAttachments")
.WithSummary("Вложить файлы в своё сообщение")
.WithDescription("multipart/form-data, поле file (можно несколько файлов, каждый до 10 МБ -> 413 при превышении). " +
                 "Только автор сохранённого сообщения: 409 message_deleted / 409 content_not_stored. " +
                 "201 -> МАССИВ AttachmentOut. Удаление сообщения для всех отзывает вложения (GET -> 404).")
.DisableAntiforgery()
.Produces(201);

app.MapGet("/attachments/{attachment_id:long}", (HttpContext ctx, long attachment_id) =>
{
    var me = RequireUser(ctx);
    return Db.Read<IResult>(c =>
    {
        var att = c.QueryFirstOrDefault<AttachmentRow>("SELECT * FROM attachments WHERE id=@i",
            new { i = attachment_id }) ?? throw new HttpException(404, "attachment not found");
        var msg = c.QueryFirst<MessageRow>("SELECT * FROM messages WHERE id=@m", new { m = att.Message_Id });
        RequireMember(c, msg.Chat_Id, me.Id);
        if (!File.Exists(att.Path_To_File)) return Err(404, "file missing on disk");
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{att.File_Name}\"";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(att.Path_To_File, att.File_Type, att.File_Name);
    });
}).WithTags("Attachments").WithName("downloadAttachment").WithSummary("Скачать вложение")
.WithDescription("Только участник чата (403 иначе). Бинарный ответ с фактическим Content-Type, " +
                 "Content-Disposition: attachment и X-Content-Type-Options: nosniff. " +
                 "Вложения удалённого для всех сообщения -> 404.")
.Produces(200);

// ================== WebSocket ==================

app.MapGet("/ws/protocol", () => Results.Json(WsProtocol.Doc))
   .WithTags("WebSocket").WithName("wsProtocol").WithSummary("Описание протокола WebSocket")
   .WithDescription("Полное описание протокола WS /ws: авторизация, действия, события, close codes, " +
                    "повторный follow, переподключение и догрузка истории.");

app.Map("/ws", async ctx =>
{
    var token = ctx.Request.Query["user_session_token"].ToString();
    if (string.IsNullOrWhiteSpace(token)) token = ctx.Request.Query["token"].ToString();
    var tokenHash = Auth.TokenHash(token);
    var user = Db.Read(c => c.QueryFirstOrDefault<SessionUser>(
        """
        SELECT s.user_id AS Id, u.tag AS Login, s.token_hash AS TokenHash FROM user_sessions s
        JOIN users u ON u.id = s.user_id
        WHERE s.token_hash = @h AND s.expires_at > @now
        """, new { h = tokenHash, now = Util.NowIso() }));
    if (user is null) { ctx.Response.StatusCode = 401; ctx.Items["ErrorBodyWritten"] = true; return; }
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        ctx.Items["ErrorBodyWritten"] = true;
        await ctx.Response.WriteAsync("websocket required");
        return;
    }
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    using var reg = hub.Register(user.Id, tokenHash, ws);
    TouchLastSeen(user.Id);

    var hello = JsonSerializer.SerializeToUtf8Bytes(new WsEvent("connected") { UserId = user.Id });
    await ws.SendAsync(hello, WebSocketMessageType.Text, true, CancellationToken.None);

    var buf = new byte[4096];
    try
    {
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            string? action = null;
            long? followChatId = null;
            try
            {
                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
                if (root.TryGetProperty("chat_id", out var cid) && cid.TryGetInt64(out var fcid))
                    followChatId = fcid;
            }
            catch { /* malformed JSON -> error ниже */ }

            object reply;
            switch (action)
            {
                case "ping":
                    reply = new WsEvent("pong");
                    break;
                case "follow" when followChatId is { } fc && fc > 0:
                    // перед подпиской проверяем, что сессия ещё действительна и пользователь — участник чата
                    var sessionOk = Db.Read(c => c.ExecuteScalar<long?>(
                        "SELECT 1 FROM user_sessions WHERE token_hash=@h AND expires_at > @now",
                        new { h = tokenHash, now = Util.NowIso() }) == 1);
                    var memberOk = sessionOk && Db.Read(c => RoleOf(c, fc, user.Id) != "");
                    if (!sessionOk)
                    {
                        await ws.CloseAsync((WebSocketCloseStatus)Hub.CloseSessionExpired,
                            "session expired or revoked", CancellationToken.None);
                        return;
                    }
                    if (memberOk) reg.Connection.Followed[fc] = 1;
                    reply = memberOk
                        ? new WsEvent("followed", fc)
                        : new WsEvent("error", fc) { Detail = "not a member of this chat" };
                    break;
                default:
                    reply = new WsEvent("error") { Detail = "unknown action or malformed JSON" };
                    break;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
}).WithTags("WebSocket").WithName("ws").WithSummary("WebSocket-соединение (см. GET /ws/protocol)");

app.Run();

/// <summary>Ошибка запроса с HTTP-статусом и кодом; маппится в единый ErrorResponse.</summary>
public class HttpException(int status, string message, string? code = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

public record ErrorResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string Code,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string Error,
    [property: System.Text.Json.Serialization.JsonPropertyName("details")] object? Details,
    [property: System.Text.Json.Serialization.JsonPropertyName("trace_id")] string TraceId);

/// <summary>Документация WS-протокола (GET /ws/protocol).</summary>
public static class WsProtocol
{
    public static object Doc => new
    {
        url = "ws://<host>/messenger/ws?user_session_token=<token>",
        auth = "Токен сессии передаётся query-параметром user_session_token (для браузерного WS). " +
               "Соединение привязано к сессии: logout (POST /auth/logout) этой сессии закрывает её сокеты " +
               "close-кодом 4001; истечение — 4002 (проверяется при follow и фоновым таймером раз в минуту).",
        close_codes = new { c4001 = "session revoked (logout)", c4002 = "session expired" },
        actions = new object[]
        {
            new { action = "ping", reply = "pong" },
            new { action = "follow", args = "chat_id", reply = "followed | error(not a member of this chat)",
                  note = "повторный follow безвреден; требует действующей сессии и членства" },
            new { action = "любое другое / malformed JSON", reply = "error{detail}" }
        },
        events = new object[]
        {
            new { type = "connected", follow = false, payload = "user_id" },
            new { type = "pong", follow = false },
            new { type = "followed", follow = false, payload = "chat_id" },
            new { type = "chat_created", follow = false, payload = "chat_id, chat", to = "участникам нового чата" },
            new { type = "chat_hidden", follow = false, payload = "chat_id", to = "только скрывшему" },
            new { type = "chat_deleted", follow = false, payload = "chat_id", to = "последнему участнику закрывшейся группы" },
            new { type = "message", follow = true, payload = "chat_id, message (полный text, включая not_stored)" },
            new { type = "message_edited", follow = true, payload = "chat_id, message" },
            new { type = "message_deleted", follow = true,
                  payload = "chat_id, message_id/message, for_everyone",
                  note = "for_everyone=false — событие только сессиям самого пользователя (без follow)" },
            new { type = "reaction_added", follow = true, payload = "chat_id, message_id, user_id, emoji" },
            new { type = "reaction_removed", follow = true, payload = "chat_id, message_id, user_id, emoji" },
            new { type = "attachment_added", follow = true, payload = "chat_id, message_id, attachment" },
            new { type = "member_added", follow = true, payload = "chat_id, user_id, user" },
            new { type = "member_removed", follow = true, payload = "chat_id, user_id", to = "включая исключённого" },
            new { type = "member_left", follow = true, payload = "chat_id, user_id" },
            new { type = "member_role_changed", follow = false, payload = "chat_id, user_id, role" },
            new { type = "owner_changed", follow = false, payload = "chat_id, user_id (новый владелец)" },
            new { type = "read", follow = true, payload = "chat_id, user_id, last_read_message_id" }
        },
        reconnect = "После переподключения: повторный handshake -> follow нужных чатов -> догрузка пропущенного " +
                    "через GET /chats/{id}/messages?before_id=<последний известный id>.",
        delivery = "delivered_at в сообщении ставится, когда событие доставлено хотя бы одному онлайн-получателю. " +
                   "Прочтение — только явное POST /chats/{id}/read."
    };
}
