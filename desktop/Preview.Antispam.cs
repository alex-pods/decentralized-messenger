using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MessengerDesktop.Antispam;
using MessengerDesktop.Appearance;

namespace MessengerDesktop;

public static partial class Preview
{
    public static bool AntispamOnly { get; set; }
    static async Task AuditAntispam(Window owner, Navigator nav)
    {
        var tag="aim"+Guid.NewGuid().ToString("N")[..7];
        var api=new MessengerClient(Server!);using var other=new MessengerClient(Server!);
        await api.Register(tag,"testPass!42");await api.Login(tag,"testPass!42");
        await api.UpdateProfile("Алиса Королёва",null);
        await other.Register("b"+tag,"testPass!42");await other.Login("b"+tag,"testPass!42");
        await other.UpdateProfile("Максим Орлов",null);
        var chat=await api.CreateDirect((await other.GetMe()).Id);
        var me=await api.GetMe();
        var vm=await Dispatcher.UIThread.InvokeAsync(()=>{var model=new ShellViewModel(nav,api,me);nav.Go(model);return model;});
        await Until(()=>vm.Chats.Count>0,"Antispam test chat missing");
        await Dispatcher.UIThread.InvokeAsync(()=>vm.SelectedChat=vm.Chats.First());
        for(var i=0;i<5;i++)await api.SendMessage(chat,"Сообщение "+(i+1));
        await Dispatcher.UIThread.InvokeAsync(()=>vm.Draft="Черновик должен сохраниться");
        await OnUiAsync(()=>vm.SendCommand.ExecuteAsync(null));
        await Until(()=>vm.AntispamRequired,"No verification prompt after burst");
        Expect(vm.Draft=="Черновик должен сохраниться"&&!vm.CanSendDraft,"Blocked send discarded draft or remained enabled");
        foreach(var dark in new[]{false,true})foreach(var width in new[]{1180,360})
        {
            await Dispatcher.UIThread.InvokeAsync(()=>{ThemeSettings.Apply(ThemeSettings.Presets[dark?1:0]);Resize(owner,width);owner.Height=width==360?600:780;});
            await Task.Delay(150);await Shot(owner,$"antispam-banner-{(dark?"dark":"light")}-{width}");
        }

        var concentration=Environment.GetEnvironmentVariable("MESSENGER_PREVIEW_CONCENTRATION")=="true";
        var sceneOnly=Environment.GetEnvironmentVariable("MESSENGER_PREVIEW_SCENE_ONLY")=="true";
        var audible=Environment.GetEnvironmentVariable("MESSENGER_PREVIEW_AUDIBLE")=="true";
        async Task Foreground(AntispamWindow dialog)
        {
            for(var i=0;i<600&&!dialog.HasNativeFocus;i++)await Task.Delay(100);
            Expect(dialog.HasNativeFocus,"Native test window needs foreground activation");
        }
        async Task TestExit(string kind,bool withVideo=false)
        {
            AntispamWindow dialog=null!;Task<AntispamOutcome?> result=null!;Window? distraction=null;
            await Dispatcher.UIThread.InvokeAsync(()=>{dialog=new AntispamWindow(api) { MutePreviewAudio=true };result=dialog.ShowDialog<AntispamOutcome?>(owner);});
            await Until(()=>dialog.ReadyToStart,"Start screen not ready");
            await Foreground(dialog);
            Expect(dialog.Phase==null && RawMouse.CursorVisible,"Start screen captured cursor or started round");
            await OnUiAsync(()=>dialog.PreviewStart(withVideo));
            for(var i=0;i<200&&dialog.Phase==null&&!result.IsCompleted;i++)await Task.Delay(50);
            Expect(dialog.Phase is "countdown" or "running", "Native 3D did not start: "+dialog.Failure);
            await Task.Delay(200);
            Expect(!RawMouse.CursorVisible,$"System cursor visible: {kind}, video={withVideo}, active={dialog.IsActive}, closed={result.IsCompleted}, failure={dialog.Failure}, {dialog.MouseDiagnostics}");
            await Dispatcher.UIThread.InvokeAsync(()=>
            {
                if(kind=="escape")dialog.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape});
                else if(kind=="close")dialog.Close();
                else if(kind=="fullscreen")dialog.WindowState=WindowState.Normal;
                else { distraction = new Window { Width = 200, Height = 100, Title = "Focus test" }; distraction.Show(); }
            });
            var outcome=await result.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.UIThread.InvokeAsync(()=>distraction?.Close());
            Expect(outcome?.Passed!=true,"Leaving verification was treated as success: "+kind);
            await api.RefreshAntispam();Expect(api.AntispamBlocked,"Exit removed block: "+kind);
            Console.WriteLine($"PASS interruption {kind}, video={withVideo}: cursor hidden, block retained");
            await Task.Delay(250);
        }
        if(!sceneOnly&&Environment.GetEnvironmentVariable("MESSENGER_PREVIEW_ROUND_ONLY")!="true")
        {
            foreach(var kind in new[]{"escape","close","fullscreen","focus"})await TestExit(kind);
            if(concentration)await TestExit("focus",true);
            Console.WriteLine("PASS native verification interruption: Escape, close, fullscreen exit and focus loss during countdown");
        }

        AntispamWindow game=null!;Task<AntispamOutcome?> completion=null!;AimState? state=null;
        await Dispatcher.UIThread.InvokeAsync(()=>{game=new AntispamWindow(api) { MutePreviewAudio=!audible,PreviewClipId=Environment.GetEnvironmentVariable("MESSENGER_PREVIEW_CLIP") };game.StateReceived+=s=>state=s;completion=game.ShowDialog<AntispamOutcome?>(owner);});
        await Until(()=>game.ReadyToStart,"Start screen not ready");
        await Foreground(game);
        await Shot(game,"antispam-start");
        await Task.Delay(1200);
        Expect(game.Phase==null&&RawMouse.CursorVisible,"Idle start screen started verification");
        if(!concentration)
        {
            await OnUiAsync(()=>game.PreviewStart(true));
            Expect(game.ReadyToStart&&game.Phase==null&&RawMouse.CursorVisible,"Unavailable media captured input or started a challenge");
            await Shot(game,"antispam-media-unavailable");
        }
        await OnUiAsync(()=>game.PreviewStart(concentration));
        for(var i=0;i<200&&state?.Phase!="running"&&!completion.IsCompleted;i++)await Task.Delay(50);
        Expect(state?.Phase=="running","Native game did not start: "+game.Failure);
        if(concentration)Expect(game.VideoTime is >= 3500 and <= 5000,"Video is not synchronized to target start: "+game.VideoTime);
        if(audible)Expect(game.AudioPlaying,"Audio output failed: "+game.AudioError);
        await Task.Delay(250);
        async Task SceneShot(string name)=>await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var visual = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(game)!;
            using var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            snapshot.Save(Path.Combine(OutDir,name+".png"));
        });
        await SceneShot("antispam-native-3d");
        Console.WriteLine($"PASS initial camera: yaw={game.CameraAngles.Yaw:0.000000}, pitch={game.CameraAngles.Pitch:0.000000}, audible={audible}");
        double yaw=0,pitch=0;const double gain=2*Math.PI/108117*9;
        if(sceneOnly)
        {
            await Dispatcher.UIThread.InvokeAsync(()=>game.PreviewMouse(320,80,false));
            await Task.Delay(180);await SceneShot("antispam-native-3d-angle");
            await Dispatcher.UIThread.InvokeAsync(()=>{game.Fail("Предпросмотр завершён.");owner.Close();});
            Console.WriteLine("PASS scene screenshot captured; preview stopped");return;
        }
        for(var hit=1;hit<=60;hit++)
        {
            await Task.Delay(120);
            var target=state!.Targets[0];var x=target.GetProperty("x").GetDouble()-480;var y=270-target.GetProperty("y").GetDouble();
            var nextYaw=Math.Atan2(-x,AimCamera.TargetDistance);var nextPitch=Math.Atan2(y,Math.Sqrt(x*x+AimCamera.TargetDistance*AimCamera.TargetDistance));
            var dx=(int)Math.Round((yaw-nextYaw)/gain);var dy=(int)Math.Round((pitch-nextPitch)/gain);
            yaw-=dx*gain;pitch-=dy*gain;
            await Dispatcher.UIThread.InvokeAsync(()=>game.PreviewMouse(dx,dy,true));
            for(var wait=0;wait<40&&state.Score<hit*100&&!completion.IsCompleted;wait++)await Task.Delay(50);
            Expect(state.Score==hit*100,"Native input/geometry failed at hit "+hit+": "+game.Failure);
        }
        Expect(!game.ShowingResult&&state!.Phase=="running","6000 ended native round early");
        var sawFade=false;var middleShot=false;var maximumDrift=0d;
        for(var i=0;i<700&&!game.ShowingResult&&!completion.IsCompleted;i++)
        {
            await Task.Delay(50);
            if(concentration&&game.VideoOpacity is > .1f and < .9f)sawFade=true;
            if(concentration&&!game.ShowingResult&&game.VideoTime>0)
            {
                maximumDrift=Math.Max(maximumDrift,Math.Abs(game.VideoTime-game.MusicPositionMs));
                if(!middleShot&&game.VideoTime>=19000){middleShot=true;await SceneShot("antispam-mid-round");}
                if(audible)Expect(game.AudioPlaying,"Audio stopped during playback: "+game.AudioError);
            }
        }
        Expect(game.ShowingResult&&state!.Phase=="passed","Native 30-second round did not complete: "+game.Failure+" phase="+state?.Phase);
        if(concentration)Expect(sawFade,"Video did not fade after full round");
        if(concentration)
        {
            Expect(game.MusicPositionMs>=35000,"Soundtrack ended before full clip: "+game.MusicPositionMs);
            Expect(maximumDrift<750,"Audio/video clocks drifted: "+maximumDrift);
            Console.WriteLine($"PASS full media: music={game.MusicPositionMs:0}ms, maximum video/audio clock offset={maximumDrift:0}ms, audible={audible}");
        }
        Console.WriteLine("PASS render measurement: "+game.RenderStats);
        Expect(game.ShowingResult&&state!.Phase=="passed","Native 30-second round did not complete: "+game.Failure+" phase="+state?.Phase);
        await Task.Delay(150);
        await Shot(game,"antispam-result");
        await Dispatcher.UIThread.InvokeAsync(game.DismissResult);
        Expect((await completion.WaitAsync(TimeSpan.FromSeconds(5)))?.Passed==true,"Native game did not complete");
        await api.RefreshAntispam();Expect(!api.AntispamBlocked,"Server did not release native verification");
        await OnUiAsync(()=>vm.SendCommand.ExecuteAsync(null));
        Expect(vm.Draft=="","Draft did not send after verification");
        Console.WriteLine("PASS native fullscreen OpenGL: reference 3D, raw mouse camera, server-verified 6000, preserved draft and resumed send");
        await Dispatcher.UIThread.InvokeAsync(owner.Close);
    }
}
