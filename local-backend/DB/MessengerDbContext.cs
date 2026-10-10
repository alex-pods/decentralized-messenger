using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Messenger.Core.Models;

namespace LocalBackend.Db;

public sealed class MessengerDbContext(
    DbContextOptions<MessengerDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMember> ChatMembers => Set<ChatMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<MessageReaction> Reactions => Set<MessageReaction>();
    public DbSet<OutboxOperation> Outbox => Set<OutboxOperation>();
    public DbSet<AppStateEntry> AppState => Set<AppStateEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Все даты храним в UTC и читаем с Kind = Utc (SQLite сам по себе Kind не хранит).
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ── Users ───────────────────────────────────────────
        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Tag).HasMaxLength(16).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Tag).IsUnique();
        });

        // ── Chats ───────────────────────────────────────────
        b.Entity<Chat>(e =>
        {
            e.ToTable("Chats", t =>
            {
                t.HasCheckConstraint("CK_Chats_Type", "\"Type\" IN ('direct', 'group')");
                t.HasCheckConstraint("CK_Chats_Peer",
                    "(\"Type\" = 'direct' AND \"PeerUserId\" IS NOT NULL) OR " +
                    "(\"Type\" = 'group' AND \"PeerUserId\" IS NULL)");
                t.HasCheckConstraint("CK_Chats_Title",
                    "\"Type\" = 'direct' OR (\"Title\" IS NOT NULL AND " +
                    "length(trim(\"Title\")) > 0 AND length(\"Title\") <= 64)");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasMaxLength(16).IsRequired();
            e.Property(x => x.Title).HasMaxLength(64);

            e.HasOne(x => x.Peer).WithMany()
                .HasForeignKey(x => x.PeerUserId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.PeerUserId).IsUnique();      // с одним собеседником — один чат
            e.HasIndex(x => x.LastMessageAt);              // сортировка списка чатов
        });

        // ── GroupChat ────────────────────────────────────────
        b.Entity<ChatMember>(e =>
        {
            e.ToTable("ChatMembers", t =>
                t.HasCheckConstraint(
                    "CK_ChatMembers_Role",
                    "\"Role\" IN ('member', 'admin', 'owner')"));

            e.HasKey(x => new { x.ChatId, x.UserId });

            e.Property(x => x.Role)
                .HasMaxLength(16)
                .IsRequired();

            e.HasOne(x => x.Chat)
                .WithMany(c => c.Members)
                .HasForeignKey(x => x.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.UserId);

            e.HasIndex(x => x.ChatId)
                .IsUnique()
                .HasFilter("\"Role\" = 'owner'");
        });

        // ── Messages ────────────────────────────────────────
        b.Entity<Message>(e =>
        {
            e.ToTable("Messages", t =>
                t.HasCheckConstraint("CK_Messages_ReplyNotSelf",
                    "\"ReplyToMessageId\" IS NULL OR \"ReplyToMessageId\" <> \"Id\""));

            e.Property(x => x.Text).IsRequired();

            e.HasOne(x => x.Chat).WithMany(c => c.Messages)
                .HasForeignKey(x => x.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Sender).WithMany()
                .HasForeignKey(x => x.SenderId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.ReplyTo).WithMany()
                .HasForeignKey(x => x.ReplyToMessageId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.ClientId).IsUnique();
            e.HasIndex(x => x.ServerId).IsUnique();        // NULL-ы в SQLite не конфликтуют
            e.HasIndex(x => new { x.ChatId, x.SentAt, x.Id });
        });

        // ── Attachments ─────────────────────────────────────
        b.Entity<Attachment>(e =>
        {
            e.ToTable("Attachments", t =>
                t.HasCheckConstraint("CK_Attachments_Size", "\"FileSizeBytes\" >= 0"));

            e.Property(x => x.FileName).IsRequired();
            e.Property(x => x.FileType).HasMaxLength(127).IsRequired();

            e.HasOne(x => x.Message).WithMany(m => m.Attachments)
                .HasForeignKey(x => x.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.MessageId);
            e.HasIndex(x => x.LocalPath).IsUnique();       // как path_to_file на сервере; NULL допустим
        });

        // ── Reactions ───────────────────────────────────────
        b.Entity<MessageReaction>(e =>
        {
            e.ToTable("MessageReactions");
            e.Property(x => x.Emoji).HasMaxLength(32).IsRequired();

            e.HasOne(x => x.Message).WithMany(m => m.Reactions)
                .HasForeignKey(x => x.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.MessageId, x.UserId }).IsUnique();
        });

        // ── Outbox ──────────────────────────────────────────
        b.Entity<OutboxOperation>(e =>
        {
            e.ToTable("Outbox");

            e.HasOne(x => x.Message).WithMany()
                .HasForeignKey(x => x.MessageId)
                .OnDelete(DeleteBehavior.Cascade);        // сообщение удалили — операции над ним не нужны

            e.HasIndex(x => new { x.NextAttemptAt, x.Id });
        });

        // ── AppState ────────────────────────────────────────
        b.Entity<AppStateEntry>(e =>
        {
            e.ToTable("AppState");
            e.HasKey(x => x.Key);
        });
    }

    internal sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => NormalizeToUtc(v),
        v => NormalizeToUtc(v));

    internal sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? null : NormalizeToUtc(v.Value),
        v => v == null ? null : NormalizeToUtc(v.Value));

    private static DateTime NormalizeToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
            return value;

        if (value.Kind == DateTimeKind.Local)
            return value.ToUniversalTime();

        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}