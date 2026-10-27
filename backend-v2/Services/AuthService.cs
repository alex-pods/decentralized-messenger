using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;

namespace Messenger.Api.Services;

public interface IAuthService
{
    Task Register(string? login, string? password);
    Task<TokenResponse> Login(string? login, string? password);
    Task Logout(string token);
    Task<long> GetUserIdByToken(string token);
}

public sealed class AuthService(IUsersRepository users, ISessionsRepository sessions) : IAuthService
{
    private const int PasswordIterations = 600_000;
    private const int SessionDays = 30;
    private static readonly Regex ValidLogin = new("^[A-Za-z0-9_.-]{3,16}$", RegexOptions.Compiled);
    private static readonly string DummyPasswordHash = HashPassword("dummy-password-not-an-account");

    public async Task Register(string? login, string? password)
    {
        var normalizedLogin = login?.Trim();
        if (normalizedLogin is null || !ValidLogin.IsMatch(normalizedLogin))
            throw AppException.BadRequest("login must be 3-16 characters: letters, digits, _, . or -");
        if (password is null || password.Length is < 8 or > 1024)
            throw AppException.BadRequest("password must be 8-1024 characters");

        try
        {
            await users.Insert(normalizedLogin, normalizedLogin, HashPassword(password));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw AppException.Conflict("login_taken", "login is already in use");
        }
    }

    public async Task<TokenResponse> Login(string? login, string? password)
    {
        var normalizedLogin = login?.Trim() ?? "";
        var candidatePassword = password ?? "";
        if (candidatePassword.Length > 1024)
            throw AppException.Unauthorized("invalid login or password");
        var user = await users.GetByTag(normalizedLogin);
        var valid = VerifyPassword(candidatePassword, user?.PasswordHash ?? DummyPasswordHash);
        if (user is null || !valid)
            throw AppException.Unauthorized("invalid login or password");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTimeOffset.UtcNow.AddDays(SessionDays);
        await sessions.Create(user.Id, HashToken(token), expiresAt.ToString("O"));
        return new TokenResponse(token, expiresAt);
    }

    public async Task Logout(string token) => await sessions.Delete(HashToken(token));

    public async Task<long> GetUserIdByToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw AppException.Unauthorized("missing bearer token");
        var userId = await sessions.GetUserId(HashToken(token));
        if (userId is null)
            throw AppException.Unauthorized("invalid or expired session");
        await users.TouchLastSeen(userId.Value);
        return userId.Value;
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2_sha256${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2_sha256" ||
                !int.TryParse(parts[1], out var iterations) || iterations is < 10_000 or > 2_000_000)
                return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
