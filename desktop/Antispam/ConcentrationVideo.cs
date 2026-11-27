using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace MessengerDesktop.Antispam;

/// <summary>Decode local video frames. Sound is decoded separately and mixed with game cues in one audio output.</summary>
internal sealed class ConcentrationVideo : IDisposable
{
    public const int Width=1280,Height=720;
    readonly object _frameGate=new();
    readonly LibVLC _vlc;
    readonly MediaPlayer _player;
    readonly Media _media;
    readonly IntPtr _pixels;
    readonly TaskCompletionSource _firstFrame=new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _version;
    bool _disposed;
    public float Opacity { get; private set; }=1;
    public long Time=>_player.Time;
    public bool Started { get; private set; }

    public ConcentrationVideo(string file)
    {
        Core.Initialize();
        try
        {
            _vlc=new LibVLC("--no-audio","--no-video-title-show","--no-osd","--no-sub-autodetect-file","--avcodec-hw=none","--file-caching=100");
            _media=new Media(_vlc,new Uri(Path.GetFullPath(file)));
            _player=new MediaPlayer(_vlc) { Media=_media };
            _pixels=Marshal.AllocHGlobal(Width*Height*4);
            _player.SetVideoFormat("RV32",Width,Height,Width*4);
            _player.SetVideoCallbacks(LockFrame,UnlockFrame,DisplayFrame);
            _player.EncounteredError+=(_,_)=>_firstFrame.TrySetException(new IOException("Не удалось декодировать ролик"));
        }
        catch { _player?.Dispose();_media?.Dispose();_vlc?.Dispose();if(_pixels!=IntPtr.Zero)Marshal.FreeHGlobal(_pixels);throw; }
    }
    IntPtr LockFrame(IntPtr opaque,IntPtr planes)
    {
        Monitor.Enter(_frameGate);Marshal.WriteIntPtr(planes,_pixels);return IntPtr.Zero;
    }
    void UnlockFrame(IntPtr opaque,IntPtr picture,IntPtr planes)=>Monitor.Exit(_frameGate);
    void DisplayFrame(IntPtr opaque,IntPtr picture)
    {
        Interlocked.Increment(ref _version);_firstFrame.TrySetResult();
    }
    public async Task Prepare(CancellationToken ct)
    {
        if(!_player.Play())throw new IOException("Не удалось открыть ролик");
        await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(12),ct);
        _player.SetPause(true);_player.Time=0;
    }
    public void Start(double elapsedMs)
    {
        if(Started||_disposed)return;
        Started=true;_player.Time=(long)Math.Max(0,elapsedMs);_player.SetPause(false);
    }
    public bool ReadFrame(ref int uploadedVersion,Action<IntPtr> upload)
    {
        lock(_frameGate)
        {
            if(_disposed||_version==0||uploadedVersion==_version)return false;
            upload(_pixels);uploadedVersion=_version;return true;
        }
    }
    public async Task FadeOut(CancellationToken ct)
    {
        var started=System.Diagnostics.Stopwatch.GetTimestamp();
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var progress=(float)(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds/2);
            if(progress>=1)break;
            Opacity=1-progress;
            await Task.Delay(20,ct);
        }
        Opacity=0;_player.SetPause(true);
    }
    public void Dispose()
    {
        if(_disposed)return;
        _player.Stop();_player.Dispose();_media.Dispose();_vlc.Dispose();
        lock(_frameGate){_disposed=true;Marshal.FreeHGlobal(_pixels);}
    }
}
