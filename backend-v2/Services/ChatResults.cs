using Messenger.Api.Repositories.Models;

namespace Messenger.Api.Services;

public sealed record OpenDirectChatResult(ChatSummary Chat, bool Created);
