using Messenger.Api.Antispam;

namespace Messenger.Api.Endpoints;

internal static class CapabilitiesEndpoints
{
    internal sealed class PublicRead;

    internal static void MapCapabilitiesEndpoints(this WebApplication app)
    {
        app.MapGet("/capabilities", (AntispamService antispam, AntispamMediaCatalog media) => Results.Ok(new
        {
            message_relay = true,
            antispam = new { enabled = antispam.Enabled, protocol = 1, concentration = antispam.Enabled && media.Available }
        }))
        .WithMetadata(new PublicRead())
        .WithSummary("Возможности сервера");
    }
}
