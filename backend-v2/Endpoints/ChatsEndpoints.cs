using System.Security.Claims;
using Messenger.Api.Common.Errors;
using Messenger.Api.Services.Contracts;
using Messenger.Api.Repositories.Models;
using Messenger.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Messenger.Api.Endpoints;

public static class ChatsEndpoints
{
    public static void MapChatsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/chats").RequireAuthorization();

        g.MapGet("", ListChatsAsync);
        g.MapPost("", CreateDirectAsync);
        g.MapPost("/group", CreateGroupAsync);
        g.MapGet("/{chatId:long}", GetChatAsync);
        g.MapDelete("/{chatId:long}", HideChatAsync);
        g.MapGet("/{chatId:long}/members", GetMembersAsync);
        g.MapPost("/{chatId:long}/members", AddMemberAsync);
        g.MapDelete("/{chatId:long}/members/{userId:long}", RemoveMemberAsync);
        g.MapPatch("/{chatId:long}/members/{userId:long}", SetRoleAsync);
        g.MapPost("/{chatId:long}/leave", LeaveAsync);
        g.MapPost("/{chatId:long}/owner", TransferOwnerAsync);
        g.MapGet("/{chatId:long}/messages", GetHistoryAsync);
        g.MapPost("/{chatId:long}/messages", SendMessageAsync);
        g.MapPost("/{chatId:long}/read", MarkReadAsync);
    }

    private static async Task<IResult> ListChatsAsync(
        HttpContext http, IChatsService chats,
        IMessagesService messages, IReactionsService reactions, CancellationToken ct)
    {
        var viewerId = http.User.GetUserId();
        var summaries = await chats.GetChatsAsync(viewerId, ct);

        var result = new List<object>(summaries.Count);
        foreach (var s in summaries)
            result.Add(await ToChatResponseAsync(s, viewerId, messages, reactions, ct));

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateDirectAsync(
        HttpContext http, IChatsService chats, CreateDirectBody body, CancellationToken ct)
    {
        var result = await chats.OpenDirectChatAsync(http.User.GetUserId(), body.AnotherUserId, ct);
        var response = new ChatIdResponse(result.Chat.Id);
        return result.Created
            ? Results.Created($"/chats/{result.Chat.Id}", response)
            : Results.Ok(response);
    }

    private static async Task<IResult> CreateGroupAsync(
        HttpContext http, IChatsService chats, CreateGroupBody body, CancellationToken ct)
    {
        var request = new CreateGroupRequest(body.Title, body.MemberIds ?? Array.Empty<long>());
        var summary = await chats.CreateGroupAsync(http.User.GetUserId(), request, ct);
        return Results.Created($"/chats/{summary.Id}", new ChatIdResponse(summary.Id));
    }

    private static async Task<IResult> GetChatAsync(
        HttpContext http, long chatId, IChatsService chats,
        IMessagesService messages, IReactionsService reactions, CancellationToken ct)
    {
        var viewerId = http.User.GetUserId();
        var summary = await chats.GetChatAsync(chatId, viewerId, ct);
        return Results.Ok(await ToChatResponseAsync(summary, viewerId, messages, reactions, ct));
    }

    private static async Task<IResult> HideChatAsync(
        HttpContext http, long chatId, IChatsService chats, CancellationToken ct)
    {
        await chats.HideChatAsync(chatId, http.User.GetUserId(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMembersAsync(
        HttpContext http, long chatId, IChatsService chats, CancellationToken ct)
    {
        var members = await chats.GetMembersAsync(chatId, http.User.GetUserId(), ct);
        return Results.Ok(members.Select(ToMemberResponse).ToList());
    }

    private static async Task<IResult> AddMemberAsync(
        HttpContext http, long chatId, IChatsService chats, AddMemberBody body, CancellationToken ct)
    {
        await chats.AddMemberAsync(chatId, http.User.GetUserId(), body.UserId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(
        HttpContext http, long chatId, long userId, IChatsService chats, CancellationToken ct)
    {
        await chats.RemoveMemberAsync(chatId, http.User.GetUserId(), userId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetRoleAsync(
        HttpContext http, long chatId, long userId, IChatsService chats,
        SetRoleBody body, CancellationToken ct)
    {
        var role = body.Role switch
        {
            "admin" => MemberRole.Admin,
            "member" => MemberRole.Member,
            _ => throw new ValidationException("role must be admin or member"),
        };
        await chats.SetRoleAsync(chatId, http.User.GetUserId(), userId, role, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> LeaveAsync(
        HttpContext http, long chatId, IChatsService chats, CancellationToken ct)
    {
        var result = await chats.LeaveAsync(chatId, http.User.GetUserId(), ct);

        return result == LeaveResult.OwnerTransferRequired
            ? Results.Conflict(new ApiErrorResponse(
                "owner_transfer_required",
                "Transfer ownership before leaving",
                null,
                Guid.NewGuid().ToString("N")))
            : Results.NoContent();
    }

    private static async Task<IResult> TransferOwnerAsync(
        HttpContext http, long chatId, IChatsService chats,
        TransferOwnerBody body, CancellationToken ct)
    {
        await chats.TransferOwnerAsync(chatId, http.User.GetUserId(), body.UserId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetHistoryAsync(
        HttpContext http, long chatId,
        [FromQuery(Name = "limit")] int? limit,
        [FromQuery(Name = "before_id")] long? beforeId,
        [FromQuery(Name = "after_id")] long? afterId,
        IMessagesService messages, IReactionsService reactions, CancellationToken ct)
    {
        var viewerId = http.User.GetUserId();
        var query = new HistoryQuery(limit ?? 50, beforeId, afterId);
        var rows = await messages.GetHistoryAsync(chatId, viewerId, query, ct);

        var result = new List<MessageResponse>(rows.Count);
        foreach (var row in rows)
            result.Add(await ToMessageResponseAsync(row, viewerId, reactions, ct));

        return Results.Ok(result);
    }

    private static async Task<IResult> SendMessageAsync(
        HttpContext http, long chatId, IMessagesService messages,
        SendMessageBody body, CancellationToken ct)
    {
        var msg = await messages.SendAsync(
            chatId, http.User.GetUserId(),
            new SendMessageRequest(body.Text, body.ReplyToId, body.ClientRef), ct);

        return Results.Json(new MessageIdResponse(msg.Id), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> MarkReadAsync(
        HttpContext http, long chatId, IChatsService chats, ReadBody body, CancellationToken ct)
    {
        await chats.MarkReadAsync(chatId, http.User.GetUserId(), body.MessageId, ct);
        return Results.NoContent();
    }

    private static async Task<object> ToChatResponseAsync(
        ChatSummary summary, long viewerId,
        IMessagesService messages, IReactionsService reactions, CancellationToken ct)
    {
        MessageResponse? lastMessage = null;
        if (summary.LastMessageId is long msgId)
        {
            var row = await messages.GetMessageAsync(msgId, viewerId, ct);
            if (row is not null)
                lastMessage = await ToMessageResponseAsync(row, viewerId, reactions, ct);
        }

        if (summary.Type == ChatType.Direct)
        {
            var other = summary.OtherUser
                ?? throw new InvalidOperationException("Direct chat without other user");
            return new DirectChatResponse(
                summary.Id, "direct",
                new UserCardResponse(other.Id, other.Tag, other.DisplayName),
                summary.CreatedAt, summary.CreatedBy, lastMessage);
        }

        return new GroupChatResponse(
            summary.Id, "group", summary.Title,
            summary.CreatedBy, summary.CreatedAt, lastMessage);
    }

    private static async Task<MessageResponse> ToMessageResponseAsync(
        MessageRow msg, long viewerId, IReactionsService reactions, CancellationToken ct)
    {
        IReadOnlyList<ReactionRow> rows =
            msg.ContentState == MessageContentState.Deleted
                ? Array.Empty<ReactionRow>()
                : await reactions.GetAsync(msg.Id, viewerId, ct);

        return new MessageResponse(
            msg.Id, msg.ChatId, msg.SenderId, msg.ReplyToId, msg.Text,
            StateToString(msg.ContentState),
            msg.SentAt, msg.DeliveredAt, msg.EditedAt, msg.ReadAt, msg.DeletedAt,
            Array.Empty<AttachmentResponse>(),
            rows.Select(r => new ReactionResponse(r.Emoji, r.UserIds)).ToList());
    }

    private static MemberResponse ToMemberResponse(MemberRow m)
        => new(m.UserId, m.Tag, m.DisplayName, RoleToString(m.Role), m.JoinedAt);

    private static string StateToString(MessageContentState state) => state switch
    {
        MessageContentState.Stored => "stored",
        MessageContentState.NotStored => "not_stored",
        MessageContentState.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string RoleToString(MemberRole role) => role switch
    {
        MemberRole.Member => "member",
        MemberRole.Admin => "admin",
        MemberRole.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static long GetUserId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return long.TryParse(sub, out var id)
            ? id
            : throw new UnauthorizedAccessException("Missing user id claim");
    }
}