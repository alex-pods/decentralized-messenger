using LocalBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalBackend.Services;

public sealed partial class MessageStore
{
    // Новая интеграция использует этот метод. GetOrCreateChatAsync оставлен
    // для старых вызовов личных чатов, не располагающих серверным временем.
    public async Task<Chat> UpsertChatAsync(ChatInfo incoming, CancellationToken ct = default)
    {
        if (incoming.Id <= 0 || incoming.CreatedAt == default)
            throw new ArgumentException("Нужны серверный ID чата и время его создания.");
        if (incoming.Type is not ("direct" or "group"))
            throw new ArgumentException("Тип чата: direct или group.");
        if (incoming.Type == "direct" && incoming.PeerUserId is not > 0)
            throw new ArgumentException("Для личного чата нужен собеседник.");
        if (incoming.Type == "group" &&
            (incoming.PeerUserId != null || string.IsNullOrWhiteSpace(incoming.Title) || incoming.Title.Length > 64))
            throw new ArgumentException("Для группы нужны название 1–64 символа и PeerUserId = null.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (incoming.PeerUserId is { } peer && !await db.Users.AnyAsync(u => u.Id == peer, ct))
            throw new InvalidOperationException("Сначала сохраните профиль собеседника через UpsertUserAsync.");

        var chat = await db.Chats.FindAsync([incoming.Id], ct);
        if (chat is null)
        {
            chat = new Chat { Id = incoming.Id };
            db.Chats.Add(chat);
        }
        else if (chat.Type != incoming.Type || chat.PeerUserId != incoming.PeerUserId)
        {
            throw new InvalidOperationException("Тип и собеседник существующего чата не могут измениться.");
        }

        chat.Type = incoming.Type;
        chat.Title = incoming.Type == "group" ? incoming.Title : null;
        chat.PeerUserId = incoming.PeerUserId;
        chat.CreatedAt = incoming.CreatedAt;
        // DraftText, IsPinned, IsMuted, IsArchived и счётчики не затрагиваем.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return chat;
    }

    public async Task<List<ChatMember>> GetMembersAsync(long chatId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ChatMembers.AsNoTracking()
            .Include(m => m.User)
            .Where(m => m.ChatId == chatId)
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId)
            .ToListAsync(ct);
    }

    // Только ПОЛНЫЙ ответ GET /chats/{id}/members, а не отдельное WS-событие.
    // Перед вызовом должны быть сохранены профили всех перечисленных пользователей.
    public async Task ReplaceMembersAsync(long chatId, IReadOnlyList<ChatMemberInfo> members,
        CancellationToken ct = default)
    {
        var snapshot = members.ToArray();
        foreach (var member in snapshot) ValidateMember(member);
        var ids = snapshot.Select(m => m.UserId).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Участник повторяется в списке.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var chat = await db.Chats.AsNoTracking().SingleOrDefaultAsync(c => c.Id == chatId, ct)
            ?? throw new InvalidOperationException("Сначала сохраните чат.");

        if (chat.Type == "direct")
        {
            if (snapshot.Length != 2 || snapshot.Any(m => m.Role != "member") ||
                !ids.Contains(chat.PeerUserId!.Value))
                throw new ArgumentException("У личного чата два участника member, включая собеседника.");
        }
        else if (snapshot.Length == 0 || snapshot.Count(m => m.Role == "owner") != 1)
        {
            throw new ArgumentException("В полном списке группы должен быть ровно один owner.");
        }

        if (await db.Users.CountAsync(u => ids.Contains(u.Id), ct) != ids.Length)
            throw new InvalidOperationException("Сначала сохраните профили всех участников через UpsertUserAsync.");

        // Delete выполняется сразу; общая транзакция не даёт потерять состав
        // при ошибке вставки и позволяет безопасно сменить owner.
        await db.ChatMembers.Where(m => m.ChatId == chatId).ExecuteDeleteAsync(ct);
        db.ChatMembers.AddRange(snapshot.Select(m => new ChatMember
        {
            ChatId = chatId, UserId = m.UserId, Role = m.Role, JoinedAt = m.JoinedAt
        }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // Применение подтверждённого сервером добавления/смены member или admin.
    // Для owner_changed заново загружаем полный состав и вызываем ReplaceMembersAsync.
    public async Task UpsertMemberAsync(long chatId, ChatMemberInfo member, CancellationToken ct = default)
    {
        ValidateMember(member);
        if (member.Role == "owner")
            throw new ArgumentException("Владельца обновляйте полным снимком через ReplaceMembersAsync.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Chats.AnyAsync(c => c.Id == chatId && c.Type == "group", ct))
            throw new InvalidOperationException("Отдельные изменения участников применяются к существующей группе.");
        if (!await db.Users.AnyAsync(u => u.Id == member.UserId, ct))
            throw new InvalidOperationException("Сначала сохраните профиль участника.");

        var row = await db.ChatMembers.FindAsync([chatId, member.UserId], ct);
        if (row?.Role == "owner")
            throw new InvalidOperationException("Для смены владельца нужен полный снимок участников.");
        if (row is null)
        {
            row = new ChatMember { ChatId = chatId, UserId = member.UserId };
            db.ChatMembers.Add(row);
        }
        row.Role = member.Role;
        row.JoinedAt = member.JoinedAt;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // Локальное применение member_removed/member_left. Запрос серверу выполняет Core.
    public async Task RemoveMemberAsync(long chatId, long userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Chats.AnyAsync(c => c.Id == chatId && c.Type == "group", ct))
            throw new InvalidOperationException("Удаление участника применяется к существующей группе.");
        var row = await db.ChatMembers.FindAsync([chatId, userId], ct);
        if (row is null) return;
        if (row.Role == "owner")
            throw new InvalidOperationException("Нужна синхронизация полного состава или закрытие группы.");
        db.ChatMembers.Remove(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static void ValidateMember(ChatMemberInfo member)
    {
        if (member.UserId <= 0 || member.JoinedAt == default)
            throw new ArgumentException("Нужны серверный ID участника и время вступления.");
        if (member.Role is not ("member" or "admin" or "owner"))
            throw new ArgumentException("Роль: member, admin или owner.");
    }
}
