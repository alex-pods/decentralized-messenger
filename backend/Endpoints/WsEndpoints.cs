using System.Net.WebSockets;
using System.Text.Json;
using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Hubs;

namespace MessengerBackend.Endpoints;

public static class WsEndpoints
{
    public static void MapWsEndpoints(this WebApplication app)
    {
        var hub = app.Services.GetRequiredService<Hub>();

        app.MapGet("/health", () => Results.Json(new
        { status = "ok", service = "messenger-backend", version = "4.0.0" }))
           .WithTags("Health").WithName("health").WithSummary("Живость сервиса");

        app.MapGet("/ws/protocol", () => Results.Json(ProtocolDoc))
           .WithTags("WebSocket").WithName("wsProtocol").WithSummary("Описание протокола WebSocket")
           .WithDescription("Авторизация, действия, события, close codes, повторный follow, " +
                            "переподключение и догрузка истории.");

        app.Map("/ws", async ctx =>
        {
            var token = ctx.Request.Query["user_session_token"].ToString();
            if (string.IsNullOrWhiteSpace(token)) token = ctx.Request.Query["token"].ToString();
            var session = Accounts.FindSession(token);
            if (session is null)
            {
                ctx.Response.StatusCode = 401;
                ctx.Items["ErrorBodyWritten"] = true; // без тела: handshake-этап
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Items["ErrorBodyWritten"] = true;
                await ctx.Response.WriteAsync("websocket required");
                return;
            }
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await using var reg = hub.Register(session.Id, session.TokenHash, ws);
            var writer = RunWriter(hub, reg.Connection);

            try
            {
                await Hub.SendControlAsync(reg.Connection, new WsEvent("connected") { UserId = session.Id },
                    CancellationToken.None);
                var buf = new byte[4096];
                while (true)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, res.Count);
                    } while (!res.EndOfMessage);
                    if (res.MessageType == WebSocketMessageType.Close) break;

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
                            // те же правила доступа, что и в HTTP: сессия действительна + членство в чате
                            var sessionOk = Accounts.FindSession(token) is not null;
                            if (!sessionOk)
                            {
                                await hub.CloseSession(session.TokenHash, Hub.CloseSessionExpired,
                                    "session expired or revoked");
                                reply = new WsEvent("error") { Detail = "session expired or revoked" };
                                break;
                            }
                            var memberOk = Db.Read(c => Chats.RoleOf(c, fc, session.Id) != "");
                            if (memberOk) reg.Connection.Followed[fc] = 1;
                            reply = memberOk
                                ? new WsEvent("followed", fc)
                                : new WsEvent("error", fc) { Detail = "not a member of this chat" };
                            break;
                        default:
                            reply = new WsEvent("error") { Detail = "unknown action or malformed JSON" };
                            break;
                    }
                    if (ws.State != WebSocketState.Open) break;
                    await Hub.SendControlAsync(reg.Connection, reply, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                await reg.DisposeAsync();
                await writer;
            }
        }).WithTags("WebSocket").WithName("ws").WithSummary("WebSocket-соединение (см. GET /ws/protocol)");
    }

    static Task RunWriter(Hub hub, Hub.Conn conn) =>
        Task.Run(async () =>
        {
            try { await Hub.WriteLoopAsync(conn, CancellationToken.None); }
            catch { /* receive-цикл приберёт соединение */ }
        });

    static object ProtocolDoc => new
    {
        url = "ws://<host>/messenger/ws?user_session_token=<token>",
        auth = "Токен сессии — query-параметр user_session_token (единственный способ для браузерного WS; " +
               "REST использует Authorization: Bearer). Соединение привязано к сессии: logout (POST /auth/logout) " +
               "закрывает только сокеты этой сессии (4001); истечение — 4002 (проверка при follow и фоном раз в минуту).",
        close_codes = new
        {
            c1001 = "server shutting down (graceful)",
            c4001 = "session revoked (logout)",
            c4002 = "session expired",
            c4008 = "slow consumer: очередь доставки (256 событий) переполнена — переподключитесь и догрузите историю"
        },
        actions = new object[]
        {
            new { action = "ping", reply = "pong" },
            new
            {
                action = "follow",
                args = "chat_id",
                reply = "followed | error(not a member of this chat)",
                note = "повторный follow безвреден; требует действующей сессии и членства"
            },
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
            new { type = "message", follow = true, payload = "chat_id, message (MessageOut, как в HTTP-истории)" },
            new { type = "message_edited", follow = true, payload = "chat_id, message" },
            new
            {
                type = "message_deleted", follow = true,
                payload = "chat_id, message_id/message, for_everyone",
                note = "for_everyone=false — событие только сессиям самого пользователя (без follow)"
            },
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
        reconnect = "После переподключения: повторный handshake -> follow нужных чатов -> синхронизация " +
                    "пропущенного через GET /chats/{id}/messages?after_id=<последний известный id> — сервер вернёт " +
                    "сообщения С id БОЛЬШЕ курсора по возрастанию; листайте до пустой страницы. Курсоры можно " +
                    "совмещать (after_id + before_id — окно), дубли устраняйте по message_id. Важно: сначала " +
                    "follow, затем загрузка (или наоборот — гонка закрыта дедупликацией по id). Восстановление " +
                    "после 4008 — тем же способом. before_id предназначен для обычной пагинации назад по истории.",
        delivery = "delivered_at ставится, когда событие доставлено хотя бы одному онлайн-получателю. " +
                   "Прочтение — только явное POST /chats/{id}/read.",
        scale = "События доставляются в рамках одного процесса сервера; несколько экземпляров не поддерживаются."
    };
}
