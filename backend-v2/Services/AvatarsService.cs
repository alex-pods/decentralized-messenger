using Messenger.Api.Common.Errors;
using Messenger.Api.Models;
using Messenger.Api.Repositories;

namespace Messenger.Api.Services;

public interface IAvatarsService
{
    Task<string> Upload(long userId, IFormFile file, CancellationToken cancellationToken);
    Task<AvatarDownload> GetDownload(long userId);
}

public sealed record AvatarDownload(string Path, string ContentType);

public sealed class AvatarsService(
    IAvatarsRepository avatars,
    IFileStorage files,
    ILogger<AvatarsService> logger) : IAvatarsService
{
    private const long MaxAvatarBytes = 5 * 1024 * 1024;

    public async Task<string> Upload(long userId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0) throw AppException.BadRequest("file must not be empty");
        if (file.Length > MaxAvatarBytes) throw AppException.PayloadTooLarge("avatar exceeds 5 MB");
        var oldAvatarPath = (await avatars.GetByUserIds([userId])).GetValueOrDefault(userId)?.Path;

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var (contentType, extension) = DetectImage(bytes)
            ?? throw AppException.BadRequest("invalid_avatar: only PNG, JPEG and WebP images are supported");

        var relativePath = $"avatars/u{userId}_{Guid.NewGuid():N}{extension}";
        await using var content = new MemoryStream(bytes, writable: false);
        await files.Write(relativePath, content, cancellationToken);

        try
        {
            await avatars.Upsert(new Avatar(userId, relativePath, contentType));
        }
        catch
        {
            await DeleteAfterFailure(relativePath);
            throw;
        }

        if (oldAvatarPath is not null && oldAvatarPath != relativePath)
            await DeleteAfterFailure(oldAvatarPath);
        return relativePath;
    }

    public async Task<AvatarDownload> GetDownload(long userId)
    {
        var avatar = (await avatars.GetByUserIds([userId])).GetValueOrDefault(userId)
            ?? throw AppException.NotFound("avatar not found");
        if (!files.Exists(avatar.Path))
            throw AppException.NotFound("avatar file not found");
        return new AvatarDownload(files.GetAbsolutePath(avatar.Path), avatar.ContentType);
    }

    private async Task DeleteAfterFailure(string path)
    {
        try
        {
            await files.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to clean up avatar file {AvatarPath}", path);
        }
    }

    private static (string ContentType, string Extension)? DetectImage(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ("image/png", ".png");
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ("image/jpeg", ".jpg");
        if (bytes.Length >= 12 &&
            bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return ("image/webp", ".webp");
        return null;
    }
}
