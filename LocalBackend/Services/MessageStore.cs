using Microsoft.EntityFrameworkCore;
using LocalBackend.Interfaces;
using LocalBackend.Db;
using LocalBackend.Models;

namespace LocalBackend.Services;

/// <summary>
/// Операции локального хранения. Связанные записи сообщения и outbox сохраняются атомарно.
/// </summary>
public sealed partial class MessageStore(IDbContextFactory<MessengerDbContext> factory) : IMessageStore
{
    // ═════════════ Пользователи и чаты ═════════════

    public async Task UpsertUserAsync(User incoming, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FindAsync([incoming.Id], ct);

        if (user is null)
        {
            incoming.UpdatedAt = DateTime.UtcNow;
            db.Users.Add(incoming);
        }
        else
        {
            user.Tag = incoming.Tag;
            user.DisplayName = incoming.DisplayName;
            user.Bio = incoming.Bio;
            user.LastSeenAt = incoming.LastSeenAt;
            user.UpdatedAt = DateTime.UtcNow;

            if (user.AvatarRemotePath != incoming.AvatarRemotePath)
            {
                user.AvatarRemotePath = incoming.AvatarRemotePath;
                user.AvatarLocalPath = null;      // аватар сменился — перекачать
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<Chat> GetOrCreateChatAsync(long serverChatId, long peerUserId, CancellationToken ct = default)
    {
        if (serverChatId <= 0 || peerUserId <= 0)
            throw new ArgumentException("IDs чата и собеседника должны быть положительными.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var chat = await db.Chats.FindAsync([serverChatId], ct);
        if (chat is not null)
        {
            if (chat.Type != "direct" || chat.PeerUserId != peerUserId)
                throw new InvalidOperationException("Этот ID уже принадлежит другому чату.");
            return chat;
        }

        chat = new Chat { Id = serverChatId, PeerUserId = peerUserId, CreatedAt = DateTime.UtcNow };
        db.Chats.Add(chat);
        await db.SaveChangesAsync(ct);
        return chat;
    }

    public async Task<List<Chat>> GetChatListAsync(bool archived = false, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Chats.AsNoTracking()
            .Include(c => c.Peer)
            .Where(c => c.IsArchived == archived)
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.LastMessageAt)
            .ToListAsync(ct);
    }

    // ═════════════ Исходящие сообщения ═════════════

    /// <summary>Пользователь нажал «Отправить»: сохраняем локально + ставим в очередь отправки.</summary>
    public async Task<Message> EnqueueOutgoingAsync(
        long chatId, long currentUserId, string text,
        long? replyToMessageId = null,
        IReadOnlyList<AttachmentInfo>? attachments = null,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;

        long? replyServerId = replyToMessageId is null
            ? null
            : await db.Messages.Where(m => m.Id == replyToMessageId).Select(m => m.ServerId).FirstOrDefaultAsync(ct);

        var message = new Message
        {
            ClientId = Guid.NewGuid(),
            ChatId = chatId,
            SenderId = currentUserId,
            IsOutgoing = true,
            ReplyToMessageId = replyToMessageId,
            ReplyToServerId = replyServerId,
            Text = text,
            SentAt = now,
            SyncState = MessageSyncState.Pending,
            Attachments = (attachments ?? []).Select(a => new Attachment
            {
                FileName = a.FileName,
                FileType = a.FileType,
                FileSizeBytes = a.FileSizeBytes,
                LocalPath = a.LocalPath,
                TransferState = TransferState.Pending,   // сначала надо загрузить файл
            }).ToList(),
        };
        db.Messages.Add(message);

        db.Outbox.Add(new OutboxOperation
        {
            Type = OutboxOperationType.SendMessage,
            Message = message,
            ChatId = chatId,
            CreatedAt = now,
            NextAttemptAt = now,
        });

        var chat = await db.Chats.FindAsync([chatId], ct)
                   ?? throw new InvalidOperationException($"Chat {chatId} not found");
        chat.LastMessageAt = now;
        chat.DraftText = null;

        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>Сервер принял сообщение и вернул id и время.</summary>
    public async Task MarkSentAsync(Guid clientId, long serverId, DateTime serverSentAt, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var message = await db.Messages.FirstOrDefaultAsync(m => m.ClientId == clientId, ct);
        if (message is null) return;   // удалили локально, пока отправлялось

        message.ServerId = serverId;
        message.SentAt = serverSentAt;
        message.SyncState = MessageSyncState.Synced;

        await db.Outbox
            .Where(o => o.MessageId == message.Id && o.Type == OutboxOperationType.SendMessage)
            .ExecuteDeleteAsync(ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task MarkSendFailedAsync(long messageId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Messages.Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.SyncState, MessageSyncState.Failed), ct);
    }

    // ═════════════ Входящие сообщения ═════════════

    /// <summary>
    /// Сохраняет сообщение, пришедшее с сервера. Идемпотентно (повтор по ServerId игнорируется).
    /// В той же транзакции кладёт AckDelivered в outbox: подтверждать серверу получение
    /// нужно ТОЛЬКО после успешной записи сюда — иначе при сбое сообщение потеряется,
    /// ведь на сервере оно больше не хранится.
    /// Возвращает null, если такое сообщение уже было.
    /// </summary>
    public async Task<Message?> SaveIncomingAsync(IncomingMessage m, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        if (await db.Messages.AnyAsync(x => x.ServerId == m.ServerId, ct))
            return null;

        var now = DateTime.UtcNow;

        long? replyLocalId = null;
        if (m.ReplyToServerId is { } replyServerId)
        {
            replyLocalId = await db.Messages
                .Where(x => x.ChatId == m.ChatId && x.ServerId == replyServerId)
                .Select(x => (long?)x.Id)
                .FirstOrDefaultAsync(ct);
        }

        var message = new Message
        {
            ClientId = Guid.NewGuid(),
            ServerId = m.ServerId,
            ChatId = m.ChatId,
            SenderId = m.SenderId,
            IsOutgoing = false,
            ReplyToMessageId = replyLocalId,
            ReplyToServerId = m.ReplyToServerId,
            Text = m.Text,
            SentAt = m.SentAt,
            DeliveredAt = now,
            EditedAt = m.EditedAt,
            SyncState = MessageSyncState.Synced,
            Attachments = (m.Attachments ?? []).Select(a => new Attachment
            {
                FileName = a.FileName,
                FileType = a.FileType,
                FileSizeBytes = a.FileSizeBytes,
                RemoteId = a.RemoteId,
                TransferState = TransferState.Pending,   // ждёт скачивания
            }).ToList(),
        };
        db.Messages.Add(message);

        db.Outbox.Add(new OutboxOperation
        {
            Type = OutboxOperationType.AckDelivered,
            Message = message,
            ChatId = m.ChatId,
            CreatedAt = now,
            NextAttemptAt = now,
        });

        var chat = await db.Chats.FindAsync([m.ChatId], ct)
                   ?? throw new InvalidOperationException($"Chat {m.ChatId} not found");
        chat.UnreadCount++;
        if (chat.LastMessageAt is null || m.SentAt > chat.LastMessageAt) chat.LastMessageAt = m.SentAt;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка: то же сообщение успел сохранить параллельный поток (unique по ServerId).
            if (await db.Messages.AnyAsync(x => x.ServerId == m.ServerId, ct)) return null;
            throw;
        }

        return message;
    }

    // ═════════════ События от сервера над уже существующими сообщениями ═════════════

    public async Task ApplyDeliveredAsync(long serverMessageId, DateTime at, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Messages
            .Where(m => m.ServerId == serverMessageId && m.IsOutgoing && m.DeliveredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeliveredAt, (DateTime?)at), ct);
    }

    /// <summary>Собеседник прочитал все мои сообщения в чате до upToServerId включительно.</summary>
    public async Task ApplyReadAsync(long chatId, long upToServerId, DateTime at, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Messages
            .Where(m => m.ChatId == chatId && m.IsOutgoing && m.ServerId <= upToServerId && m.ReadAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.ReadAt, (DateTime?)at)
                .SetProperty(m => m.DeliveredAt, m => m.DeliveredAt ?? at), ct);
    }

    public async Task ApplyRemoteEditAsync(long serverMessageId, string newText, DateTime editedAt, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var message = await db.Messages.FirstOrDefaultAsync(m => m.ServerId == serverMessageId, ct);
        if (message is null || message.DeletedAt != null) return;

        message.Text = newText;
        message.EditedAt = editedAt;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Удаление «у всех»: остаётся надгробие, текст/вложения/реакции стираются.
    /// Возвращает пути файлов, которые вызывающий код должен удалить с диска.</summary>
    public async Task<List<string>> ApplyRemoteDeleteAsync(long serverMessageId, DateTime at, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var message = await db.Messages
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .FirstOrDefaultAsync(m => m.ServerId == serverMessageId, ct);
        if (message is null) return [];

        var files = CollectFiles(message.Attachments);

        message.DeletedAt = at;
        message.Text = "";
        db.Attachments.RemoveRange(message.Attachments);
        db.Reactions.RemoveRange(message.Reactions);

        await db.SaveChangesAsync(ct);
        return files;
    }

    // ═════════════ Локальные действия пользователя ═════════════

    /// <summary>Пользователь открыл чат: помечаем входящие прочитанными и ставим MarkRead в outbox.</summary>
    public async Task<int> MarkChatReadAsync(long chatId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;

        var lastServerId = await db.Messages
            .Where(m => m.ChatId == chatId && !m.IsOutgoing && m.ReadAt == null)
            .MaxAsync(m => m.ServerId, ct);

        var count = await db.Messages
            .Where(m => m.ChatId == chatId && !m.IsOutgoing && m.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, (DateTime?)now), ct);

        await db.Chats.Where(c => c.Id == chatId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UnreadCount, 0), ct);

        if (count > 0 && lastServerId is not null)
        {
            db.Outbox.Add(new OutboxOperation
            {
                Type = OutboxOperationType.MarkRead,
                ChatId = chatId,
                PayloadJson = $"{{\"upToServerId\":{lastServerId}}}",
                CreatedAt = now,
                NextAttemptAt = now,
            });
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return count;
    }

    /// <summary>«Удалить у меня»: строка стирается полностью (у собеседника сообщение останется).
    /// Возвращает пути файлов для удаления с диска.</summary>
    public async Task<List<string>> DeleteLocallyAsync(long messageId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var message = await db.Messages.Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return [];

        var files = CollectFiles(message.Attachments);
        db.Messages.Remove(message);   // вложения/реакции/outbox — каскадом, ответы на него — SET NULL
        await db.SaveChangesAsync(ct);
        return files;
    }

    /// <summary>Поставить/сменить реакцию (emoji = null — убрать). Одна реакция на пользователя.</summary>
    public async Task SetReactionAsync(long messageId, long userId, string? emoji,
        bool enqueueForServer, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var existing = await db.Reactions.FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId, ct);

        if (emoji is null)
        {
            if (existing is not null) db.Reactions.Remove(existing);
        }
        else if (existing is null)
        {
            db.Reactions.Add(new MessageReaction { MessageId = messageId, UserId = userId, Emoji = emoji, CreatedAt = now });
        }
        else
        {
            existing.Emoji = emoji;
        }

        if (enqueueForServer)
        {
            db.Outbox.Add(new OutboxOperation
            {
                Type = emoji is null ? OutboxOperationType.RemoveReaction : OutboxOperationType.SetReaction,
                MessageId = messageId,
                PayloadJson = emoji is null ? null : System.Text.Json.JsonSerializer.Serialize(new { emoji }),
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // ═════════════ Чтение истории и поиск ═════════════

    /// <summary>Страница истории, от новых к старым. Для первой страницы cursor = null.
    /// Следующий курсор — (SentAt, Id) последнего элемента страницы.</summary>
    public async Task<List<Message>> GetMessagesPageAsync(
        long chatId, MessageCursor? before = null, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var query = db.Messages.AsNoTracking().Where(m => m.ChatId == chatId);
        if (before is { } c)
            query = query.Where(m => m.SentAt < c.SentAt || (m.SentAt == c.SentAt && m.Id < c.Id));

        return await query
            .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
            .Take(limit)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReplyTo)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    /// <summary>Полнотекстовый поиск (FTS5), опционально внутри одного чата. Поддерживает поиск по префиксу.</summary>
    public async Task<List<Message>> SearchAsync(string text, long? chatId = null, int limit = 50, CancellationToken ct = default)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return [];

        // Каждое слово экранируем кавычками (чтобы пользовательский ввод не ломал синтаксис FTS) + префикс *.
        var match = string.Join(' ', tokens.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*"));

        await using var db = await factory.CreateDbContextAsync(ct);

        var ids = await db.Database.SqlQuery<long>($"""
            SELECT MessagesFts.rowid AS Value
            FROM MessagesFts
            JOIN Messages ON Messages.Id = MessagesFts.rowid
            WHERE MessagesFts MATCH {match}
              AND ({chatId} IS NULL OR Messages.ChatId = {chatId})
            ORDER BY MessagesFts.rank
            LIMIT {limit}
            """).ToListAsync(ct);

        var byId = await db.Messages.AsNoTracking()
            .Include(m => m.Attachments)
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        return ids.Select(id => byId[id]).ToList();
    }

    // ═════════════ Outbox (для фонового воркера синхронизации) ═════════════

    public async Task<List<OutboxOperation>> GetDueOutboxAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        return await db.Outbox.AsNoTracking()
            .Include(o => o.Message).ThenInclude(m => m!.Attachments)
            .Where(o => o.NextAttemptAt <= now)
            .OrderBy(o => o.Id)               // порядок операций важен
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task CompleteOutboxAsync(long operationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Outbox.Where(o => o.Id == operationId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Неудача: увеличиваем счётчик и откладываем повтор (экспоненциально, максимум 5 минут).</summary>
    public async Task RecordOutboxFailureAsync(long operationId, string error, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var op = await db.Outbox.FindAsync([operationId], ct);
        if (op is null) return;

        op.Attempts++;
        op.LastError = error.Length > 500 ? error[..500] : error;
        op.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, op.Attempts)));
        await db.SaveChangesAsync(ct);
    }

    // ═════════════ AppState ═════════════

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppState.Where(x => x.Key == key).Select(x => x.Value).FirstOrDefaultAsync(ct);
    }

    public async Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entry = await db.AppState.FindAsync([key], ct);
        if (entry is null) db.AppState.Add(new AppStateEntry { Key = key, Value = value });
        else entry.Value = value;
        await db.SaveChangesAsync(ct);
    }

    // ═════════════ helpers ═════════════

    private static List<string> CollectFiles(IEnumerable<Attachment> attachments) =>
        attachments
            .SelectMany(a => new[] { a.LocalPath, a.ThumbnailLocalPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
}
