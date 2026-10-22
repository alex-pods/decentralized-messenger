namespace Messenger.Api.Models;

/// <summary>Строка таблицы messages.</summary>
public sealed record Message(long Id, long ChatId, long SenderId, string Text, string SentAt);
