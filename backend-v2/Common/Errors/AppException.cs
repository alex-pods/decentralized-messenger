namespace Messenger.Api.Common.Errors;

/// <summary>Ошибка бизнес-правила. Бросается из сервисов, превращается в JSON в ErrorMiddleware.</summary>
public sealed class AppException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static AppException Unauthorized(string m) => new(401, "unauthorized", m);
    public static AppException NotFound(string m) => new(404, "not_found", m);
    public static AppException Forbidden(string m) => new(403, "forbidden", m);
    public static AppException BadRequest(string m) => new(400, "validation_error", m);
    public static AppException Conflict(string code, string m) => new(409, code, m);
    public static AppException PayloadTooLarge(string m) => new(413, "payload_too_large", m);
}
