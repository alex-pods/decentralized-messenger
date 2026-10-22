namespace Messenger.Api.Common.Errors;

/// <summary>Ошибка бизнес-правила. Бросается из сервисов, превращается в JSON в ErrorMiddleware.</summary>
public sealed class AppException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static AppException NotFound(string m) => new(404, "not_found", m);
    public static AppException Forbidden(string m) => new(403, "forbidden", m);
    public static AppException BadRequest(string m) => new(400, "validation_error", m);
}

/// <summary>Заглушка для ручек, которые ещё не сделаны.</summary>
public static class ImplementMe
{
    public static IResult Result(string what) =>
        Results.Json(new { code = "not_implemented", error = "implement me: " + what }, statusCode: 501);
}
