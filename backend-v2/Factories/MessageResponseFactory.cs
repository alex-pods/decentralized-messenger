using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;

namespace Messenger.Api.Services;

public static class MessageResponseFactory
{
    public static async Task<MessageResponse> CreateAsync(
        MessageRow msg,
        long viewerId,
        IReactionsService reactions,
        IAttachmentsRepository attachments,
        CancellationToken ct)
    {
        IReadOnlyList<ReactionRow> rows =
            msg.ContentState == MessageContentState.Deleted
                ? Array.Empty<ReactionRow>()
                : await reactions.GetAsync(msg.Id, viewerId, ct);

        var files = msg.ContentState == MessageContentState.Deleted
            ? Array.Empty<AttachmentResponse>()
            : (await attachments.GetByMessageIds([msg.Id]))
                .Select(AttachmentResponse.From)
                .ToArray();

        var state = msg.ContentState switch
        {
            MessageContentState.Stored => "stored",
            MessageContentState.NotStored => "not_stored",
            MessageContentState.Deleted => "deleted",
            _ => throw new ArgumentOutOfRangeException(nameof(msg.ContentState))
        };

        return new MessageResponse(
            msg.Id, msg.ChatId, msg.SenderId, msg.ReplyToId, msg.Text,
            state,
            msg.SentAt, msg.DeliveredAt, msg.EditedAt, msg.ReadAt, msg.DeletedAt,
            files,
            rows.Select(r => new ReactionResponse(r.Emoji, r.UserIds)).ToList());
    }
}