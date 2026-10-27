namespace Messenger.Api.Infrastructure;

public interface IFileStorage
{
    string GetAbsolutePath(string relativePath);
    bool Exists(string relativePath);
    Task Write(string relativePath, Stream content, CancellationToken cancellationToken);
    Task Delete(string relativePath, CancellationToken cancellationToken = default);
}

public sealed class FileStorage(IConfiguration configuration) : IFileStorage
{
    private readonly string root = Path.GetFullPath(
        Environment.GetEnvironmentVariable("MESSENGER_FILES")
        ?? configuration["Files"]
        ?? Path.Combine(Directory.GetCurrentDirectory(), "files"));

    public string GetAbsolutePath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) && path != root)
            throw new InvalidOperationException("File path escapes storage root");
        return path;
    }

    public bool Exists(string relativePath) => File.Exists(GetAbsolutePath(relativePath));

    public async Task Write(string relativePath, Stream content, CancellationToken cancellationToken)
    {
        var path = GetAbsolutePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        await content.CopyToAsync(destination, cancellationToken);
    }

    public Task Delete(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetAbsolutePath(relativePath);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
