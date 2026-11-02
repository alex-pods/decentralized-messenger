using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories.Models;

namespace Messenger.Api.Services;

public static class ChatResponseFactory
{
    public static object Create(
        ChatSummary summary,
        MessageResponse? lastMessage = null)
    {
        if (summary.Type == ChatType.Direct)
        {
            var other = summary.OtherUser
                ?? throw new InvalidOperationException(
                    "Direct chat without other user");

            return new DirectChatResponse(
                summary.Id,
                "direct",
                new UserCardResponse(
                    other.Id,
                    other.Tag,
                    other.DisplayName),
                summary.CreatedAt,
                summary.CreatedBy,
                lastMessage);
        }

        return new GroupChatResponse(
            summary.Id,
            "group",
            summary.Title,
            summary.CreatedBy,
            summary.CreatedAt,
            lastMessage);
    }
}