using Messenger.Api.Repositories.Models;

namespace Messenger.Api.Services.Contracts;

public sealed record CreateGroupRequest(string Title, IReadOnlyCollection<long> MemberIds);
public sealed record SendMessageRequest(string Text, long? ReplyToId, string? ClientRef);
public sealed record EditMessageRequest(string Text);

public sealed record OpenDirectChatResult(ChatSummary Chat, bool Created);
