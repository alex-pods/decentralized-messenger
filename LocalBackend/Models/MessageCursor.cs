namespace LocalBackend.Models;

/// <summary>Курсор для постраничной загрузки истории (keyset pagination).</summary>
public readonly record struct MessageCursor(DateTime SentAt, long Id);