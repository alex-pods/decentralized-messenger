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

    static async Task RestoreSession(
    Navigator nav,
    Avalonia.Controls.Window window)
    {
        var saved = SessionStore.Load();

        if (saved is null ||
            window.DataContext is not LoginViewModel login)
        {
            return;
        }

        login.Busy = true;
        MessengerClient? pendingClient = null;

        try
        {
            var api = new MessengerClient(saved.Server, saved.Token);
            pendingClient = api;

            ProfileDto me;
            try
            {
                me = await api.GetMe();
                SessionStore.Save(api.ServerBase, api.Token!, me);
            }
            catch (Exception ex) when (saved.Profile is { Id: > 0 } &&
                (ex is HttpRequestException or TaskCanceledException || ex is ApiException { Status: >= 500 }))
            {
                me = saved.Profile!;
            }
            var core = await DesktopBootstrap.OpenAsync(api, me);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(window.DataContext, login))
                    return;

                nav.Go(new ShellViewModel(nav, api, me, core));

                // Клиент теперь принадлежит ShellViewModel.
                pendingClient = null;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(window.DataContext, login))
                    return;

                if (ex is ApiException { Status: 401 })
                    SessionStore.Clear();

                login.Error = LoginViewModel.Friendly(ex);
            });
        }
        finally
        {
            pendingClient?.Dispose();

            await Dispatcher.UIThread.InvokeAsync(
                () => login.Busy = false);
        }
    }
}
