using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MessengerDesktop;
using MessengerDesktop.Antispam;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static class ConcentrationChecks
{
    public static async Task CheckSoundtracks(string folder)
    {
        foreach(var file in Directory.EnumerateFiles(folder,"*.mp4").Where(p=>!p.Contains(".source.")))
        {
            var music=await TrainingMusic.Prepare(file,CancellationToken.None);
            var mixer=new CueMixer();mixer.Music(music,0);
            var buffer=new float[9600];float peak=0;double tail=0;
            for(var i=0;i<361;i++)
            {
                if(i is > 40 and < 200 && i%2==0)mixer.Play("hit");
                mixer.Read(buffer,0,buffer.Length);
                Check(buffer.All(float.IsFinite),"Invalid mixed audio");
                peak=Math.Max(peak,buffer.Max(Math.Abs));
                if(i==359)tail=buffer.Select(v=>(double)v*v).Average();
            }
            Check(peak<.5&&peak>.01,"Missing audio or insufficient mixing headroom: "+peak);
            Check(buffer.All(v=>v==0),"Sound did not end at 36s");
            Check(tail<.0001,"Abrupt soundtrack ending");
            Console.WriteLine($"PASS decoded + mixed {Path.GetFileName(file)}: peak {peak:0.000}, 36s, smooth fade, silent end");
        }
    }
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    public static async Task Run(string root)
    {
        using var remote = new HttpClient();
        using var bundledManifest = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Media", "Training", "manifest.json")));
        Check(bundledManifest.RootElement.GetProperty("clips").GetArrayLength() == 8, "Packaged manifest is incomplete");
        var missing=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            {"MESSENGER_ANTISPAM_MEDIA",Path.Combine(root,"missing-catalog")}}).Build();
        using var missingCatalog = CreateCatalog(missing);
        try{await missingCatalog.LoadAsync(remote, CancellationToken.None);throw new Exception("Invalid explicit media path silently fell back");}
        catch(DirectoryNotFoundException){}
        Console.WriteLine("PASS packaged catalog manifest and fail-closed local override");
        foreach(var (w,h) in new[]{(1920,1080),(1280,720),(2560,1440)})
        {
            var corners=TrainingStand.ProjectCorners(new AimCamera(),w,h);
            Check(Math.Abs(corners[0].Y-corners[1].Y)<.001f&&Math.Abs(corners[2].Y-corners[3].Y)<.001f,"Stand horizontal edges skewed");
            Check(Math.Abs(corners[0].X-corners[3].X)<.001f&&Math.Abs(corners[1].X-corners[2].X)<.001f,"Stand vertical edges skewed");
            Check(Math.Abs((corners[0].X+corners[2].X)/2-w/2f)<.001f&&Math.Abs((corners[0].Y+corners[2].Y)/2-h/2f)<.001f,"Stand is off-center");
        }
        Console.WriteLine("PASS coplanar stand: parallel edges and centered projection at 720p, 1080p and 1440p");
        var mixer=new CueMixer();var samples=new float[9600];
        Check(mixer.Read(samples,0,samples.Length)==samples.Length&&samples.All(s=>s==0),"Idle mixer clock stopped");
        mixer.Play("hit");mixer.Read(samples,0,960);
        var first=samples.Take(960).ToArray();
        mixer.Play("hit");mixer.Read(samples,0,960);
        Check(samples.Take(960).Where((v,i)=>Math.Abs(v-first[i])>1e-6).Any(),"New hit cut off previous voice");
        mixer.Read(samples,0,samples.Length);
        Check(samples.All(float.IsFinite)&&samples.Skip(7000).All(v=>v==0),"Audio tail did not return to silence");
        var clip=CueMixer.Synthesize((1050,.055,1,0));
        Check(clip[0]==0&&Math.Abs(clip[^1])<1e-6,"Audio click at envelope boundary");
        Console.WriteLine("PASS continuous audio clock, overlapping hit sounds and smooth silent boundaries");

        var media=Path.Combine(root,"media");Directory.CreateDirectory(media);
        var bytes=RandomNumberGenerator.GetBytes(512);var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var descriptor=new ConcentrationClip("test-015","test",15,4000,36000,bytes.Length,hash);
        var manifest=Path.Combine(media,"manifest.json");
        void Save(ConcentrationClip value)=>File.WriteAllText(manifest,JsonSerializer.Serialize(new{version=1,clips=new[]{value}},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Save(descriptor);File.WriteAllBytes(Path.Combine(media,"test-015.mp4"),bytes);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"MESSENGER_ANTISPAM_MEDIA",media}}).Build();
        using var catalog = CreateCatalog(config);
        await catalog.LoadAsync(remote, CancellationToken.None);
        Check(catalog.Available&&catalog.Clips.Count==1,"Valid media rejected");
        Save(descriptor with{Id="../escape"});
        try{await catalog.LoadAsync(remote, CancellationToken.None);throw new Exception("Traversal accepted");}catch(InvalidDataException){}
        Save(descriptor with{Sha256=new string('0',64)});
        try{await catalog.LoadAsync(remote, CancellationToken.None);throw new Exception("Bad hash accepted");}catch(InvalidDataException){}
        var handler=new MediaHandler(bytes);
        using var api=new MessengerClient("https://test.invalid/api/dev","test-token",handler);
        var path=await api.DownloadConcentrationClip(descriptor,null,CancellationToken.None);
        try
        {
            Check(File.ReadAllBytes(path).SequenceEqual(bytes)&&handler.Count==1,"Download changed media");
            await api.DownloadConcentrationClip(descriptor,null,CancellationToken.None);
            Check(handler.Count==1,"Verified cache fetched file again");
            File.WriteAllBytes(path,new byte[512]);handler.Corrupt=true;
            try{await api.DownloadConcentrationClip(descriptor,null,CancellationToken.None);throw new Exception("Corrupt download accepted");}catch(IOException){}
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!,hash+".*.part").Any(),"Partial file leaked");
        }
        finally {File.Delete(path);}
        Console.WriteLine("PASS media allowlist, traversal rejection, checksums, authenticated download, verified cache and failed-download cleanup");
    }
    static Messenger.Api.Antispam.AntispamMediaCatalog CreateCatalog(IConfiguration config) =>
        new(config, new(true), NullLogger<Messenger.Api.Antispam.AntispamMediaCatalog>.Instance);

    sealed class MediaHandler(byte[] data):SignedTestServer
    {
        public int Count;public bool Corrupt;
        protected override Task<HttpResponseMessage> SendVerified(HttpRequestMessage request,CancellationToken ct)
        {
            Count++;Check(request.Headers.Authorization?.Parameter=="test-token","Media request lost authorization");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Corrupt?new byte[data.Length]:data)});
        }
    }
}
