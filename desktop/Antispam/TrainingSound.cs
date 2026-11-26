using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MessengerDesktop.Antispam;

/// <summary>One persistent audio device and overlapping voices; no per-click device restart.</summary>
internal sealed class TrainingSound : IDisposable
{
    readonly CueMixer _mixer = new();
    IWavePlayer? _output;
    bool _attempted, _disposed;
    internal bool Muted { get; set; }
    public string? Error { get; private set; }
    internal bool IsPlaying => _output?.PlaybackState==PlaybackState.Playing && Error==null;
    internal double MusicPositionMs => _mixer.MusicPositionMs;
    public void EnsureStarted()
    {
        if (_attempted || _disposed) return;
        _attempted = true;
        try
        {
            _output = new WasapiOut(AudioClientShareMode.Shared, true, 60);
            _output.PlaybackStopped+=(_,e)=>{if(!_disposed&&e.Exception!=null)Error="Аудиопоток прервался: "+e.Exception.GetType().Name;};
            _output.Init(_mixer.ToWaveProvider16()); _output.Volume=Muted?0:1; _output.Play();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NAudio.MmException)
        {
            _output?.Dispose(); _output = null;
            Error = "Не удалось открыть аудиоустройство.";
        }
    }
    public void Play(string cue)
    {
        if (_disposed) return;
        EnsureStarted();
        if (_output != null) _mixer.Play(cue);
    }
    public void Stop() => _mixer.Clear();
    public void Music(float[] samples,double elapsedMs) => _mixer.Music(samples,elapsedMs);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _output?.Stop(); _output?.Dispose(); _mixer.Clear();
    }
}

public sealed class CueMixer : ISampleProvider
{
    public const int SampleRate = 48000;
    const float MusicGain = .028f; // Tenfold attenuation of the previous .28 soundtrack gain.
    readonly object _gate = new();
    readonly List<Voice> _voices = [];
    float[]? _music;
    int _musicPosition;
    public double MusicPositionMs=>Volatile.Read(ref _musicPosition)*500.0/SampleRate;
    readonly Dictionary<string,float[]> _clips = new()
    {
        ["hit"] = Synthesize((1050,.055,1,0)),
        ["miss"] = Synthesize((180,.035,.25,0)),
        ["tick"] = Synthesize((660,.07,.5,0)),
        ["finish"] = Synthesize((660,.12,.7,0),(990,.2,.6,.13))
    };
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate,2);
    public void Play(string cue)
    {
        lock(_gate) if(_clips.TryGetValue(cue,out var clip) && _voices.Count<32) _voices.Add(new Voice(clip));
    }
    public void Music(float[] samples,double elapsedMs)
    {
        lock(_gate) { _music=samples;_musicPosition=Math.Clamp((int)(elapsedMs/1000*SampleRate)*2,0,samples.Length); }
    }
    public void Clear() { lock(_gate) { _voices.Clear(); _music=null; } }
    public int Read(float[] buffer,int offset,int count)
    {
        Array.Clear(buffer,offset,count);
        lock(_gate)
        {
            if(_music is {} music)
            {
                var available=Math.Min(count,music.Length-_musicPosition);
                for(var i=0;i<available;i++)
                {
                    var frame=(_musicPosition+i)/2;
                    // A single clock/output for music and cues. Fade is sample-accurate, never volume steps.
                    var fadeIn=Math.Min(1,frame/(SampleRate*.015));
                    var fadeOut=Math.Clamp((36*SampleRate-frame)/(2.0*SampleRate),0,1);
                    buffer[offset+i]=music[_musicPosition+i]*MusicGain*(float)(fadeIn*fadeOut);
                }
                _musicPosition+=available;
                if(_musicPosition==music.Length)_music=null;
            }
            for(var i=_voices.Count-1;i>=0;i--)
            {
                var voice=_voices[i];var available=Math.Min(count,voice.Samples.Length-voice.Position);
                for(var sample=0;sample<available;sample++)buffer[offset+sample]+=voice.Samples[voice.Position+sample];
                voice.Position+=available;
                if(voice.Position==voice.Samples.Length)_voices.RemoveAt(i);
            }
        }
        for(var i=offset;i<offset+count;i++)buffer[i]=Math.Clamp(buffer[i],-.98f,.98f);
        return count; // Keep the device clock alive even between clicks.
    }
    public static float[] Synthesize(params (double Frequency,double Duration,double Level,double Delay)[] tones)
    {
        var samples=new float[(int)Math.Ceiling(tones.Max(t=>t.Delay+t.Duration+.01)*SampleRate)*2];
        foreach(var (frequency,duration,level,delay) in tones)
        {
            var peak=.2*.2*level;var sweep=Math.Log(.68)/duration;var start=(int)Math.Round(delay*SampleRate);
            for(var i=0;i<(int)((duration+.01)*SampleRate);i++)
            {
                var time=i/(double)SampleRate;
                var envelope=time<.004?peak*time/.004:time<duration
                    ?peak*Math.Pow(.0001/peak,(time-.004)/(duration-.004)):.0001*Math.Max(0,1-(time-duration)/.01);
                var phase=time<=duration?frequency*(Math.Exp(sweep*time)-1)/sweep
                    :frequency*(Math.Exp(sweep*duration)-1)/sweep+frequency*.68*(time-duration);
                var value=(float)(Math.Sin(Math.Tau*phase)*envelope);
                samples[(start+i)*2]+=value;samples[(start+i)*2+1]+=value;
            }
        }
        return samples;
    }
    sealed class Voice(float[] samples) { public readonly float[] Samples=samples;public int Position; }
}
