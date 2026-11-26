namespace MessengerDesktop;

internal static class ChatSearchFilter
{
    public static ChatItem[] Match(IEnumerable<ChatItem> chats, string? input)
    {
        var query = input?.Trim() ?? "";
        if (query.Length == 0) return chats.ToArray();
        var tagOnly = query.StartsWith('@');
        var termQuery = Normalize(query.TrimStart('@').Trim());
        var terms = termQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];
        return chats.Select(chat =>
        {
            var tag = Normalize(chat.Tag ?? "");
            var title = tagOnly ? "" : Normalize(chat.Title);
            if (tagOnly && tag.Length == 0 || terms.Any(term => !title.Contains(term, StringComparison.Ordinal) &&
                                                                !tag.Contains(term, StringComparison.Ordinal)))
                return (Chat: chat, Rank: -1);
            var rank = tag == termQuery || title == termQuery ? 0
                : tag.StartsWith(termQuery, StringComparison.Ordinal) || title.StartsWith(termQuery, StringComparison.Ordinal) ? 1 : 2;
            return (Chat: chat, Rank: rank);
        }).Where(result => result.Rank >= 0).OrderBy(result => result.Rank).Select(result => result.Chat).ToArray();
    }

    static string Normalize(string value) => value.ToLowerInvariant().Replace('ё', 'е');

    public static string? DirectoryTag(string input)
    {
        var tag = input.Trim().TrimStart('@').Trim();
        return tag.Length is >= 3 and <= 16 &&
            tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? tag : null;
    }
}
