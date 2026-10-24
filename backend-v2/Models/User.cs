namespace Messenger.Api.Models;

/// <summary>Строка таблицы users — ровно как в БД.</summary>
public sealed record User(
    long Id,
    string Tag,
    string Name,
    string PasswordHash
);
