using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Messenger.Api.Common.Errors;

namespace Messenger.Api.Antispam;

public sealed record ConcentrationClip(string Id, string VideoId, int TargetStartSeconds, int LeadInMs,
    int DurationMs, long Bytes, string Sha256, string? AverageColor = null);

public sealed class AntispamMediaCatalog(IConfiguration config, AntispamOptions options,
    ILogger<AntispamMediaCatalog> logger) : BackgroundService
{
    const string DefaultSource = "https://sftp-ftp-server.duckdns.org/messenger-assets/v1/";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    volatile IReadOnlyDictionary<string, (ConcentrationClip Clip, string Path)> _clips =
        new Dictionary<string, (ConcentrationClip, string)>(StringComparer.Ordinal);

    public IReadOnlyList<ConcentrationClip> Clips => _clips.Values.Select(entry => entry.Clip).ToArray();
    public bool Available => _clips.Count > 0;

    public string PathFor(string id) => _clips.TryGetValue(id, out var entry)
        ? entry.Path : throw AppException.NotFound("Ролик не найден");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        await Task.Yield();
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await LoadAsync(client, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or HttpRequestException or OperationCanceledException or ArgumentException)
            {
                logger.LogWarning(ex, "Не удалось подготовить медиа для дополнительного режима");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    public async Task LoadAsync(HttpClient client, CancellationToken ct)
    {
        var local = config["MESSENGER_ANTISPAM_MEDIA"];
        var useLocal = !string.IsNullOrWhiteSpace(local);
        var manifestDirectory = useLocal ? Path.GetFullPath(local!) : Path.Combine(AppContext.BaseDirectory, "Media", "Training");
        var manifest = JsonSerializer.Deserialize<MediaManifest>(
            await File.ReadAllBytesAsync(Path.Combine(manifestDirectory, "manifest.json"), ct), Json);
        if (manifest is not { Version: 1, Clips: { Length: > 0 and <= 64 } })
            throw new InvalidDataException("Invalid media manifest");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in manifest.Clips)
        {
            if (clip?.Id == null || clip.Sha256 == null || !Regex.IsMatch(clip.Id, "^[A-Za-z0-9_-]{1,64}$")
                || !ids.Add(clip.Id) || clip.LeadInMs != 4000 || clip.DurationMs != 36000
                || clip.Bytes is < 1 or > 16 * 1024 * 1024 || !Regex.IsMatch(clip.Sha256, "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid media descriptor");
        }

        var source = useLocal ? null : ReadSource(config["MESSENGER_ASSET_SOURCE"]);
        var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(config["Db"] ?? "messenger.db"))!;
        var cache = Path.Combine(dbDirectory, "assets", "training");
        if (!useLocal) Directory.CreateDirectory(cache);
        var verified = new Dictionary<string, (ConcentrationClip, string)>(StringComparer.Ordinal);
        foreach (var clip in manifest.Clips)
        {
            var path = useLocal ? Path.Combine(manifestDirectory, clip.Id + ".mp4")
                : Path.Combine(cache, clip.Sha256 + ".mp4");
            if (!await ValidFile(path, clip, ct))
            {
                if (source == null) throw new InvalidDataException("Invalid local asset: " + clip.Id);
                await Download(client, new Uri(source, clip.Id + ".mp4"), path, clip, ct);
            }
            verified.Add(clip.Id, (clip, path));
        }
        _clips = verified;
    }

    static Uri ReadSource(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? DefaultSource : value.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Asset source must be an HTTPS directory URL");
        return uri;
    }

    static async Task<bool> ValidFile(string path, ConcentrationClip clip, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget != null || file.Length != clip.Bytes) return false;
        await using var content = file.OpenRead();
        return Convert.ToHexString(await SHA256.HashDataAsync(content, ct))
            .Equals(clip.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    static async Task Download(HttpClient client, Uri source, string path, ConcentrationClip clip, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != clip.Bytes)
            throw new InvalidDataException("Unexpected asset length: " + clip.Id);

        var partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                for (int read; (read = await input.ReadAsync(buffer, token)) != 0;)
                {
                    total += read;
                    if (total > clip.Bytes) throw new InvalidDataException("Asset exceeds expected length: " + clip.Id);
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            if (total != clip.Bytes || !Convert.ToHexString(hash.GetHashAndReset())
                .Equals(clip.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Asset download failed verification: " + clip.Id);
            token.ThrowIfCancellationRequested();
            File.Move(partial, path, overwrite: true);
        }
        finally { File.Delete(partial); }
    }

    sealed record MediaManifest(int Version, ConcentrationClip[] Clips);
}
