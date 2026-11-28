using System.Security.Cryptography;
using System.Text;

namespace Messenger.Api.Infrastructure;

public static class MessageFingerprint
{
    public static string Compute(
        string chatId,
        string text,
        string? replyToId)
        => Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{chatId}|{replyToId ?? "0"}|{text}")))
            .ToLowerInvariant();

    public static bool MatchesMigrated(string value, string text, bool hasReply)
    {
        var parts = value["legacy:".Length..].Split('|');
        return parts.Length == 3 && (parts[1] != "0") == hasReply &&
            parts[2] == Compute(parts[0], text, parts[1]);
    }
}
