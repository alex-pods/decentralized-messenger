using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MessengerDesktop.Views.Dialogs;

public partial class UserProfilePage : UserControl
{
    CancellationTokenSource? _load;
    ShellViewModel _shell = null!;
    string _userId = "";
    Bitmap? _photo;
    bool _attached;

    public UserProfilePage() => InitializeComponent();
    void OnProfileSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var gutter = (double)Application.Current!.FindResource("DialogScrollBarWidth")!;
        ProfileContent.Width = Math.Max(0, e.NewSize.Width - gutter);
    }

    public UserProfilePage(ShellViewModel shell, string userId) : this()
    {
        _shell = shell;
        _userId = userId;
        PhotoFrame.DataContext = userId;
        SetName(shell.KnownContactName(userId) ?? "");
        ErrorText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) ErrorText.IsVisible = !string.IsNullOrEmpty(ErrorText.Text);
        };
        WriteMessage.IsVisible = userId != shell.CurrentUserId && shell.SelectedChat?.AvatarUserId != userId;
        AttachedToVisualTree += async (_, _) => { _attached = true; await LoadProfile(); };
        Retry.Click += async (_, _) => await LoadProfile();
        CopyUsername.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && Username.Text != null)
                {
                    await clipboard.SetTextAsync(Username.Text);
                    UsernameLabel.Text = "Тег скопирован";
                }
            }
            catch { ErrorText.Text = "Не удалось скопировать тег"; }
        };
        WriteMessage.Click += async (_, _) =>
        {
            WriteMessage.IsEnabled = false;
            var error = await shell.OpenUserConversation(userId);
            if (!_attached) return;
            if (error != null) { ErrorText.Text = error; WriteMessage.IsEnabled = true; }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _load?.Cancel();
            Photo.Source = null;
            Initial.IsVisible = true;
            _photo?.Dispose();
            _photo = null;
        };
    }

    void SetName(string name)
    {
        ContactName.Text = name;
        Initial.Text = StringInfo.GetNextTextElement(name).ToUpperInvariant();
    }

    async Task LoadProfile()
    {
        _load?.Cancel();
        using var load = new CancellationTokenSource();
        _load = load;
        var ct = load.Token;
        Loading.IsVisible = true; Retry.IsVisible = false; ErrorText.Text = null;
        UsernameLabel.Text = "Имя пользователя";
        try
        {
            var person = await _shell.ThemeApi.GetUser(_userId, ct);
            ct.ThrowIfCancellationRequested();
            var displayName = string.IsNullOrWhiteSpace(person.DisplayName) ? "@" + person.Tag : person.DisplayName;
            SetName(displayName);
            Username.Text = "@" + person.Tag; ProfileDetails.IsVisible = true;
            Biography.Text = person.Bio; About.IsVisible = !string.IsNullOrWhiteSpace(person.Bio);
            Activity.Text = FormatActivity(person.LastSeenAt);
            Activity.IsVisible = !string.IsNullOrEmpty(Activity.Text);
            ToolTip.SetTip(Activity, MessageMapper.FormatDateTime(person.LastSeenAt));
            WriteMessage.IsEnabled = true;
            try
            {
                var bytes = await _shell.ThemeApi.GetAvatar(_userId, ct);
                ct.ThrowIfCancellationRequested();
                using var stream = bytes == null ? null : new MemoryStream(bytes);
                var next = stream == null ? null : new Bitmap(stream);
                _photo?.Dispose();
                _photo = next;
                Photo.Source = _photo; Initial.IsVisible = _photo == null;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { ErrorText.Text = "Фото не загрузилось"; }
        }
        catch (Exception)
        {
            if (ct.IsCancellationRequested) return;
            ErrorText.Text = "Не удалось загрузить профиль. Проверьте соединение и попробуйте снова.";
            Retry.IsVisible = true;
        }
        finally
        {
            if (ReferenceEquals(_load, load))
            {
                _load = null;
                Loading.IsVisible = false;
            }
        }
    }

    internal static string FormatActivity(string? value)
    {
        if (!DateTimeOffset.TryParse(value, out var date)) return "";
        var local = date.ToLocalTime();
        if (local.Date == DateTime.Today) return "Был(а) сегодня в " + local.ToString("HH:mm");
        if (local.Date == DateTime.Today.AddDays(-1)) return "Был(а) вчера в " + local.ToString("HH:mm");
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        return "Был(а) " + local.ToString(local.Year == DateTime.Today.Year ? "d MMM, HH:mm" : "dd.MM.yyyy", culture);
    }
}
