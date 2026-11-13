using System.Security.Cryptography;
using System.Text;

namespace Messenger.Api.Infrastructure;

public static class MessageFingerprint
{
    public static string Compute(
        long chatId,
        string text,
        long? replyToId)
        => Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    FormattableString.Invariant(
                        $"{chatId}|{replyToId ?? 0}|{text}"))))
            .ToLowerInvariant();
}