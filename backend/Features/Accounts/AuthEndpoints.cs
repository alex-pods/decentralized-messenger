using System.Data;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Hubs;

namespace MessengerBackend.Endpoints;

public static class AuthEndpoints
{


    public static void MapAuthEndpoints(this WebApplication app)
    {
        var hub = app.Services.GetRequiredService<Hub>();

        app.MapPost("/auth/register", ([FromBody] RegisterIn r, HttpContext ctx) =>
        {
            var login = r.Login?.Trim() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(login, @"^[A-Za-z0-9_.\-]{3,16}$"))
                return Errors.Err(400, "login (tag) должен быть 3-16 символов: буквы, цифры, _ . -");
            if (string.IsNullOrEmpty(r.Password) || r.Password.Length < 8 || r.Password.Length > 1024)
                return Errors.Err(400, "password должен быть от 8 до 1024 символов");
            var passwordHash = Auth.HashPassword(r.Password);
            return Db.Write<IResult>(c =>
            {
                if (c.ExecuteScalar<long?>("SELECT 1 FROM users WHERE tag=@t COLLATE NOCASE LIMIT 1",
                        new { t = login }) == 1)
                    return Errors.Err(409, "Указанный login уже занят", "login_taken");
                var id = c.ExecuteScalar<long>(
                    "INSERT INTO users(tag, password_hash, display_name, join_date) VALUES(@t, @p, @d, @j); " +
                    "SELECT last_insert_rowid()",
                    new { t = login, p = passwordHash, d = login, j = Util.NowIso() });
                return Results.StatusCode(StatusCodes.Status201Created);
            });
        })
        .RequireRateLimiting("auth").WithTags("Auth").WithName("register").WithSummary("Регистрация")
        .WithDescription("Создаёт пользователя: tag = login (3-16: буквы, цифры, _ . -), пароль >= 8, " +
                         "display_name = login. 201 без тела. 409 — login занят. Лимит 30 запросов/мин на IP.")
        .Produces(201);

        app.MapPost("/auth/login", ([FromBody] LoginIn r, HttpContext ctx) =>
        {
            if (string.IsNullOrEmpty(r?.Login))
                return Errors.Err(400, "login обязателен");
            if (r.Login.Length > 64 || r.Password is null || r.Password.Length > 1024)
                return Errors.Err(401, "Неверный login или password");
            var u = Db.Read(c => c.QueryFirstOrDefault<UserRow>(
                "SELECT * FROM users WHERE tag=@t COLLATE NOCASE", new { t = r.Login.Trim() }));
            var valid = Auth.VerifyPassword(r.Password, u?.Password_Hash ?? DummyPasswordHash);
            if (u is null || !valid) return Errors.Err(401, "Неверный login или password");
            return Db.Write<IResult>(c =>
            {
                // Upgrade legacy PBKDF2 hashes on successful login.
                if (!u.Password_Hash.StartsWith("pbkdf2_sha256$600000$", StringComparison.Ordinal))
                    c.Execute("UPDATE users SET password_hash=@p WHERE id=@i", new { p = Auth.HashPassword(r.Password), i = u.Id });
                return Results.Json(new TokenOut(Accounts.CreateSession(c, u.Id),
                    Util.NowIso(TimeSpan.FromDays(Accounts.SessionTtlDays))));
            });
        })
        .RequireRateLimiting("auth").WithTags("Auth").WithName("login").WithSummary("Вход")
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

    private static readonly string DummyPasswordHash = Auth.HashPassword("dummy-password-not-an-account");
}
