using System.Text.Json;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;
using Messenger.Api.Services;

namespace Messenger.Api.Realtime;

public sealed class WsEventPublisher(
    WsConnectionRegistry registry,
    IChatsRepository chats,
    IChatMembershipRepository memberships,
    ISessionsRepository sessions,
    ILogger<WsEventPublisher> logger)
{
    public async Task<int> PublishToChatAsync(
        string chatId,
        string senderId,
        WsEventResponse evt,
        CancellationToken ct,
        bool requireFollow = true,
        string? departedUserId = null)
    {
        // Обойти проверку членства можно только для последнего
        // уведомления вышедшему или исключённому участнику.
        if (departedUserId is not null &&
            (evt.Type is not ("member_removed" or "member_left") ||
             evt.UserId != departedUserId ||
             evt.ChatId != chatId))
        {
            throw new ArgumentException(
                "Invalid departure event", nameof(evt));
        }

        var members = await chats.GetMembersAsync(chatId, ct);
        IEnumerable<string> recipients =
            members.Select(member => member.UserId);

        if (departedUserId is string departed)
            recipients = recipients.Append(departed);

        var payload = JsonSerializer.SerializeToUtf8Bytes(evt);
        var delivered = 0;

        foreach (var userId in recipients.Distinct())
        {
            var count = await SendAsync(
                userId,
                payload,
                ct,
                followChatId: requireFollow ? chatId : null,
                memberOfChatId: userId == departedUserId ? null : chatId);

            if (userId != senderId)
                delivered += count;
        }

        return delivered;
    }

    public async Task PublishToUserAsync(
        string userId,
        WsEventResponse evt,
        CancellationToken ct)
    {
        await SendAsync(
            userId,
            JsonSerializer.SerializeToUtf8Bytes(evt),
            ct);
    }

    internal async Task<int> RelayToChatAsync(string chatId, string senderId, MessageResponse message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new WsEventResponse("message_relayed", ChatId: chatId, Message: message));
        var delivered = 0;
        foreach (var member in await chats.GetMembersAsync(chatId, ct))
        {
            if (member.UserId != senderId)
                delivered += await SendAsync(member.UserId, payload, ct, followChatId: chatId,
                    memberOfChatId: chatId, requireRelaySupport: true);
        }
        return delivered;
    }

    public Task PublishToChatSafelyAsync(
        string chatId,
        string actorId,
        WsEventResponse evt,
        bool requireFollow = true,
        string? departedUserId = null)
        => SafelyAsync(
            evt.Type,
            ct => PublishToChatAsync(
                chatId, actorId, evt, ct,
                requireFollow, departedUserId));

    public Task PublishToUserSafelyAsync(
        string userId,
        WsEventResponse evt)
        => SafelyAsync(
            evt.Type,
            ct => PublishToUserAsync(userId, evt, ct));

    public async Task PublishChatCreatedSafelyAsync(
        string chatId,
        IEnumerable<string> userIds)
    {
        foreach (var userId in userIds.Distinct())
        {
            await SafelyAsync("chat_created", async ct =>
            {
                // В direct-чате у каждого получателя свой other_user.
                var summary =
                    await chats.GetSummaryAsync(chatId, userId, ct);

                if (summary is null)
                    return;

                var evt = new WsEventResponse(
                    "chat_created",
                    ChatId: chatId,
                    Chat: ChatResponseFactory.Create(summary));

                await SendAsync(
                    userId,
                    JsonSerializer.SerializeToUtf8Bytes(evt),
                    ct,
                    memberOfChatId: chatId);
            });
        }
    }
    public Task PublishAvatarChangedSafelyAsync(string userId)
    => SafelyAsync("avatar_changed", async ct =>
    {
        var recipients = await chats.GetPeerUserIdsAsync(userId, ct);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new WsEventResponse(
                "avatar_changed",
                UserId: userId));

        foreach (var recipient in recipients)
            await SendAsync(recipient, payload, ct);
    });
    private async Task<int> SendAsync(
        string userId,
        byte[] payload,
        CancellationToken ct,
        string? followChatId = null,
        string? memberOfChatId = null,
        bool requireRelaySupport = false)
    {
        var delivered = 0;

        foreach (var connection in registry.GetByUser(userId))
        {
            ct.ThrowIfCancellationRequested();
            if (requireRelaySupport && !connection.SupportsRelay) continue;

            if (followChatId is string followed &&
                !connection.IsFollowing(followed))
                continue;

            if (await sessions.GetUserId(connection.TokenHash) != userId)
            {
                connection.RequestClose(
                    4002, "session expired or revoked");
                continue;
            }

            if (memberOfChatId is string chatId &&
                !await memberships.IsMember(chatId, userId))
                continue;

            if (connection.TryEnqueue(payload))
                delivered++;
        }

        return delivered;
    }

    private async Task SafelyAsync(
        string eventType,
        Func<CancellationToken, Task> publish)
    {
        try
        {
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await publish(timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to publish {EventType}",
                eventType);
        }
    }
}
