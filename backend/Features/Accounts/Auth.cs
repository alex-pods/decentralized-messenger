using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using Dapper;

namespace MessengerBackend;

/// <summary>Пароли: PBKDF2-SHA256; сессии: случайный токен, в БД только его SHA-256.</summary>
public static class Auth
{
    private const int Pbkdf2Iterations = 600_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2_sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2_sha256" ||
                !int.TryParse(parts[1], out var iters) || iters < 10_000 || iters > 2_000_000) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string CreateSession(IDbConnection c, long userId)
    {
        var token = NewToken();
        c.Execute(
            "INSERT INTO user_sessions(user_id, token_hash, created_at, expires_at) VALUES(@u, @h, @ca, @ea)",
            new { u = userId, h = TokenHash(token), ca = Util.NowIso(), ea = Util.NowIso(TimeSpan.FromDays(30)) });
        return token;
    }
}

public static class Util
{
    /// <summary>UTC ISO-8601 с миллисекундами и Z; лексикографически сортируется по времени.</summary>
    public static string NowIso(TimeSpan offset = default) =>
        DateTime.UtcNow.Add(offset).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
}
