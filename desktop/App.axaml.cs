using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
                LocalHistoryStore.DirectoryPath = Path.Combine(Preview.OutDir, "test-history");
                Appearance.WallpaperSettings.DirectoryPath = Path.Combine(Preview.OutDir, "test-wallpapers");
                ServerTrustStore.TestFilePath = Path.Combine(Preview.OutDir, "test-server-trust.json");
            }
            Appearance.ThemeSettings.Restore();
            Settings.AppPreferences.Load();
            Appearance.WallpaperSettings.Restore();
            var window = new MainWindow();
            var nav = new Navigator(window);
            desktop.MainWindow = window;
            var saved = Preview.Server == null ? SessionStore.Load() : null;
            var login = new LoginViewModel(nav, Program.ServerOverride ?? saved?.Server, saved: saved);
            window.DataContext = login;
            if (Preview.Server != null)
                _ = Preview.Run(window, nav).ContinueWith(t =>
                {
                    Console.Error.WriteLine("PREVIEW FAIL " + t.Exception);
                    Environment.Exit(1);
                }, TaskContinuationOptions.OnlyOnFaulted);
            else if (saved != null)
                _ = login.RestoreSaved();
        }
        base.OnFrameworkInitializationCompleted();
    }

}
