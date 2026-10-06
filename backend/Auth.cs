using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using Dapper;

namespace MessengerBackend;

/// <summary>Пароли: PBKDF2-SHA256; сессии: случайный токен, в БД лежит только его SHA-256.</summary>
public static class Auth
{
    private const int Pbkdf2Iterations = 240_000;
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2_sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256") return false;
        var iters = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static DateTime ExpiresUtc() => DateTime.UtcNow.Add(SessionTtl);

    public static string CreateSession(IDbConnection c, long userId)
    {
        var token = NewToken();
        c.Execute(
            "INSERT INTO user_sessions(user_id, token_hash, created_at, expires_at) VALUES(@u, @h, @ca, @ea)",
            new { u = userId, h = TokenHash(token), ca = Util.NowIso(), ea = Util.NowIso(SessionTtl) });
        return token;
    }

    public static void DropSession(IDbConnection c, string tokenHash) =>
        c.Execute("DELETE FROM user_sessions WHERE token_hash=@h", new { h = tokenHash });

    /// <summary>Достаёт токен из Authorization: Bearer / X-Session-Token / ?user_session_token=</summary>
    public static string? TokenFromContext(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var h = ctx.Request.Headers["X-Session-Token"].ToString();
        if (!string.IsNullOrWhiteSpace(h)) return h.Trim();
        var q = ctx.Request.Query["user_session_token"].ToString();
        return string.IsNullOrWhiteSpace(q) ? null : q.Trim();
    }
}

public class SessionUser
{
    public long Id { get; set; }
    public string Login { get; set; } = "";
    public string TokenHash { get; set; } = "";
}

/// <summary>Простейший in-memory лимитер для /auth/*: 30 запросов/мин с одного IP.</summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public bool Allow(string key, int limit, TimeSpan window)
    {
        var now = DateTime.UtcNow;
        var b = _buckets.GetOrAdd(key, _ => new Bucket());
        lock (b)
        {
            if (now - b.WindowStart > window) { b.WindowStart = now; b.Count = 0; }
            b.Count++;
            return b.Count <= limit;
        }
    }

    private class Bucket
    {
        public DateTime WindowStart { get; set; } = DateTime.UtcNow;
        public int Count { get; set; }
    }
}

public static class Util
{
    /// <summary>UTC ISO-8601 с миллисекундами и Z; лексикографически сортируется по времени.</summary>
    public static string NowIso(TimeSpan offset = default) =>
        DateTime.UtcNow.Add(offset).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
