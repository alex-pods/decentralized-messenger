namespace MessengerDesktop;

internal static class ChatSearchFilter
{
    public static ChatItem[] Match(IEnumerable<ChatItem> chats, string? input)
    {
        var query = input?.Trim() ?? "";
        if (query.Length == 0) return chats.ToArray();
        var tagOnly = query.StartsWith('@');
        var terms = Normalize(query.TrimStart('@')).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return chats.Where(chat =>
        {
            var tag = Normalize(chat.Tag ?? "");
            var title = tagOnly ? "" : Normalize(chat.Title);
            return (!tagOnly || tag.Length > 0) &&
                   terms.All(term => title.Contains(term, StringComparison.Ordinal) ||
                                     tag.Contains(term, StringComparison.Ordinal));
        }).ToArray();
    }

    static string Normalize(string value) => value.ToLowerInvariant().Replace('ё', 'е');
}
