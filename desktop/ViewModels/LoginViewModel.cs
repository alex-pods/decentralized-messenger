using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class LoginViewModel : ObservableObject
{
    static readonly Regex LoginRe = new(@"^[A-Za-z0-9_.\-]{3,16}$", RegexOptions.CultureInvariant);

    private readonly Navigator _nav;
    private SessionStore.Saved? _saved;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ServerAddress))]
    private string _server = "http://127.0.0.1:5080/messenger";
    [ObservableProperty] private string _login = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _connectionError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthAction))]
    private bool _busy;
    [ObservableProperty] private bool _showConnection;

    public LoginViewModel(Navigator nav, string? server = null, string? error = null, SessionStore.Saved? saved = null)
    {
        _nav = nav;
        _saved = saved;
        if (!string.IsNullOrWhiteSpace(server)) Server = server;
        Error = error;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthTitle))]
    [NotifyPropertyChangedFor(nameof(AuthAction))]
    [NotifyPropertyChangedFor(nameof(ModePrompt))]
    [NotifyPropertyChangedFor(nameof(ModeAction))]
    private bool _isRegistration;

    public string AuthTitle => IsRegistration ? "Новый аккаунт" : "Вход";
    public string ServerAddress => Uri.TryCreate(Server.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
        ? uri.Authority + (uri.AbsolutePath == "/" ? "" : uri.AbsolutePath.TrimEnd('/'))
        : "Укажите адрес сервера";
    public string AuthAction => Busy ? "Подождите…" : IsRegistration ? "Создать аккаунт" : "Войти";
    public string ModePrompt => IsRegistration ? "Уже есть аккаунт?" : "Нет аккаунта?";
    public string ModeAction => IsRegistration ? "Войти" : "Создать аккаунт";
    public bool CanRestoreSaved => _saved != null;

    [RelayCommand] void SwitchMode() { IsRegistration = !IsRegistration; Error = null; }
    [RelayCommand] Task Submit() => Auth(IsRegistration);

    [RelayCommand]
    Task SignIn() => Auth(register: false);

    [RelayCommand]
    Task SignUp() => Auth(register: true);

    [RelayCommand]
    void ToggleConnection() => ShowConnection = !ShowConnection;

    [RelayCommand]
    internal async Task RestoreSaved()
    {
        if (Busy || _saved is not { } saved) return;
        Busy = true;
        Error = null;
        ConnectionError = null;
        MessengerClient? api = null;
        try
        {
            api = SessionStore.ConnectSaved(saved, Server);
            await OpenAccount(api);
            api = null;
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            SessionStore.Clear();
            _saved = null;
            OnPropertyChanged(nameof(CanRestoreSaved));
            Error = "Сессия завершена. Войдите снова.";
        }
        catch (Exception ex)
        {
            ShowConnection = true;
            ConnectionError = Friendly(ex);
        }
        finally
        {
            api?.Dispose();
            Busy = false;
        }
    }

    async Task Auth(bool register)
    {
        if (Busy) return;
        Error = null;
        ConnectionError = null;
        var login = Login.Trim();
        var password = Password;
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
        MessengerClient? api = null;
        try
        {
            var expected = _saved == null ? null : SessionStore.GetFingerprint(_saved);
            api = new MessengerClient(Server, expectedFingerprint: expected);
            if (register)
                await api.Register(login, password);
            await api.Login(login, password);
            await OpenAccount(api);
            api = null;
        }
        catch (Exception ex)
        {
            if (ex is ServerIdentityException or HttpRequestException or TaskCanceledException)
            {
                ShowConnection = true;
                ConnectionError = Friendly(ex);
            }
            else Error = Friendly(ex);
        }
        finally { api?.Dispose(); Busy = false; }
    }

    async Task OpenAccount(MessengerClient api)
    {
        var me = await api.GetMe();
        SessionStore.Save(api);
        _nav.Go(new ShellViewModel(_nav, api, me, _saved?.Server ?? Server));
    }

    partial void OnServerChanged(string value) => ConnectionError = null;

    /// <summary>Текст для UI: у ApiException берём русский error; машинный code — в ApiException.Code.</summary>
    public static string Friendly(Exception ex) => ex switch
    {
        ApiException a => string.IsNullOrWhiteSpace(a.Message) ? (a.Code ?? "Ошибка") : a.Message,
        HttpRequestException => "Нет соединения с сервером",
        TaskCanceledException => "Сервер не ответил",
        _ => ex.Message
    };
}
