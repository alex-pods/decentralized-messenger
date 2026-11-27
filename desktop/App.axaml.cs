using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MessengerDesktop.Views;

namespace MessengerDesktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Preview.Server != null)
            {
                Appearance.ThemeSettings.FilePath = Path.Combine(Preview.OutDir, "test-appearance.json");
                Settings.AppPreferences.FilePath = Path.Combine(Preview.OutDir, "test-settings.json");
                LocalPinStore.DirectoryPath = Path.Combine(Preview.OutDir, "test-pins");
                Appearance.WallpaperSettings.DirectoryPath = Path.Combine(Preview.OutDir, "test-wallpapers");
            }
            Appearance.ThemeSettings.Restore();
            Settings.AppPreferences.Load();
            Appearance.WallpaperSettings.Restore();
            var window = new MainWindow();
            var nav = new Navigator(window);
            desktop.MainWindow = window;
            window.DataContext = new LoginViewModel(nav, Program.ServerOverride);
            if (Preview.Server != null)
                _ = Preview.Run(window, nav).ContinueWith(t =>
                {
                    Console.Error.WriteLine("PREVIEW FAIL " + t.Exception);
                    Environment.Exit(1);
                }, TaskContinuationOptions.OnlyOnFaulted);
            else
                _ = RestoreSession(nav, window);
        }
        base.OnFrameworkInitializationCompleted();
    }

    static async Task RestoreSession(Navigator nav, Avalonia.Controls.Window window)
    {
        var saved = SessionStore.Load();
        if (saved == null) return;
        if (Program.ServerOverride != null && !string.Equals(Program.ServerOverride, saved.Server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return;
        var api = new MessengerClient(saved.Server, saved.Token);
        try
        {
            var me = await api.GetMe();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (window.DataContext is LoginViewModel)
                    nav.Go(new ShellViewModel(nav, api, me));
                else
                    api.Dispose();
            });
        }
        catch
        {
            api.Dispose();
            SessionStore.Clear();
        }
    }
}
