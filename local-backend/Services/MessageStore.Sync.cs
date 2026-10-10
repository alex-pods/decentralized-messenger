using Messenger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalBackend.Services;

public sealed partial class MessageStore
{
    public async Task<User?> GetUserAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task SetVisibleChatsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Chats.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsServerVisible, c => ids.Contains(c.Id)), ct);
    }

    public async Task SetChatVisibleAsync(long id, bool visible, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Chats.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsServerVisible, visible), ct);
    }

    // Called only after a complete successful server snapshot; a failed page must not hide cached history.
    public async Task ReconcileHistoryAsync(long chatId, IReadOnlyList<long> serverIds, long upperId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Messages.Where(m => m.ChatId == chatId && m.ServerId != null && m.ServerId <= upperId && !serverIds.Contains(m.ServerId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsHidden, true), ct);
        var chat = await db.Chats.SingleAsync(c => c.Id == chatId, ct);
        chat.UnreadCount = await db.Messages.CountAsync(m => m.ChatId == chatId && !m.IsHidden && !m.IsOutgoing && m.ReadAt == null, ct);
        chat.LastMessageAt = await db.Messages.Where(m => m.ChatId == chatId && !m.IsHidden).MaxAsync(m => (DateTime?)m.SentAt, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task HideServerMessageAsync(long serverId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Messages.Where(m => m.ServerId == serverId).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsHidden, true), ct);
    }

    public async Task RetryFailedAsync(long chatId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Messages.Where(m => m.ChatId == chatId && m.SyncState == MessageSyncState.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.SyncState, MessageSyncState.Pending), ct);
        await db.Outbox.Where(o => o.ChatId == chatId && o.Type == OutboxOperationType.SendMessage)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow).SetProperty(o => o.Attempts, 0), ct);
        await tx.CommitAsync(ct);
    }
}
