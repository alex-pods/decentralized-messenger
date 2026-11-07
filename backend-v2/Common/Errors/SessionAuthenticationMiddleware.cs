using System.Security.Claims;
using Messenger.Api.Services;

namespace Messenger.Api.Common.Errors;

public sealed class SessionAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuthService auth)
    {
        var requestPath = context.Request.Path.Value?.TrimEnd('/');

        if (HttpMethods.IsGet(context.Request.Method) &&
            (string.Equals(
                requestPath,
                "/ws/protocol",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                requestPath,
                "/health",
                StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }
        if (HttpMethods.IsPost(context.Request.Method) &&
            (string.Equals(requestPath, "/auth/register", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(requestPath, "/auth/login", StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var isWsEndpoint = string.Equals(
        requestPath,
        "/ws",
        StringComparison.OrdinalIgnoreCase);

        string token;

        if (isWsEndpoint)
        {
        token = context.Request.Query["user_session_token"].ToString();

        if (string.IsNullOrWhiteSpace(token))
            token = context.Request.Query["token"].ToString();
        }
        else
        {
            var authorization = context.Request.Headers.Authorization.ToString();

            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                throw AppException.Unauthorized("missing bearer token");

            token = authorization["Bearer ".Length..].Trim();
        }

        long userId;

        try
        {
            userId = await auth.GetUserIdByToken(token);
        }
        catch (AppException exception)
            when (isWsEndpoint && exception.Status == StatusCodes.Status401Unauthorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.Items[CurrentUser.ContextKey] = userId;
        context.Items[CurrentUser.TokenContextKey] = token;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Session"));
        await next(context);
    }
}

public static class CurrentUser
{
    internal const string ContextKey = "Messenger.Api.CurrentUserId";
    internal const string TokenContextKey = "Messenger.Api.SessionToken";

    public static long GetId(HttpContext context) =>
        context.Items.TryGetValue(ContextKey, out var value) && value is long id
            ? id
            : throw AppException.Unauthorized("missing bearer token");

    public static string GetToken(HttpContext context) =>
        context.Items.TryGetValue(TokenContextKey, out var value) && value is string token
            ? token
            : throw AppException.Unauthorized("missing bearer token");
}
