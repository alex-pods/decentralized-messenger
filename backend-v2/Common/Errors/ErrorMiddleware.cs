namespace Messenger.Api.Common.Errors;

/// <summary>AppException → {code, error} с нужным статусом; остальное → 500.</summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (AppException e)
        {
            ctx.Response.StatusCode = e.Status;
            await ctx.Response.WriteAsJsonAsync(new
            {
                code = e.Code,
                error = e.Message,
                details = (object?)null,
                trace_id = ctx.TraceIdentifier
            });
        }
        catch (Exception e)
        {
            log.LogError(e, "unhandled error on {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new
            {
                code = "internal_error",
                error = "internal error",
                details = (object?)null,
                trace_id = ctx.TraceIdentifier
            });
        }
    }
}
