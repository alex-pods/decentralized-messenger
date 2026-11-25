using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace MessengerDesktop.Views.Dialogs;

public partial class UserProfileWindow : Window
{
    readonly CancellationTokenSource _lifetime = new();
    ShellViewModel _shell = null!;
    long _userId;
    Bitmap? _photo;
    bool _closed;

    public UserProfileWindow() => InitializeComponent();

    public UserProfileWindow(ShellViewModel shell, long userId) : this()
    {
        _shell = shell;
        _userId = userId;
        WriteMessage.IsVisible = userId != shell.CurrentUserId;
        Opened += async (_, _) => await LoadProfile();
        Retry.Click += async (_, _) => await LoadProfile();
        CloseProfile.Click += (_, _) => Close();
        CopyUsername.Click += async (_, _) =>
        {
            try { if (Clipboard != null && Username.Text != null) await Clipboard.SetTextAsync(Username.Text); }
            catch { ErrorText.Text = "Не удалось скопировать тег"; }
        };
        WriteMessage.Click += async (_, _) =>
        {
            WriteMessage.IsEnabled = false;
            var error = await shell.OpenUserConversation(userId);
            if (_closed) return;
            if (error == null) Close();
            else { ErrorText.Text = error; WriteMessage.IsEnabled = true; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); Photo.Source = null; _photo?.Dispose(); _lifetime.Dispose(); };
    }

    async Task LoadProfile()
    {
        Loading.IsVisible = true; Retry.IsVisible = false; ErrorText.Text = null;
        try
        {
            var person = await _shell.ThemeApi.GetUser(_userId, _lifetime.Token);
            if (_closed) return;
            PersonName.Text = string.IsNullOrWhiteSpace(person.DisplayName) ? "@" + person.Tag : person.DisplayName;
            Initial.Text = StringInfo.GetNextTextElement(PersonName.Text).ToUpperInvariant();
            Username.Text = "@" + person.Tag; CopyUsername.IsVisible = true;
            Biography.Text = person.Bio; About.IsVisible = !string.IsNullOrWhiteSpace(person.Bio);
            Activity.Text = FormatActivity(person.LastSeenAt);
            Joined.Text = DateTimeOffset.TryParse(person.JoinDate, out var joined)
                ? "В мессенджере с " + joined.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")) : null;
            WriteMessage.IsEnabled = true;
            try
            {
                var bytes = await _shell.ThemeApi.GetAvatar(_userId, _lifetime.Token);
                if (_closed) return;
                _photo?.Dispose();
                using var stream = bytes == null ? null : new MemoryStream(bytes);
                _photo = stream == null ? null : new Bitmap(stream);
                Photo.Source = _photo; Initial.IsVisible = _photo == null;
            }
            catch (Exception) when (!_closed) { ErrorText.Text = "Фото не загрузилось. Остальные данные профиля доступны."; }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception)
        {
            if (_closed) return;
            PersonName.Text = "Профиль недоступен";
            ErrorText.Text = "Не удалось загрузить профиль. Проверьте соединение и попробуйте снова.";
            Retry.IsVisible = true;
        }
        finally { if (!_closed) Loading.IsVisible = false; }
    }

    internal static string FormatActivity(string? value) => DateTimeOffset.TryParse(value, out var date)
        ? "Последняя активность: " + date.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
        : "Нет данных об активности";
}
