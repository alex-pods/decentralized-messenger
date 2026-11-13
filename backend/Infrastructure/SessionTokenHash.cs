using System.Security.Cryptography;
using System.Text;

namespace Messenger.Api.Infrastructure;

public static class SessionTokenHash
{
    public static string Compute(string token) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)))
        .ToLowerInvariant();
}