using System.Text.Json;

namespace MessengerBackend.Core;

/// <summary>Ошибка бизнес-правила: HTTP-статус + машинный код. Маппится в единый ErrorResponse.</summary>
public class HttpException(int status, string message, string? code = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

public record ErrorResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string Code,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string Error,
    [property: System.Text.Json.Serialization.JsonPropertyName("details")] object? Details,
    [property: System.Text.Json.Serialization.JsonPropertyName("trace_id")] string TraceId);

/// <summary>Единая точка формирования ошибок: {"code","error","details","trace_id"}.</summary>
public static class Errors
{
    public static IResult Err(int status, string msg, string? code = null) =>
        Results.Json(new ErrorResponse(Code(status, code), msg, null, Guid.NewGuid().ToString("N")),
            statusCode: status);

    public static string Code(int status, string? code) => code ?? status switch
    {
        400 => "validation_error",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        409 => "conflict",
        413 => "file_too_large",
        415 => "unsupported_media_type",
        429 => "rate_limited",
        _ => "error"
    };
}

/// <summary>Middleware: HttpException и необработанные ошибки -> ErrorResponse;
/// пустые framework-ответы (binding 400, 404 маршрута, 415) дополняет телом того же формата.</summary>
public static class ErrorMapping
{
    public static void Install(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            try { await next(ctx); }
            catch (HttpException ex)
            {
                if (ctx.Response.HasStarted) return;
                ctx.Response.StatusCode = ex.Status;
                ctx.Response.ContentType = "application/json";
                ctx.Items["ErrorBodyWritten"] = true;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(
                    new ErrorResponse(Errors.Code(ex.Status, ex.Code), ex.Message, null, Guid.NewGuid().ToString("N"))));
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "unhandled error on {Path}", ctx.Request.Path);
                if (ctx.Response.HasStarted) return;
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json";
                ctx.Items["ErrorBodyWritten"] = true;
                await ctx.Response.WriteAsync(
                    "{\"code\":\"internal_error\",\"error\":\"internal error\",\"details\":null,\"trace_id\":\"" +
                    Guid.NewGuid().ToString("N") + "\"}");
            }

            if (ctx.Items["ErrorBodyWritten"] is null && !ctx.Response.HasStarted &&
                ctx.Response.StatusCode is 400 or 404 or 415 &&
                ctx.Response.ContentLength is null or 0 && ctx.Response.ContentType is null)
            {
                ctx.Items["ErrorBodyWritten"] = true;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ErrorResponse(
                    ctx.Response.StatusCode switch
                    {
                        415 => "unsupported_media_type",
                        404 => "not_found",
                        _ => "validation_error"
                    },
                    ctx.Response.StatusCode switch
                    {
                        415 => "Content-Type не поддерживается; ожидается application/json или multipart/form-data",
                        404 => "Маршрут не найден",
                        _ => "Некорректное тело запроса или обязательные поля отсутствуют"
                    },
                    null, Guid.NewGuid().ToString("N"))));
            }
        });
    }
}
