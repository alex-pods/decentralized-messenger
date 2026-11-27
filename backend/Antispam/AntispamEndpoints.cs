using System.Net.WebSockets;
using System.Text.Json;
using Messenger.Api.Common.Errors;
using Messenger.Api.Services;

namespace Messenger.Api.Antispam;

public static class AntispamEndpoints
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed class PublicCapabilities;

    public static void MapAntispamEndpoints(this WebApplication app)
    {
        app.MapGet("/capabilities", (AntispamService service, AntispamMediaCatalog media) => Results.Ok(new { antispam = new { enabled = service.Enabled, protocol = 1, concentration = service.Enabled && media.Available } }))
            .WithMetadata(new PublicCapabilities()).WithSummary("Возможности сервера; отсутствие antispam означает выключенный антиспам");
        app.MapGet("/antispam/status", (HttpContext ctx, AntispamService service) =>
            Results.Ok(new { enabled = service.Enabled, required = service.Required(CurrentUser.GetId(ctx)) }));
        app.MapGet("/antispam/media", (AntispamMediaCatalog media) => Results.Ok(new { version=1, clips=media.Clips }));
        app.MapGet("/antispam/media/{id}", (string id, AntispamMediaCatalog media, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl="private, max-age=86400";
            return Results.File(media.PathFor(id),"video/mp4",enableRangeProcessing:true);
        });
        app.MapPost("/antispam/challenges", (HttpContext ctx, AntispamService service) =>
            Results.Ok(new { id = service.Create(CurrentUser.GetId(ctx)), countdownMs = PrecisionRound.CountdownMs, durationMs = PrecisionRound.DurationMs, requiredScore = PrecisionRound.RequiredScore }));
        app.MapDelete("/antispam/challenges/{id}", (string id, HttpContext ctx, AntispamService service) =>
        {
            service.Abort(CurrentUser.GetId(ctx), id);
            return Results.NoContent();
        });
        app.MapGet("/antispam/challenges/{id}/play", Play).WithSummary("Авторизованный WebSocket проверки; разрыв соединения означает провал");
    }

    static async Task Play(string id, HttpContext ctx, AntispamService service, IAuthService auth)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) throw AppException.BadRequest("WebSocket required");
        // Desktop supplies Authorization; no tokens in URL, browser storage or logs.
        var userId = CurrentUser.GetId(ctx);
        var initial = service.Start(userId, id);
        WebSocket? socket = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        lifetime.CancelAfter(TimeSpan.FromSeconds(40));
        var buffer = new byte[2048];
        try
        {
            socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await Write(initial);
            var validatedAt = Environment.TickCount64;
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
                if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) break;
                if (Environment.TickCount64 - validatedAt >= 1000)
                {
                    if (await auth.GetUserIdByToken(CurrentUser.GetToken(ctx)) != userId) break;
                    validatedAt = Environment.TickCount64;
                }
                var input = JsonSerializer.Deserialize<ChallengeInput>(buffer.AsSpan(0, result.Count), Json);
                if (input == null) break;
                var state = service.Input(userId, id, input);
                await Write(state);
                if (state.Phase is "failed" or "passed")
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, state.Phase, lifetime.Token);
                    // Do not abort immediately after sending the terminal result: TCP reset can
                    // discard the final frame before the desktop has received it.
                    await socket.ReceiveAsync(buffer.AsMemory(),lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2),lifetime.Token);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or TimeoutException or JsonException or AppException) { }
        finally { service.Abort(userId, id); if(socket?.State!=WebSocketState.Closed)socket?.Abort(); socket?.Dispose(); }

        Task Write(ChallengeState state) => socket!.SendAsync(JsonSerializer.SerializeToUtf8Bytes(state, Json), WebSocketMessageType.Text, true, lifetime.Token);
    }
}
