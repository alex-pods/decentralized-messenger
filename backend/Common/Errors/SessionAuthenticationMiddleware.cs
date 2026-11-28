using System.Security.Claims;
using Messenger.Api.Endpoints;
using Microsoft.AspNetCore.Http.Features;
using Messenger.Api.Services;

namespace Messenger.Api.Common.Errors;

public sealed class SessionAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuthService auth)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<MessageRelayEndpoints.RelayBody>() != null)
        {
            const int maxRelayBytes = 32768;
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maxRelayBytes;
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.ContentLength > maxRelayBytes)
                throw AppException.PayloadTooLarge("Relay request is too large");
        }
        if (context.Request.Path.StartsWithSegments("/themes"))
        {
            var size = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 16384;
            if (context.Request.ContentLength > 16384) throw AppException.PayloadTooLarge("Theme is too large");
        }
        if (HttpMethods.IsGet(context.Request.Method) && context.GetEndpoint()?.Metadata.GetMetadata<Messenger.Api.Endpoints.PublicThemeRead>() != null)
        {
            await next(context); return;
        }
        if (context.GetEndpoint()?.Metadata.GetMetadata<CapabilitiesEndpoints.PublicRead>() != null)
        { await next(context); return; }
        if (context.GetEndpoint()?.Metadata.GetMetadata<ServerIdentityEndpoints.PublicProof>() != null)
        {
            const int maxProofRequestBytes = 512;
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = maxProofRequestBytes;
            if (context.Request.ContentLength > maxProofRequestBytes)
                throw AppException.PayloadTooLarge("Server identity request is too large");
            await next(context);
            return;
        }
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

        var authorization = context.Request.Headers.Authorization.ToString();
        string token;
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authorization["Bearer ".Length..].Trim();
        }
        else if (isWsEndpoint && authorization.Length == 0)
        {
            token = context.Request.Query["user_session_token"].ToString();
            if (string.IsNullOrWhiteSpace(token))
                token = context.Request.Query["token"].ToString();
        }
        else
        {
            throw AppException.Unauthorized("missing bearer token");
        }

        string userId;

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
            [new Claim(ClaimTypes.NameIdentifier, userId)], "Session"));
        await next(context);
    }
}

public static class CurrentUser
{
    internal const string ContextKey = "Messenger.Api.CurrentUserId";
    internal const string TokenContextKey = "Messenger.Api.SessionToken";

    public static string GetId(HttpContext context) =>
        context.Items.TryGetValue(ContextKey, out var value) && value is string id
            ? id
            : throw AppException.Unauthorized("missing bearer token");

    public static string GetToken(HttpContext context) =>
        context.Items.TryGetValue(TokenContextKey, out var value) && value is string token
            ? token
            : throw AppException.Unauthorized("missing bearer token");
}
