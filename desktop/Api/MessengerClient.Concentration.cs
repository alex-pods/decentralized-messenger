using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MessengerDesktop;

public sealed record ConcentrationClip(string Id,string VideoId,int TargetStartSeconds,int LeadInMs,int DurationMs,long Bytes,string Sha256,string? AverageColor=null);
public sealed record ConcentrationCatalog(int Version,ConcentrationClip[] Clips);

public sealed partial class MessengerClient
{
    public async Task<ConcentrationClip[]> GetConcentrationClips(CancellationToken ct)
    {
        var catalog=await Send<ConcentrationCatalog>(HttpMethod.Get,"antispam/media",null,ct);
        if(catalog?.Version!=1)return [];
        return (catalog.Clips??[]).Where(ValidClip).Take(64).ToArray();
    }
    static bool ValidClip(ConcentrationClip c)=>c!=null && c.Id!=null && c.Sha256!=null &&
        Regex.IsMatch(c.Id,"^[A-Za-z0-9_-]{1,64}$") && c.LeadInMs==4000 && c.DurationMs==36000 &&
        c.Bytes is > 0 and <= 16*1024*1024 && Regex.IsMatch(c.Sha256,"^[a-f0-9]{64}$");
    public async Task<string> DownloadConcentrationClip(ConcentrationClip clip,IProgress<double>? progress,CancellationToken ct)
    {
        if(!ValidClip(clip))throw new IOException("Некорректное описание ролика");
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"messenger-desktop","training-media");
        Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,clip.Sha256+".mp4");
        async Task<bool> Valid(string file)
        {
            if(!File.Exists(file)||new FileInfo(file).Length!=clip.Bytes)return false;
            await using var stream=File.OpenRead(file);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)).Equals(clip.Sha256,StringComparison.OrdinalIgnoreCase);
        }
        if(await Valid(path)){progress?.Report(1);return path;}
        var partial=path+"."+Guid.NewGuid().ToString("N")+".part";
        try
        {
            using var response=await _http.GetAsync("antispam/media/"+Uri.EscapeDataString(clip.Id),HttpCompletionOption.ResponseHeadersRead,ct);
            response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is { } length && length!=clip.Bytes)throw new IOException("Размер ролика не совпадает");
            await using(var source=await response.Content.ReadAsStreamAsync(ct))
            await using(var target=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
            {
                var buffer=new byte[81920];long total=0;int read;
                while((read=await source.ReadAsync(buffer,ct))>0)
                {
                    total+=read;if(total>clip.Bytes)throw new IOException("Ролик превышает заявленный размер");
                    await target.WriteAsync(buffer.AsMemory(0,read),ct);progress?.Report(total/(double)clip.Bytes);
                }
            }
            if(!await Valid(partial))throw new IOException("Ролик загрузился не полностью");
            File.Move(partial,path,true);
            // This directory contains only content-addressed media cache, bounded to 256 MiB.
            long used=clip.Bytes;
            foreach(var file in new DirectoryInfo(folder).EnumerateFiles("*.mp4").Where(f=>f.FullName!=path).OrderByDescending(f=>f.LastWriteTimeUtc))
            {
                used+=file.Length;
                if(used>256L*1024*1024)try{file.Delete();}catch(IOException){/* Another open player may still use it. */}
            }
            return path;
        }
        finally { if(File.Exists(partial))File.Delete(partial); }
    }
}
