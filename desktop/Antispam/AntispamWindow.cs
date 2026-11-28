using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Threading;

namespace MessengerDesktop.Antispam;

public sealed record AimState(string Phase, double CountdownMs, double RemainingMs, int Score, JsonElement Targets, int AckSeq, bool Hit, string? Reason, bool Shot, int Hits = 0, int Misses = 0, double Accuracy = 0);
public sealed record AimInput(string Type, int Seq = 0, double X = 0, double Y = 0);
public sealed record AntispamOutcome(bool Passed, string? Error);

public sealed partial class AntispamWindow : Window
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly MessengerClient _api;
    readonly AimCamera _camera = new();
    readonly TrainingScene _scene;
    readonly TrainingSound _sound = new();
    string _lastCountdown = "";
    readonly TextBlock _countdown = new() { FontSize = 72, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _score = new() { FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Border _scoreHud = new() { Background=Brush.Parse("#D9101A23"), CornerRadius=new CornerRadius(6), Padding=new Thickness(16,9),
        HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(0,24,0,0), IsVisible=false };
    readonly Grid _crosshair = new() { Width = 11, Height = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    AimState? _state;
    long _stateAt, _lastShot;
    bool _pendingShot;
    Vector3 _shotPoint;
    readonly CancellationTokenSource _lifetime = new();
    readonly CancellationTokenSource _closed = new();
    readonly Channel<AimInput> _outgoing = Channel.CreateBounded<AimInput>(64);
    readonly ClientWebSocket _socket = new();
    readonly DispatcherTimer _mouseFrame = new() { Interval = TimeSpan.FromMilliseconds(16) };
    RawMouse? _mouse;
    AntispamChallenge? _challenge;
    int _dx, _dy;
    bool _armed, _ready, _ended, _passed, _closing, _cleaned;
    public string? Failure { get; private set; }
    public string? Phase { get; private set; }
    internal bool ShowingResult { get; private set; }
    internal bool MutePreviewAudio { set => _sound.Muted=value; }
    internal string MouseDiagnostics => _mouse?.Diagnostics??"no capture";
    internal (double Yaw,double Pitch) CameraAngles => (_camera.Yaw,_camera.Pitch);
    internal bool AudioPlaying=>_sound.IsPlaying;
    internal double MusicPositionMs=>_sound.MusicPositionMs;
    internal string? AudioError=>_sound.Error;
    internal bool HasNativeFocus => IsActive && (!OperatingSystem.IsWindows() || RawMouse.IsForeground(TryGetPlatformHandle()?.Handle??IntPtr.Zero));
    internal string RenderStats => $"{_scene.Renderer}: {_scene.FramesPerSecond:0.0} FPS, {_scene.Frames} frames";
    internal void DismissResult() { if (ShowingResult) Close(new AntispamOutcome(_passed,Failure)); }
    public event Action<AimState>? StateReceived;
    internal void PreviewMouse(int dx, int dy, bool click) => OnRawMouse(dx, dy, click);

    public AntispamWindow(MessengerClient api)
    {
        _api = api;
        _scene = new TrainingScene(_camera);
        Title = "Проверка"; SystemDecorations = SystemDecorations.None; WindowState = WindowState.FullScreen;
        CanResize = false; Background = Brush.Parse("#18232d"); Cursor = Cursor.Default;
        _crosshair.Children.Add(new Border { Width = 13, Height = 3, Background = Brush.Parse("#B3000000"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Center });
        _crosshair.Children.Add(new Border { Width = 3, Height = 13, Background = Brush.Parse("#B3000000"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center });
        _crosshair.Children.Add(new Border { Width = 11, Height = 1, Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        _crosshair.Children.Add(new Border { Width = 1, Height = 11, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        _scoreHud.Child=_score;
        var root = new Grid { Children = { _scene, _countdown, _scoreHud, _crosshair } };
        root.Children.Add(BuildStartScreen()); Content = root;
        _scene.Ready += () =>
        {
            if (_ended || _ready) return;
            _ready = true; _startButton.IsEnabled = true;
        };
        _scene.Failed += () => Fail("Не удалось запустить 3D-сцену. Проверьте драйвер видеокарты.");
        Opened += (_, _) => { _ = StartupTimeout(); _ = ShowOptionalMode(); };
        Deactivated += (_, _) => { if (_armed && !_ended) Fail("Проверка прервана: окно потеряло фокус."); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (ShowingResult) DismissResult(); else Fail("Проверка прервана."); } };
        PropertyChanged += (_, e) => { if (_armed && e.Property == WindowStateProperty && WindowState != WindowState.FullScreen) Fail("Проверка прервана: выход из полного экрана."); };
        _mouseFrame.Tick += (_, _) =>
        {
            if (_ended) return;
            if(_armed&&!HasNativeFocus){Fail("Проверка прервана: окно потеряло фокус.");return;}
            _mouse?.KeepCursorHidden();
            ApplyMouse();
            if (_state is not { } state) return;
            var elapsed = Stopwatch.GetElapsedTime(_stateAt).TotalMilliseconds;
            _countdown.Text = state.Phase == "countdown" ? AimCamera.Countdown(state.CountdownMs - elapsed) : "";
            if(_countdown.Text != _lastCountdown)
            {
                _lastCountdown=_countdown.Text ?? "";
                if(_lastCountdown.Length>0)_sound.Play("tick");
            }
            _score.Text = state.Phase == "running" ? $"{state.Score} / 6000    ·    {Math.Ceiling(Math.Max(0,state.RemainingMs-elapsed)/1000):0}" : "";
            _scoreHud.IsVisible=state.Phase=="running";
            _crosshair.IsVisible = state.Phase == "running";
        };
        Closing += (_, _) => { _closing = true; if (!_ended) Fail("Проверка закрыта до завершения."); Cleanup(); };
        Closed += (_, _) => { _closed.Cancel(); Cleanup(); _sound.Dispose(); _socket.Dispose(); };
    }

    async Task StartupTimeout()
    {
        try { await Task.Delay(15000, _lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (!_ready && !_ended) Fail("Не удалось запустить 3D-проверку. Проверьте драйвер видеокарты.");
    }

    void ApplyMouse()
    {
        if (_dx == 0 && _dy == 0) return;
        _camera.Move(_dx, _dy); _dx = _dy = 0; _scene.RequestNextFrameRendering();
    }

    void OnRawMouse(int dx, int dy, bool click)
    {
        if (_ended || !_armed || !HasNativeFocus) return;
        _dx += dx; _dy += dy;
        ApplyMouse();
        if (!click) return;
        if (Phase != "running" || _pendingShot || Stopwatch.GetElapsedTime(_lastShot).TotalMilliseconds < 65) return;
        var point = _camera.AimPoint();
        _shotPoint = new((float)(point.X - 480)/40, 2+(float)(270-point.Y)/40, -12);
        _pendingShot = true; _lastShot = Stopwatch.GetTimestamp();
        _sound.Play(_scene.OnTarget ? "hit" : "miss");
        Queue(new("shot", X: point.X, Y: point.Y));
    }

    void Queue(AimInput input)
    {
        if (!_ended && !_outgoing.Writer.TryWrite(input)) Fail("Проверка прервана: соединение не успевает отвечать.");
    }

    async Task RunRound()
    {
        try
        {
            _challenge = await _api.CreateAntispamChallenge(_lifetime.Token);
            if (_ended) return;
            await _api.ConnectAuthenticatedSocket(_socket, _api.AntispamWebSocketUri(_challenge.Id), _lifetime.Token);
            var sending = SendLoop();
            var heartbeat = Heartbeats();
            var buffer = new byte[8192];
            while (!_ended)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token);
                if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) throw new IOException("Challenge disconnected");
                var state = JsonSerializer.Deserialize<AimState>(buffer.AsSpan(0, result.Count), Json) ?? throw new IOException("Invalid challenge state");
                if (_ended) break;
                Phase = state.Phase;
                _state = state; _stateAt = Stopwatch.GetTimestamp();
                if (state.Phase == "countdown" && _video is { Started:false } video)
                {
                    var elapsed=4000-state.CountdownMs;
                    video.Start(elapsed);
                    if(_music is {} music) { _sound.Music(music,elapsed);_music=null; }
                }
                _scene.Targets(state.Targets.EnumerateArray().Select(p => new Vector3(
                    (p.GetProperty("x").GetSingle()-480)/40, 2+(270-p.GetProperty("y").GetSingle())/40, -12)).ToArray());
                if (state.Shot) { _pendingShot = false; if (state.Hit) _scene.Hit(_shotPoint); }
                StateReceived?.Invoke(state);
                if (state.Phase == "qualified")
                {
                    if (HasNativeFocus && WindowState == WindowState.FullScreen) Queue(new("finish"));
                    else Fail("Проверка прервана.");
                }
                else if (state.Phase == "passed")
                {
                    if (!HasNativeFocus) { Fail("Проверка прервана."); break; }
                    await CompleteRound(state,true);
                }
                else if (state.Phase == "failed")
                {
                    if(state.Reason == "time_up") await CompleteRound(state,false);
                    else Fail("Проверка не пройдена. Попробуйте снова.");
                }
            }
            _lifetime.Cancel();
            try { await Task.WhenAll(sending, heartbeat); } catch (OperationCanceledException) { }
        }
        catch (ApiException ex) when (ex.Code == "antispam_not_required")
        {
            try
            {
                await _api.RefreshAntispam();
                if (!_api.AntispamBlocked && !_ended) { _passed = true; _ended = true; Close(new AntispamOutcome(true, null)); }
                else if (!_ended) Fail("Не удалось подтвердить статус проверки.");
            }
            catch { if (!_ended) Fail("Не удалось подтвердить статус проверки."); }
        }
        catch (Exception) { if (!_ended) Fail("Проверка прервана: нет связи с сервером или 3D-окном."); }
        finally
        {
            Cleanup();
            if (!_passed && _challenge != null)
                try { using var timeout = new CancellationTokenSource(3000); await _api.AbortAntispamChallenge(_challenge.Id, timeout.Token); } catch { }
        }
    }

    async Task SendLoop()
    {
        var sequence = 0;
        await foreach (var input in _outgoing.Reader.ReadAllAsync(_lifetime.Token))
            await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(input with { Seq = ++sequence }, Json), WebSocketMessageType.Text, true, _lifetime.Token);
    }

    async Task Heartbeats()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(_lifetime.Token)) Queue(new("heartbeat"));
    }

    void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        _scene.Video = null; _video?.Dispose(); _video = null; _music=null;
        _mouseFrame.Stop(); _scene.Stop(); _sound.Stop(); _mouse?.Dispose(); _mouse = null;
        _lifetime.Cancel(); _outgoing.Writer.TryComplete(); _socket.Abort();
    }

    internal void Fail(string message)
    {
        if (_ended) return;
        _ended = true; Failure = message; Cleanup();
        if (!_closing) Close(new AntispamOutcome(false, message));
    }
}
