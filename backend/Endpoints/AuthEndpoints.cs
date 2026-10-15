using System.Data;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Hubs;

namespace MessengerBackend.Endpoints;

public static class AuthEndpoints
{
    private static readonly RateLimiter Limiter = new();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var hub = app.Services.GetRequiredService<Hub>();

        app.MapPost("/auth/register", ([FromBody] RegisterIn r, HttpContext ctx) =>
        {
            if (!Limiter.Allow(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", 30, TimeSpan.FromMinutes(1)))
                return RateLimited(ctx);
            var login = r.Login?.Trim() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(login, @"^[A-Za-z0-9_.\-]{3,16}$"))
                return Errors.Err(400, "login (tag) должен быть 3-16 символов: буквы, цифры, _ . -");
            if (string.IsNullOrEmpty(r.Password) || r.Password.Length < 8)
                return Errors.Err(400, "password должен быть не короче 8 символов");
            return Db.Write<IResult>(c =>
            {
                if (c.ExecuteScalar<long?>("SELECT 1 FROM users WHERE tag=@t COLLATE NOCASE LIMIT 1",
                        new { t = login }) == 1)
                    return Errors.Err(409, "Указанный login уже занят", "login_taken");
                var id = c.ExecuteScalar<long>(
                    "INSERT INTO users(tag, password_hash, display_name, join_date) VALUES(@t, @p, @d, @j); " +
                    "SELECT last_insert_rowid()",
                    new { t = login, p = Auth.HashPassword(r.Password), d = login, j = Util.NowIso() });
                return Results.StatusCode(StatusCodes.Status201Created);
            });
        })
        .WithTags("Auth").WithName("register").WithSummary("Регистрация")
        .WithDescription("Создаёт пользователя: tag = login (3-16: буквы, цифры, _ . -), пароль >= 8, " +
                         "display_name = login. 201 без тела. 409 — login занят. Лимит 30 запросов/мин на IP.")
        .Produces(201);

        app.MapPost("/auth/login", ([FromBody] LoginIn r, HttpContext ctx) =>
        {
            if (!Limiter.Allow(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", 30, TimeSpan.FromMinutes(1)))
                return RateLimited(ctx);
            if (string.IsNullOrEmpty(r?.Login))
                return Errors.Err(400, "login обязателен");
            return Db.Read<IResult>(c =>
            {
                var u = c.QueryFirstOrDefault<UserRow>("SELECT * FROM users WHERE tag=@t COLLATE NOCASE",
                    new { t = r.Login.Trim() });
                if (u is null || !Auth.VerifyPassword(r.Password ?? "", u.Password_Hash))
                    return Errors.Err(401, "Неверный login или password");
                return Results.Json(new TokenOut(Accounts.CreateSession(c, u.Id),
                    Util.NowIso(TimeSpan.FromDays(Accounts.SessionTtlDays))));
            });
        })
        .WithTags("Auth").WithName("login").WithSummary("Вход")
        .WithDescription("Возвращает user_session_token (30 дней) и expires_at. " +
                         "Токен далее передаётся в Authorization: Bearer. 401 — неверные учётные данные.")
        .Produces<TokenOut>(200);

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            var me = Accounts.RequireUser(ctx);
            var hash = Auth.TokenHash(Accounts.BearerToken(ctx)!);
            await Db.WriteAsync(async c => { Accounts.DropSession(c, hash); return 0; });
            _ = hub.CloseSession(hash, Hub.CloseSessionRevoked, "session revoked");
            return Results.NoContent();
        })
        .WithTags("Auth").WithName("logout").WithSummary("Выход")
        .WithDescription("Удаляет ТОЛЬКО эту сессию и закрывает её WebSocket-соединения (close 4001). " +
                         "Другие сессии пользователя остаются действительными.")
        .Produces(204);
    }

    static IResult RateLimited(HttpContext ctx)
    {
        ctx.Response.Headers.RetryAfter = "60";
        return Errors.Err(429, "слишком много запросов, повторите позже");
    }
}
