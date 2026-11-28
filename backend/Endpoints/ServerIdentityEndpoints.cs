using Messenger.Api.Infrastructure;
using Messenger.Identity;

namespace Messenger.Api.Endpoints;

internal static class ServerIdentityEndpoints
{
    internal sealed class PublicProof;
    internal sealed record Challenge(string? Nonce);

    internal static void MapServerIdentityEndpoints(this WebApplication app)
    {
        app.MapPost("/" + ServerIdentityProof.Path, (Challenge challenge, HttpContext context, ServerIdentity identity) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!ServerIdentityProof.TryReadNonce(challenge.Nonce, out var nonce))
                return Results.BadRequest(new { error = "Invalid server identity nonce" });

            return Results.Ok(new { public_key = identity.PublicKey, signature = identity.Sign(nonce) });
        })
        .WithMetadata(new PublicProof())
        .WithSummary("Подпись случайного числа постоянным ключом сервера");
    }
}
