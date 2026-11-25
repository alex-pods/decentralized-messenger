using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Messenger.Api.Common.Errors;

namespace Messenger.Api.Antispam;

public sealed record ConcentrationClip(string Id, string VideoId, int TargetStartSeconds, int LeadInMs,
    int DurationMs, long Bytes, string Sha256, string? AverageColor = null);

/// <summary>An administrator-provided allowlist, never a URL proxy or a user-supplied file path.</summary>
public sealed class AntispamMediaCatalog
{
    readonly Dictionary<string,(ConcentrationClip Clip,string Path)> _clips = new(StringComparer.Ordinal);
    public IReadOnlyList<ConcentrationClip> Clips => _clips.Values.Select(c=>c.Clip).ToArray();
    public bool Available => _clips.Count>0;
    public AntispamMediaCatalog(IConfiguration config)
    {
        var directory=config["MESSENGER_ANTISPAM_MEDIA"];
        if(string.IsNullOrWhiteSpace(directory))
        {
            directory=Path.Combine(AppContext.BaseDirectory,"Media","Training");
            if(!File.Exists(Path.Combine(directory,"manifest.json")))return;
        }
        directory=Path.GetFullPath(directory);
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"manifest.json")));
        if(manifest.RootElement.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("Unsupported training media manifest");
        foreach(var item in manifest.RootElement.GetProperty("clips").EnumerateArray())
        {
            var clip=item.Deserialize<ConcentrationClip>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if(!Regex.IsMatch(clip.Id,"^[A-Za-z0-9_-]{1,64}$") || clip.LeadInMs!=4000 || clip.DurationMs!=36000 ||
               clip.Bytes is < 1 or > 16*1024*1024 || !Regex.IsMatch(clip.Sha256,"^[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid training clip metadata");
            var path=Path.Combine(directory,clip.Id+".mp4");var file=new FileInfo(path);
            if(!file.Exists || file.LinkTarget!=null || file.Length!=clip.Bytes)throw new InvalidDataException("Missing or invalid training clip: "+clip.Id);
            using var stream=file.OpenRead();
            if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(clip.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Training clip checksum mismatch: "+clip.Id);
            _clips.Add(clip.Id,(clip,path));
            if(_clips.Count>64)throw new InvalidDataException("Too many training clips");
        }
    }
    public string PathFor(string id)=>_clips.TryGetValue(id,out var entry)?entry.Path:throw AppException.NotFound("Ролик не найден");
}
