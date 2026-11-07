using System.Net.WebSockets;
using System.Text.Json;
using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Realtime;
using Messenger.Api.Repositories;
using Messenger.Api.Services;
using Messenger.Api.Infrastructure;

namespace Messenger.Api.Endpoints;

public static class WsEndpoints
{
    public static void MapWsEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () =>
            Results.Json(new
            {
                status = "ok",
                service = "messenger-backend-v2"
            }));

        app.MapGet("/ws/protocol", (HttpContext context) =>
            Results.Json(
                WsProtocol.Describe(
                    context.Request.PathBase.Value ?? "")));
        app.Map("/ws", async (
            HttpContext context,
            WsConnectionRegistry registry,
            IChatMembershipRepository memberships,
            IAuthService auth) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("websocket required");
                return;
            }

            var userId = CurrentUser.GetId(context);
            var tokenHash = SessionTokenHash.Compute(CurrentUser.GetToken(context));

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var connection = registry.Register(userId, socket, tokenHash);

            using var receiveLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                connection.Closed);

            var ct = receiveLifetime.Token;
            var writer = connection.RunWriterAsync(context.RequestAborted);

            try
            {
                try
                {
                    await auth.GetUserIdByToken(CurrentUser.GetToken(context));
                }
                catch (AppException exception)
                    when (exception.Status == StatusCodes.Status401Unauthorized)
                {
                    connection.RequestClose(4002, "session expired or revoked");
                    return;
                }
                var connected = new WsEventResponse("connected", UserId: userId);
                var payload = JsonSerializer.SerializeToUtf8Bytes(connected);

                if (!connection.TryEnqueue(payload))
                    return;

                var buffer = new byte[4096];

                const int maxMessageSize = 16 * 1024;

                while(socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult result;

                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            connection.RequestClose(1000, "bye");
                            return;
                        }

                        if(result.MessageType == WebSocketMessageType.Binary ||
                            message.Length + result.Count > maxMessageSize)
                        {
                            connection.RequestClose(1009, "text frames up to 16 KiB");
                            return;
                        }

                        message.Write(buffer, 0, result.Count);
                    }
                    while(!result.EndOfMessage);


                    WsCommandRequest? command = null;

                    try
                    {
                        command = JsonSerializer.Deserialize<WsCommandRequest>(message.ToArray());
                    }
                    catch (JsonException)
                    {
                        
                    }

                    try
                    {
                        await auth.GetUserIdByToken(CurrentUser.GetToken(context));
                    }
                    catch(AppException exception)
                        when(exception.Status == StatusCodes.Status401Unauthorized)
                    {
                        connection.RequestClose(4002, "session expired or revoked");
                        return;
                    }
                    WsEventResponse reply;

                    switch (command?.Action)
                    {
                        case "ping":
                            reply = new WsEventResponse("pong");
                            break;
                        
                        case "follow" when command?.ChatId is long chatId && chatId > 0:
                            if(!await memberships.IsMember(chatId, userId))
                            {
                                reply = new WsEventResponse(
                                    "error",
                                    ChatId: chatId,
                                    Detail: "not a member of this chat"
                                );

                                break;
                            }

                            connection.Follow(chatId);
                            reply = new WsEventResponse("followed", ChatId: chatId);
                            break;

                        default:
                            reply = new WsEventResponse(
                                "error",
                                Detail: "unknown action or malformed JSON");
                            break;
                    }

                    var replyBytes = JsonSerializer.SerializeToUtf8Bytes(reply);

                    if (!connection.TryEnqueue(replyBytes))
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                
            }
            catch (WebSocketException)
            {
                
            }
            finally
            {
                registry.Remove(connection.Id);
                connection.RequestClose(1000, "bye");
                await writer;
            }
        });
    }
}