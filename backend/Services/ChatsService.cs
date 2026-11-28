using Messenger.Api.Common.Errors;
using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Realtime;
using Messenger.Api.Infrastructure;
using Messenger.Identifiers;

namespace Messenger.Api.Services;

public interface IChatsService
{
    Task<IReadOnlyList<ChatSummary>> GetChatsAsync(string viewerId, CancellationToken ct = default);
    Task<ChatSummary> GetChatAsync(string chatId, string viewerId, CancellationToken ct = default);
    Task<OpenDirectChatResult> OpenDirectChatAsync(string callerId, CreateDirectRequest request, CancellationToken ct = default);
    Task<ChatSummary> CreateGroupAsync(string creatorId, CreateGroupRequest request, CancellationToken ct = default);
    Task HideChatAsync(string chatId, string viewerId, CancellationToken ct = default);
    Task<IReadOnlyList<MemberRow>> GetMembersAsync(string chatId, string viewerId, CancellationToken ct = default);
    Task<MemberRow> AddMemberAsync(string chatId, string callerId, string userId, CancellationToken ct = default);
    Task RemoveMemberAsync(string chatId, string callerId, string userId, CancellationToken ct = default);
    Task<MemberRow> SetRoleAsync(string chatId, string callerId, string userId, MemberRole role, CancellationToken ct = default);
    Task TransferOwnerAsync(string chatId, string callerId, string newOwnerId, CancellationToken ct = default);
    Task<LeaveResult> LeaveAsync(string chatId, string userId, CancellationToken ct = default);
    Task<string?> MarkReadAsync(string chatId, string viewerId, string messageId, CancellationToken ct = default);
}

public sealed class ChatsService(
    IChatsRepository chats,
    IUsersRepository users,
    IMessagesRepository messages,
    WsEventPublisher publisher,
    IFileStorage files,
    ILogger<ChatsService> logger) : IChatsService
{
    public Task<IReadOnlyList<ChatSummary>> GetChatsAsync(string viewerId, CancellationToken ct = default)
        => chats.GetVisibleChatsAsync(viewerId, ct);

    public async Task<ChatSummary> GetChatAsync(string chatId, string viewerId, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, viewerId, ct);
        var summary = await chats.GetSummaryAsync(chatId, viewerId, ct);
        return summary ?? throw new NotFoundException("Chat not found");
    }

    public async Task<OpenDirectChatResult> OpenDirectChatAsync(string callerId, CreateDirectRequest request, CancellationToken ct = default)
    {
        var otherUserId = request.AnotherUserId;
        if (!EntityIds.IsChat(request.ChatId) || !EntityIds.IsUser(otherUserId))
            throw new ValidationException("Invalid chat or user ID");
        if (callerId == otherUserId)
            throw new ValidationException("Cannot open direct chat with self");
        if (await users.GetById(otherUserId) is null)
            throw new NotFoundException("User not found");

        var result = await chats.GetOrCreateDirectAsync(request.ChatId, callerId, otherUserId, ct);
        if (!result.Created)
        {
            await chats.ShowAsync(result.ChatId, callerId, ct);
        }
        else
        {
            await publisher.PublishChatCreatedSafelyAsync(
                result.ChatId,
                [callerId, otherUserId]);
        }
        var summary = await chats.GetSummaryAsync(result.ChatId, callerId, ct)
            ?? throw new NotFoundException("Chat not found");
        return new OpenDirectChatResult(summary, result.Created);
    }

    public async Task<ChatSummary> CreateGroupAsync(string creatorId, CreateGroupRequest request, CancellationToken ct = default)
    {
        if (!EntityIds.IsChat(request.ChatId)) throw new ValidationException("Invalid chat ID");
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 64)
            throw new ValidationException("Title must be 1..64 characters");

        var memberIds = (request.MemberIds ?? Array.Empty<string>())
                    .Where(id => id != creatorId)
                    .Distinct()
                    .ToList();

        if (memberIds.Any(id => !EntityIds.IsUser(id))) throw new ValidationException("Invalid user ID");
        foreach (var id in memberIds)
            if (await users.GetById(id) is null)
                throw new NotFoundException($"User {id} not found");

        var chatId = await chats.CreateGroupAsync(request.ChatId, creatorId, title, memberIds, ct);
        await publisher.PublishChatCreatedSafelyAsync(
            chatId,
            memberIds.Append(creatorId));
        return await chats.GetSummaryAsync(chatId, creatorId, ct)
            ?? throw new NotFoundException("Chat not found");
    }

    public async Task HideChatAsync(string chatId, string viewerId, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, viewerId, ct);
        await chats.HideAsync(chatId, viewerId, ct);
        await publisher.PublishToUserSafelyAsync(viewerId, new WsEventResponse("chat_hidden", ChatId: chatId));
    }

    public async Task<IReadOnlyList<MemberRow>> GetMembersAsync(string chatId, string viewerId, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, viewerId, ct);
        return await chats.GetMembersAsync(chatId, ct);
    }

    public async Task<MemberRow> AddMemberAsync(
        string chatId,
        string callerId,
        string userId,
        CancellationToken ct = default)
    {
        await RequireGroupAdminAsync(chatId, callerId, ct);

        if (await users.GetById(userId) is null)
            throw new NotFoundException("User not found");

        var current = await chats.GetMembersAsync(chatId, ct);
        var existing = current.SingleOrDefault(m => m.UserId == userId);

        if (existing is not null)
            return existing;

        var added = await chats.AddMemberAsync(chatId, userId, ct);

        var refreshed = await chats.GetMembersAsync(chatId, ct);
        var member = refreshed.Single(m => m.UserId == userId);

        if (added)
        {
            await publisher.PublishToChatSafelyAsync(
                chatId,
                callerId,
                new WsEventResponse(
                    "member_added",
                    ChatId: chatId,
                    UserId: userId,
                    User: new UserCardResponse(
                        userId,
                        member.Tag,
                        member.DisplayName)));

            await publisher.PublishChatCreatedSafelyAsync(
                chatId,
                [userId]);
        }

        return member;
    }

    public async Task RemoveMemberAsync(string chatId, string callerId, string userId, CancellationToken ct = default)
    {
        var access = await RequireGroupAdminAsync(chatId, callerId, ct);

        if (userId == callerId)
            throw new ValidationException("Use LeaveAsync to leave the group");

        var members = await chats.GetMembersAsync(chatId, ct);
        var target = members.SingleOrDefault(m => m.UserId == userId)
            ?? throw new NotFoundException("Member not found");

        if (target.Role == MemberRole.Owner)
            throw new ForbiddenException("Cannot remove the group owner");

        if (target.Role == MemberRole.Admin && access.Role != MemberRole.Owner)
            throw new ForbiddenException("Only owner can remove an admin");

        await chats.RemoveMemberAsync(chatId, userId, ct);
        await publisher.PublishToChatSafelyAsync(chatId,callerId,
                new WsEventResponse(
                "member_removed",
                ChatId: chatId,
                UserId: userId),
                departedUserId: userId);
    }

    public async Task<MemberRow> SetRoleAsync(string chatId, string callerId, string userId, MemberRole role, CancellationToken ct = default)
    {
        var access = await RequireGroupAdminAsync(chatId, callerId, ct);

        if (access.Role != MemberRole.Owner)
            throw new ForbiddenException("Only owner can change roles");

        if (role == MemberRole.Owner)
            throw new ValidationException("Use TransferOwnerAsync to transfer ownership");

        var members = await chats.GetMembersAsync(chatId, ct);
        var target = members.SingleOrDefault(m => m.UserId == userId)
            ?? throw new NotFoundException("Member not found");

        if (target.Role == MemberRole.Owner)
            throw new ForbiddenException("Cannot change owner role");

        await chats.SetRoleAsync(chatId, userId, role, ct);
        await publisher.PublishToChatSafelyAsync(
            chatId,
            callerId,
            new WsEventResponse(
                "member_role_changed",
                ChatId: chatId,
                UserId: userId,
                Role: role == MemberRole.Admin ? "admin" : "member"),
                requireFollow: false);

        var refreshed = await chats.GetMembersAsync(chatId, ct);
        return refreshed.Single(m => m.UserId == userId);
    }

    public async Task TransferOwnerAsync(string chatId, string callerId, string newOwnerId, CancellationToken ct = default)
    {
        var access = await RequireGroupAdminAsync(chatId, callerId, ct);

        if (access.Role != MemberRole.Owner)
            throw new ForbiddenException("Only owner can transfer ownership");

        if (newOwnerId == callerId)
            throw new ValidationException("Already the owner");

        var members = await chats.GetMembersAsync(chatId, ct);
        if (members.All(m => m.UserId != newOwnerId))
            throw new NotFoundException("New owner is not a member");

        await chats.TransferOwnerAsync(chatId, callerId, newOwnerId, ct);
        await publisher.PublishToChatSafelyAsync(
            chatId,
            callerId,
            new WsEventResponse(
                "owner_changed",
                ChatId: chatId,
                UserId: newOwnerId),
            requireFollow: false);
    }

    public async Task<LeaveResult> LeaveAsync(
        string chatId,
        string userId,
        CancellationToken ct = default)
    {
        var access = await RequireAccessAsync(chatId, userId, ct);

        if (access.Type == ChatType.Direct)
            throw new ValidationException("Cannot leave a direct chat");

        var result = await chats.LeaveGroupAsync(chatId, userId, ct);

        if (result.Result == LeaveResult.GroupClosed)
        {
            await publisher.PublishToUserSafelyAsync(
                userId,
                new WsEventResponse(
                    "chat_deleted",
                    ChatId: chatId));

            foreach (var path in result.AttachmentPaths)
            {
                try
                {
                    await files.Delete(path, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to remove closed chat attachment {Path}",
                        path);
                }
            }
        }
        else if (result.Result == LeaveResult.Left)
        {
            await publisher.PublishToChatSafelyAsync(
                chatId,
                userId,
                new WsEventResponse(
                    "member_left",
                    ChatId: chatId,
                    UserId: userId),
                departedUserId: userId);
        }

        return result.Result;
    }

    public async Task<string?> MarkReadAsync(string chatId, string viewerId, string messageId, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, viewerId, ct);

        var message = await messages.GetByIdAsync(messageId, ct)
            ?? throw new NotFoundException("Message not found");

        if (message.ChatId != chatId)
            throw new ValidationException("Message does not belong to this chat");

        var result = await chats.MarkReadAsync(
            chatId, viewerId, messageId, ct);

        if (result.Changed)
        {
            await publisher.PublishToChatSafelyAsync(
                chatId,
                viewerId,
                new WsEventResponse(
                    "read",
                    ChatId: chatId,
                    UserId: viewerId,
                    LastReadMessageId: result.LastReadMessageId,
                    LastReadSequence: result.LastReadSequence));
        }

        return result.LastReadMessageId;
    }

    private async Task<ChatAccess> RequireAccessAsync(string chatId, string userId, CancellationToken ct)
    {
        var access = await chats.GetAccessAsync(chatId, userId, ct);
        if (access is not null) return access;
        throw new NotFoundException("Chat not found");
    }

    private async Task<ChatAccess> RequireGroupAdminAsync(string chatId, string userId, CancellationToken ct)
    {
        var access = await RequireAccessAsync(chatId, userId, ct);

        if (access.Type != ChatType.Group)
            throw new ValidationException("Operation requires a group chat");

        if (access.Role is not (MemberRole.Admin or MemberRole.Owner))
            throw new ForbiddenException("Requires admin or owner role");

        return access;
    }
}
