using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MessengerDesktop.Antispam;

/// <summary>Decode the complete local soundtrack before starting; no decoder/file I/O on the audio callback.</summary>
internal static class TrainingMusic
{
    public static Task<float[]> Prepare(string path,CancellationToken ct)=>Task.Run(()=>
    {
        using var reader=new MediaFoundationReader(path);
        ISampleProvider source=reader.ToSampleProvider();
        if(source.WaveFormat.Channels==1)source=new MonoToStereoSampleProvider(source);
        if(source.WaveFormat.Channels!=2)throw new IOException("Unsupported soundtrack channels");
        if(source.WaveFormat.SampleRate!=CueMixer.SampleRate)source=new WdlResamplingSampleProvider(source,CueMixer.SampleRate);
        var samples=new float[36*CueMixer.SampleRate*2];var position=0;
        while(position<samples.Length)
        {
            ct.ThrowIfCancellationRequested();
            var read=source.Read(samples,position,Math.Min(8192,samples.Length-position));
            if(read==0)break;
            position+=read;
        }
        if(position<35*CueMixer.SampleRate*2)throw new IOException("Soundtrack is incomplete");
        // AAC reconstruction may exceed full-scale: leave headroom before mixing the game cues.
        var peak=samples.Max(x=>Math.Abs(x));
        if(!float.IsFinite(peak))throw new IOException("Invalid soundtrack samples");
        if(peak>1)for(var i=0;i<position;i++)samples[i]/=peak;
        return samples;
    },ct);
}
