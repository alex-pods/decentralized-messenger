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
            var window = new MainWindow();
            var nav = new Navigator(window);
            desktop.MainWindow = window;
            window.DataContext = new LoginViewModel(nav);
            _ = RestoreSession(nav, window);
        }
        base.OnFrameworkInitializationCompleted();
    }

    static async Task RestoreSession(Navigator nav, Avalonia.Controls.Window window)
    {
        var saved = SessionStore.Load();
        if (saved == null) return;
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
