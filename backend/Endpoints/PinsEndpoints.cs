using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;
using Messenger.Api.Services;

namespace Messenger.Api.Endpoints;

public static class PinsEndpoints
{
    public static void MapPinsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/chats/{chatId:long}/pins");

        group.MapGet("", async (
            long chatId, HttpContext context, IMessagesService service, CancellationToken cancellationToken) =>
        {
            var pins = await service.ListPinsAsync(chatId, CurrentUser.GetId(context), cancellationToken);
            var result = new List<PinResponse>(pins.Count);
            foreach (var pin in pins)
            {
                var message = await MessageResponseFactory.CreateAsync(
                    pin.Message, CurrentUser.GetId(context),
                    context.RequestServices.GetRequiredService<IReactionsService>(),
                    context.RequestServices.GetRequiredService<IAttachmentsRepository>(),
                    cancellationToken);
                result.Add(new PinResponse(pin.MessageId, pin.PinnedBy, pin.PinnedAt, message));
            }
            return Results.Ok(result);
        }).WithName("ListPins").WithSummary("Закреплённые сообщения чата");

        group.MapPost("", async (
            long chatId, PinMessageRequest request, HttpContext context,
            IMessagesService service, CancellationToken cancellationToken) =>
        {
            if (request.MessageId < 1)
                throw AppException.BadRequest("message_id must be positive");
            await service.PinAsync(chatId, CurrentUser.GetId(context), request.MessageId, cancellationToken);
            return Results.NoContent();
        }).WithName("PinMessage").WithSummary("Закрепить сообщение");

        group.MapDelete("/{messageId:long}", async (
            long chatId, long messageId, HttpContext context,
            IMessagesService service, CancellationToken cancellationToken) =>
        {
            await service.UnpinAsync(chatId, CurrentUser.GetId(context), messageId, cancellationToken);
            return Results.NoContent();
        }).WithName("UnpinMessage").WithSummary("Открепить сообщение");
    }
}
