using System.Data;
using Dapper;

namespace MessengerBackend.Core;

public sealed class SessionUser
{
    public long Id { get; set; }
    public string Login { get; set; } = "";
    public string TokenHash { get; set; } = "";
}

/// <summary>Аккаунты и сессии. Единственный источник проверки токена для HTTP и WS.</summary>
public static class Accounts
{
    public const int SessionTtlDays = 30;

    /// <summary>Токен только из Authorization: Bearer (REST). WS использует свой query-вариант.</summary>
    public static string? BearerToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : null;
    }

    public static SessionUser RequireUser(HttpContext ctx)
    {
        var token = BearerToken(ctx);
        if (string.IsNullOrWhiteSpace(token))
            throw new HttpException(401, "missing bearer token");
        return RequireSession(token);
    }

    public static SessionUser RequireSession(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new HttpException(401, "missing token");
        var hash = Auth.TokenHash(token);
        var user = Db.Read(c => c.QueryFirstOrDefault<SessionUser>(
            """
            SELECT s.user_id AS Id, u.tag AS Login, s.token_hash AS TokenHash
            FROM user_sessions s JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = @h AND s.expires_at > @now
            """, new { h = hash, now = Util.NowIso() }));
        if (user is null)
            throw new HttpException(401, "недействительная или истёкшая сессия");
        TouchLastSeen(user.Id);
        return user;
    }

    public static SessionUser? FindSession(string token)
    {
        var hash = Auth.TokenHash(token);
        return Db.Read(c => c.QueryFirstOrDefault<SessionUser>(
            """
            SELECT s.user_id AS Id, u.tag AS Login, s.token_hash AS TokenHash
            FROM user_sessions s JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = @h AND s.expires_at > @now
            """, new { h = hash, now = Util.NowIso() }));
    }

    public static string CreateSession(IDbConnection c, long userId) => Auth.CreateSession(c, userId);

    public static void DropSession(IDbConnection c, string tokenHash) =>
        c.Execute("DELETE FROM user_sessions WHERE token_hash=@h", new { h = tokenHash });

    public static HashSet<string> ExpiredSessionHashes() => Db.Read(c =>
        c.Query<string>("SELECT token_hash FROM user_sessions WHERE expires_at <= @now",
            new { now = Util.NowIso() }).ToHashSet());

    static void TouchLastSeen(long userId)
    {
        Db.Write(c => c.Execute("UPDATE users SET last_seen_at=@t WHERE id=@u AND (last_seen_at IS NULL OR last_seen_at < @cutoff)",
            new { t = Util.NowIso(), cutoff = Util.NowIso(TimeSpan.FromMinutes(-1)), u = userId }));
    }
}
