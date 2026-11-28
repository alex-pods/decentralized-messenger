using System.Security.Cryptography;

namespace Messenger.Identifiers;

internal static class EntityIds
{
    private const int UserBytes = 16;
    private const int ChatBytes = 24;
    private const int MessageSuffixBytes = 16;
    private const int ChatLength = ChatBytes * 2;

    public static string NewUser() => RandomHex(UserBytes);
    public static string NewChat() => RandomHex(ChatBytes);
    public static string NewMessage(string chatId) => chatId + "." + RandomHex(MessageSuffixBytes);

    public static bool IsUser(string? id) => id != null && IsHex(id, UserBytes * 2);
    public static bool IsChat(string? id) => id != null && IsHex(id, ChatLength);

    public static bool IsMessage(string? id) =>
        id is { Length: ChatLength + 1 + MessageSuffixBytes * 2 } &&
        id[ChatLength] == '.' && IsHex(id.AsSpan(0, ChatLength), ChatLength) &&
        IsHex(id.AsSpan(ChatLength + 1), MessageSuffixBytes * 2);

    public static bool IsMessageInChat(string? id, string chatId) =>
        IsMessage(id) && id!.AsSpan(0, ChatLength).SequenceEqual(chatId);

    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static bool IsHex(ReadOnlySpan<char> value, int length)
    {
        if (value.Length != length) return false;
        foreach (var ch in value)
            if (ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
