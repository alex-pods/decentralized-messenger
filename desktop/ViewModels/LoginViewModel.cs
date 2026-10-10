using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class LoginViewModel : ObservableObject
{
    static readonly Regex LoginRe = new(@"^[A-Za-z0-9_.\-]{3,16}$", RegexOptions.CultureInvariant);

    private readonly Navigator _nav;

    [ObservableProperty] private string _server = "http://127.0.0.1:5080/messenger";
    [ObservableProperty] private string _login = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _busy;

    public LoginViewModel(Navigator nav, string? server = null, string? error = null)
    {
        _nav = nav;
        if (!string.IsNullOrWhiteSpace(server)) Server = server;
        Error = error;
    }

    [RelayCommand]
    Task SignIn() => Auth(register: false);

    [RelayCommand]
    Task SignUp() => Auth(register: true);

    async Task Auth(bool register)
    {
        if (Busy) return;
        var server = Server.Trim().TrimEnd('/');
        var login = Login.Trim();
        var password = Password;
        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Адрес сервера должен начинаться с http:// или https://";
            return;
        }
        if (!LoginRe.IsMatch(login))
        {
            Error = "Логин: 3–16 символов, буквы, цифры, _ . -";
            return;
        }
        if (password.Length < 8)
        {
            Error = "Пароль — минимум 8 символов";
            return;
        }

        Busy = true;
        Error = null;
        var api = new MessengerClient(server);
        try
        {
            if (register)
                await api.Register(login, password);
            await api.Login(login, password);
            var me = await api.GetMe();
            var core = await DesktopBootstrap.OpenAsync(api, me);

            SessionStore.Save(api.ServerBase, api.Token!, me);

            _nav.Go(new ShellViewModel(_nav, api, me, core));
        }
        catch (Exception ex)
        {
            Error = ex is ApiException or HttpRequestException ? Friendly(ex) : ex.Message;
            api.Dispose();
        }
        finally { Busy = false; }
    }

    public static string Friendly(Exception ex) => ex switch
    {
        ApiException a => a.Message,
        HttpRequestException => "Нет соединения с сервером",
        TaskCanceledException => "Сервер не ответил",
        _ => ex.Message
    };
}
