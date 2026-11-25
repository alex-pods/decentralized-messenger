using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace MessengerDesktop.Antispam;

public sealed partial class AntispamWindow
{
    readonly Button _startButton = new() { Content="Начать", IsEnabled=false };
    readonly ToggleSwitch _concentration = new() { OnContent="Повышенная концентрация",OffContent="Повышенная концентрация" };
    Control _startScreen = null!;
    ConcentrationVideo? _video;
    float[]? _music;
    bool _preparing;
    internal bool ReadyToStart => _ready && !_preparing && !_armed;
    internal bool Preparing => _preparing;
    internal long VideoTime => _video?.Time ?? -1;
    internal float VideoOpacity => _video?.Opacity ?? 0;
    internal string? PreviewClipId { get; set; }
    internal Task PreviewStart(bool concentration=false) { _concentration.IsChecked=concentration; return StartTraining(); }

    Control BuildStartScreen()
    {
        RequestedThemeVariant=ThemeVariant.Dark;
        _startButton.Height=54; _startButton.FontSize=17; _startButton.FontWeight=FontWeight.SemiBold;
        _startButton.CornerRadius=new CornerRadius(8); _startButton.Background=Brush.Parse("#B6E4F5");
        _startButton.Foreground=Brush.Parse("#132530"); _startButton.HorizontalAlignment=HorizontalAlignment.Stretch;
        _startButton.HorizontalContentAlignment=HorizontalAlignment.Center;
        _startButton.Click+=async(_,_)=>await StartTraining();
        _concentration.FontSize=14; _concentration.Foreground=Brush.Parse("#DBE7ED");
        _concentration.Margin=new Thickness(0,8,0,0);
        _concentration.HorizontalAlignment=HorizontalAlignment.Center;
        var content=new StackPanel { Spacing=24, MaxWidth=440, Margin=new Thickness(32),
            HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text="Подтвердите, что вы человек",FontSize=30,FontWeight=FontWeight.SemiBold,
            TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,
            Foreground=Brush.Parse("#EDF4F7"),Margin=new Thickness(0,0,0,8) });
        content.Children.Add(_startButton);
        content.Children.Add(_concentration);
        _startScreen=new Grid { Background=Brush.Parse("#F5101C27"),Children={content} };
        return _startScreen;
    }

    async Task StartTraining()
    {
        if(!_ready||_preparing||_armed||_ended)return;
        _preparing=true; _startButton.IsEnabled=false; _concentration.IsEnabled=false;
        try
        {
            if(!OperatingSystem.IsWindows())throw new NotSupportedException();
            _sound.EnsureStarted();
            if(_concentration.IsChecked==true)
            {
                _startButton.Content="Загрузка…";
                using var download=new CancellationTokenSource(TimeSpan.FromSeconds(90));
                using var loading=CancellationTokenSource.CreateLinkedTokenSource(download.Token,_closed.Token,_lifetime.Token);
                var clips=await _api.GetConcentrationClips(loading.Token);
                if(clips.Length==0)throw new IOException("No concentration clips");
                var videos=clips.GroupBy(c=>c.VideoId).ToArray();
                var variants=videos[Random.Shared.Next(videos.Length)].ToArray();
                var clip=clips.FirstOrDefault(c=>c.Id==PreviewClipId)??variants[Random.Shared.Next(variants.Length)];
                _scene.Palette=TrainingPalette.FromAverage(clip.AverageColor);
                var progress=new Progress<double>(p=>
                {
                    if(_ended||!_preparing)return;
                    _startButton.Content=$"Загрузка · {p*100:0}%";
                });
                var file=await _api.DownloadConcentrationClip(clip,progress,loading.Token);
                _startButton.Content="Подготовка…";
                _music=await TrainingMusic.Prepare(file,loading.Token);
                var video=new ConcentrationVideo(file);
                try { await video.Prepare(loading.Token); }
                catch { video.Dispose(); throw; }
                if(_ended){video.Dispose();return;}
                _video=video;
            }
            if(_ended)return;
            if(!HasNativeFocus)throw new InvalidOperationException("Window inactive");
            _mouse=new RawMouse(TryGetPlatformHandle()!.Handle,OnRawMouse);
            _scene.Video=_video; _armed=true; Cursor=new Cursor(StandardCursorType.None);
            _startScreen.IsVisible=false; _mouseFrame.Start(); _lifetime.CancelAfter(TimeSpan.FromSeconds(60));
            _mouse.KeepCursorHidden();
            _ = RunRound();
        }
        catch(Exception) when(!_ended)
        {
            _scene.Video=null; _video?.Dispose(); _video=null; _music=null;
            _mouse?.Dispose(); _mouse=null; Cursor=Cursor.Default;
            _startButton.Content="Повторить";
        }
        catch(Exception) when(_ended) { }
        finally
        {
            _preparing=false;
            if(!_armed&&!_ended){_startButton.IsEnabled=true;_concentration.IsEnabled=true;}
        }
    }

    async Task CompleteRound(AimState state,bool passed)
    {
        _passed=passed;_ended=true;_armed=false;
        _mouseFrame.Stop();_mouse?.Dispose();_mouse=null;Cursor=Cursor.Default;
        _crosshair.IsVisible=false;_countdown.Text="";_score.Text="";_scoreHud.IsVisible=false;_scene.Targets([]);
        _lifetime.Cancel();
        if(_video is {} video)
            try { await video.FadeOut(_closed.Token); } catch(OperationCanceledException) { return; }
        if(!_closing)ShowResult(state,passed);
    }
}
