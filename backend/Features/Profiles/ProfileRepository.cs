using System.Data;
using Dapper;
using MessengerBackend.Core;

namespace MessengerBackend.Endpoints;

internal static class ProfileRepository
{
    public static UserRow ById(IDbConnection c, long id) =>
        c.QueryFirstOrDefault<UserRow>("SELECT * FROM users WHERE id=@id", new { id })
        ?? throw new HttpException(404, "пользователь не найден");
}
