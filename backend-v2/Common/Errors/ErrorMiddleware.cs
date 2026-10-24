namespace Messenger.Api.Common.Errors;

/// <summary>AppException → {code, error} с нужным статусом; остальное → 500.</summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (AppException e)
        {
            await WriteError(ctx, e.Status, e.Code, e.Message);
        }
        catch (NotFoundException e)
        {
            await WriteError(ctx, StatusCodes.Status404NotFound, "not_found", e.Message);
        }
        catch (ForbiddenException e)
        {
            await WriteError(ctx, StatusCodes.Status403Forbidden, "forbidden", e.Message);
        }
        catch (ConflictException e)
        {
            await WriteError(ctx, StatusCodes.Status409Conflict, "conflict", e.Message);
        }
        catch (ValidationException e)
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "validation_error", e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            await WriteError(ctx, StatusCodes.Status401Unauthorized, "unauthorized", e.Message);
        }
        catch (Exception e)
        {
            log.LogError(e, "unhandled error on {Path}", ctx.Request.Path);
            await WriteError(ctx, StatusCodes.Status500InternalServerError, "internal_error", "internal error");
        }
    }

    private static Task WriteError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new
        {
            code,
            error = message,
            details = (object?)null,
            trace_id = context.TraceIdentifier
        });
    }
}
