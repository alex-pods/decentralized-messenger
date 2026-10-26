using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IAttachmentsRepository
{
    Task<List<Attachment>> GetByMessageIds(IReadOnlyCollection<long> messageIds);
    Task<Attachment?> GetById(long id);
    Task InsertMany(IReadOnlyCollection<Attachment> attachments);
}

public sealed class AttachmentsRepository(DbConnectionFactory db) : IAttachmentsRepository
{
    public async Task<List<Attachment>> GetByMessageIds(IReadOnlyCollection<long> messageIds)
    {
        if (messageIds.Count == 0) return [];
        using var connection = db.Open();
        var rows = await connection.QueryAsync<Attachment>(
            """
            SELECT id, message_id AS MessageId, file_name AS FileName, content_type AS ContentType,
                   size_bytes AS SizeBytes, storage_path AS StoragePath
            FROM attachments WHERE message_id IN @messageIds ORDER BY id
            """, new { messageIds });
        return rows.Select(Decrypt).ToList();
    }

    public async Task<Attachment?> GetById(long id)
    {
        using var connection = db.Open();
        var attachment = await connection.QuerySingleOrDefaultAsync<Attachment>(
            """
            SELECT id, message_id AS MessageId, file_name AS FileName, content_type AS ContentType,
                   size_bytes AS SizeBytes, storage_path AS StoragePath
            FROM attachments WHERE id = @id
            """, new { id });
        return attachment is null ? null : Decrypt(attachment);
    }

    public async Task InsertMany(IReadOnlyCollection<Attachment> attachments)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var attachment in attachments)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO attachments(message_id, file_name, content_type, size_bytes, storage_path)
                VALUES(@MessageId, @FileName, @ContentType, @SizeBytes, @StoragePath)
                """,
                new
                {
                    attachment.MessageId,
                    FileName = Crypto.Encrypt(attachment.FileName),
                    ContentType = Crypto.Encrypt(attachment.ContentType),
                    attachment.SizeBytes,
                    StoragePath = Crypto.Encrypt(attachment.StoragePath)
                }, transaction);
        }
        transaction.Commit();
    }

    private static Attachment Decrypt(Attachment attachment) => attachment with
    {
        FileName = Crypto.Decrypt(attachment.FileName)!,
        ContentType = Crypto.Decrypt(attachment.ContentType),
        StoragePath = Crypto.Decrypt(attachment.StoragePath)!
    };
}
