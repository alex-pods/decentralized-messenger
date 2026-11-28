using System.Security.Claims;
using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;
using Messenger.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Messenger.Api.Endpoints;

public static class ChatsEndpoints
{
    public static void MapChatsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/chats");

        g.MapGet("", ListChatsAsync);
        g.MapPost("", CreateDirectAsync);
        g.MapPost("/group", CreateGroupAsync);
        g.MapGet("/{chatId}", GetChatAsync);
        g.MapDelete("/{chatId}", HideChatAsync);
        g.MapGet("/{chatId}/members", GetMembersAsync);
        g.MapGet("/{chatId}/activity", async (HttpContext http, string chatId, long? before_id, IChatsRepository chats, CancellationToken ct) =>
            Results.Ok(await chats.GetGroupActivityAsync(chatId, http.User.GetUserId(), before_id, ct)));
        g.MapPost("/{chatId}/members", AddMemberAsync);
        g.MapDelete("/{chatId}/members/{userId}", RemoveMemberAsync);
        g.MapPatch("/{chatId}/members/{userId}", SetRoleAsync);
        g.MapPost("/{chatId}/leave", LeaveAsync);
        g.MapPost("/{chatId}/owner", TransferOwnerAsync);
        g.MapGet("/{chatId}/messages", GetHistoryAsync);
        g.MapPost("/{chatId}/messages", SendMessageAsync);
        g.MapPost("/{chatId}/read", MarkReadAsync);
    }

    private static async Task<IResult> ListChatsAsync(
        HttpContext http, IChatsService chats,
        IMessagesService messages, IReactionsService reactions, IAttachmentsRepository attachments,
        CancellationToken ct)
    {
        var viewerId = http.User.GetUserId();
        var summaries = await chats.GetChatsAsync(viewerId, ct);

        var result = new List<object>(summaries.Count);
        foreach (var s in summaries)
            result.Add(await ToChatResponseAsync(s, viewerId, messages, reactions, attachments, ct));

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateDirectAsync(
        HttpContext http, IChatsService chats, CreateDirectRequest request, CancellationToken ct)
    {
        var result = await chats.OpenDirectChatAsync(http.User.GetUserId(), request, ct);
        var response = new ChatIdResponse(result.Chat.Id);
        return result.Created
            ? Results.Created($"/chats/{result.Chat.Id}", response)
            : Results.Ok(response);
    }

    private static async Task<IResult> CreateGroupAsync(
        HttpContext http, IChatsService chats, CreateGroupRequest request, CancellationToken ct)
    {
        var summary = await chats.CreateGroupAsync(http.User.GetUserId(), request, ct);
        return Results.Created($"/chats/{summary.Id}", new ChatIdResponse(summary.Id));
    }

    private static async Task<IResult> GetChatAsync(
        HttpContext http, string chatId, IChatsService chats,
        IMessagesService messages, IReactionsService reactions, IAttachmentsRepository attachments,
        CancellationToken ct)
    {
        var viewerId = http.User.GetUserId();
        var summary = await chats.GetChatAsync(chatId, viewerId, ct);
        return Results.Ok(await ToChatResponseAsync(summary, viewerId, messages, reactions, attachments, ct));
    }

    private static async Task<IResult> HideChatAsync(
        HttpContext http, string chatId, IChatsService chats, CancellationToken ct)
    {
        await chats.HideChatAsync(chatId, http.User.GetUserId(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMembersAsync(
        HttpContext http, string chatId, IChatsService chats, CancellationToken ct)
    {
        var members = await chats.GetMembersAsync(chatId, http.User.GetUserId(), ct);
        return Results.Ok(members.Select(ToMemberResponse).ToList());
    }

    private static async Task<IResult> AddMemberAsync(
        HttpContext http, string chatId, IChatsService chats, AddMemberRequest request, CancellationToken ct)
    {
        await chats.AddMemberAsync(chatId, http.User.GetUserId(), request.UserId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(
        HttpContext http, string chatId, string userId, IChatsService chats, CancellationToken ct)
    {
        await chats.RemoveMemberAsync(chatId, http.User.GetUserId(), userId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetRoleAsync(
        HttpContext http, string chatId, string userId, IChatsService chats,
        SetRoleRequest request, CancellationToken ct)
    {
        var role = request.Role switch
        {
            "admin" => MemberRole.Admin,
            "member" => MemberRole.Member,
            _ => throw new ValidationException("role must be admin or member"),
        };
        await chats.SetRoleAsync(chatId, http.User.GetUserId(), userId, role, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> LeaveAsync(
        HttpContext http, string chatId, IChatsService chats, CancellationToken ct)
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
        HttpContext http, string chatId, IChatsService chats,
        TransferOwnerRequest request, CancellationToken ct)
    {
        await chats.TransferOwnerAsync(chatId, http.User.GetUserId(), request.UserId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetHistoryAsync(
        HttpContext http, string chatId,
        [FromQuery(Name = "limit")] int? limit,
        [FromQuery(Name = "before_id")] string? beforeId,
        [FromQuery(Name = "after_id")] string? afterId,
        [FromQuery(Name = "before_sequence")] long? beforeSequence,
        [FromQuery(Name = "after_sequence")] long? afterSequence,
        IMessagesService messages, IReactionsService reactions, IAttachmentsRepository attachments,
        CancellationToken ct)
    {
        var viewerId = http.User.GetUserId();
        var query = new HistoryQuery(limit ?? 50, beforeId, afterId, beforeSequence, afterSequence);
        var rows = await messages.GetHistoryAsync(chatId, viewerId, query, ct);

        var result = new List<MessageResponse>(rows.Count);
        foreach (var row in rows)
            result.Add(await ToMessageResponseAsync(row, viewerId, reactions, attachments, ct));

        return Results.Ok(result);
    }

    private static async Task<IResult> SendMessageAsync(
        HttpContext http,
        string chatId,
        IMessagesService messages,
        SendMessageRequest request,
        CancellationToken ct)
    {
        var result = await messages.SendAsync(
            chatId, http.User.GetUserId(), request, ct);

        return Results.Json(
            new MessageIdResponse(result.Message.Id),
            statusCode: result.Created
                ? StatusCodes.Status201Created
                : StatusCodes.Status200OK);
    }

    private static async Task<IResult> MarkReadAsync(
        HttpContext http, string chatId, IChatsService chats, MarkChatReadRequest request, CancellationToken ct)
    {
        await chats.MarkReadAsync(chatId, http.User.GetUserId(), request.MessageId, ct);
        return Results.NoContent();
    }

    private static async Task<object> ToChatResponseAsync(
        ChatSummary summary, string viewerId,
        IMessagesService messages, IReactionsService reactions, IAttachmentsRepository attachments,
        CancellationToken ct)
    {
        MessageResponse? lastMessage = null;
        if (summary.LastMessageId is string msgId)
        {
            var row = await messages.GetMessageAsync(msgId, viewerId, ct);
            if (row is not null)
                lastMessage = await ToMessageResponseAsync(row, viewerId, reactions, attachments, ct);
        }

        return ChatResponseFactory.Create(summary, lastMessage);
    }

    private static Task<MessageResponse> ToMessageResponseAsync(
    MessageRow msg,
    string viewerId,
    IReactionsService reactions,
    IAttachmentsRepository attachments,
    CancellationToken ct)
    => MessageResponseFactory.CreateAsync(
        msg, viewerId, reactions, attachments, ct);

    private static MemberResponse ToMemberResponse(MemberRow m)
        => new(m.UserId, m.Tag, m.DisplayName, RoleToString(m.Role), m.JoinedAt);

    private static string RoleToString(MemberRole role) => role switch
    {
        MemberRole.Member => "member",
        MemberRole.Admin => "admin",
        MemberRole.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static string GetUserId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return sub
            ?? throw new UnauthorizedAccessException("Missing user id claim");
    }
}
