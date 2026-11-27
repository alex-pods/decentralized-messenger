using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class LoginViewModel : ObservableObject
{
    static readonly Regex LoginRe = new(@"^[A-Za-z0-9_.\-]{3,16}$", RegexOptions.CultureInvariant);

    private readonly AccountManager _accounts;
    private readonly ShellViewModel? _returnShell;

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

    public LoginViewModel(
        Navigator nav,
        string? server = null,
        string? error = null,
        AccountManager? accounts = null,
        bool adding = false,
        ShellViewModel? returnShell = null,
        string? login = null)
    {
        _accounts = accounts ?? (nav.Accounts ??= new AccountManager(nav));
        _returnShell = returnShell;
        if (!string.IsNullOrWhiteSpace(server)) Server = server;
        if (!string.IsNullOrWhiteSpace(login)) Login = login;
        IsAdding = adding;
        Error = error;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthTitle))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isAdding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthTitle))]
    [NotifyPropertyChangedFor(nameof(AuthAction))]
    [NotifyPropertyChangedFor(nameof(ModePrompt))]
    [NotifyPropertyChangedFor(nameof(ModeAction))]
    private bool _isRegistration;

    public string AuthTitle => IsRegistration
        ? IsAdding ? "Регистрация аккаунта" : "Новый аккаунт"
        : IsAdding ? "Добавить аккаунт" : "Вход";
    public bool CanCancel => IsAdding || _returnShell is not null;
    public string ServerAddress => Uri.TryCreate(Server.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
        ? uri.Authority + (uri.AbsolutePath == "/" ? "" : uri.AbsolutePath.TrimEnd('/'))
        : "Укажите адрес сервера";
    public string AuthAction => Busy ? "Подождите…" : IsRegistration ? "Создать аккаунт" : "Войти";
    public string ModePrompt => IsRegistration ? "Уже есть аккаунт?" : "Нет аккаунта?";
    public string ModeAction => IsRegistration ? "Войти" : "Создать аккаунт";

    [RelayCommand] void SwitchMode() { IsRegistration = !IsRegistration; Error = null; }
    [RelayCommand] Task Submit() => Auth(IsRegistration);

    [RelayCommand]
    Task SignIn() => Auth(register: false);

    [RelayCommand]
    Task SignUp() => Auth(register: true);

    [RelayCommand]
    void ToggleConnection() => ShowConnection = !ShowConnection;
    [RelayCommand]
    void Cancel() => _accounts.CancelLogin(_returnShell, Server);

    async Task Auth(bool register)
    {
        if (Busy) return;
        Error = null;
        ConnectionError = null;
        var server = Server.Trim().TrimEnd('/');
        var login = Login.Trim();
        var password = Password;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri) ||
            serverUri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(serverUri.Host))
        {
            ShowConnection = true;
            ConnectionError = "Укажите адрес сервера, начиная с http:// или https://";
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
            await _accounts.CompleteLoginAsync(api, me, _returnShell);
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException or TaskCanceledException)
            {
                ShowConnection = true;
                ConnectionError = Friendly(ex);
            }
            else Error = Friendly(ex);
            api.Dispose();
        }
        finally { Busy = false; }
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
