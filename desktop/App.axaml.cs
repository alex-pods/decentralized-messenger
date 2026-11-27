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
                AccountStore.FilePath = Path.Combine(Preview.OutDir, "test-accounts.json");
                LocalPinStore.DirectoryPath = Path.Combine(Preview.OutDir, "test-pins");
                Appearance.WallpaperSettings.DirectoryPath = Path.Combine(Preview.OutDir, "test-wallpapers");
            }
            Appearance.ThemeSettings.Restore();
            Settings.AppPreferences.Load();
            Appearance.WallpaperSettings.Restore();
            var window = new MainWindow();
            var nav = new Navigator(window);
            nav.Accounts = new AccountManager(nav);
            desktop.MainWindow = window;
            window.DataContext = new LoginViewModel(nav, Program.ServerOverride, accounts: nav.Accounts);
            if (Preview.Server != null)
                _ = Preview.Run(window, nav).ContinueWith(t =>
                {
                    Console.Error.WriteLine("PREVIEW FAIL " + t.Exception);
                    Environment.Exit(1);
                }, TaskContinuationOptions.OnlyOnFaulted);
            else
                _ = nav.Accounts.RestoreAsync(Program.ServerOverride);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
