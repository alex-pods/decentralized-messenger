using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
namespace MessengerDesktop.Appearance;

public sealed partial class AppearanceEditor
{
    readonly MessengerClient? _api;
    readonly string? _owner;
    ThemeShare? _remote;
    readonly TextBox _themeLink = new() { Name = "ThemeLink", Watermark = "https://…/themes/…" };
    readonly TextBlock _exchangeStatus = new() { Name = "ThemeExchangeStatus", TextWrapping = TextWrapping.Wrap, Classes = { "caption" }, IsVisible = false };
    readonly Button _updateShared = new() { Content = "Обновить опубликованную", Classes = { "secondaryAction" }, IsVisible = false };
    StackPanel _exchange = null!;

    Control SharingControls()
    {
        _exchange = new StackPanel { Name = "ThemeSharing", Spacing = 10 };
        _exchangeStatus.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) _exchangeStatus.IsVisible = !string.IsNullOrEmpty(_exchangeStatus.Text);
        };
        _themeLink.TextChanged += (_, _) =>
        {
            if (_remote != null && _themeLink.Text?.Trim().TrimEnd('/') != _api?.ServerBase + "/themes/" + _remote.Id)
            {
                _remote = null;
                _updateShared.IsVisible = false;
            }
        };
        _exchange.Children.Add(new TextBlock { Text = "Обмен темами", FontWeight = FontWeight.SemiBold });
        _exchange.Children.Add(new TextBlock { Text = "Фон остаётся на этом устройстве и не входит в тему при обмене.", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        _exchange.Children.Add(new TextBlock { Text = "Файл", Classes = { "field" } });
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        var import = new Button { Content = "Импорт файла", Classes = { "secondaryAction" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var export = new Button { Content = "Экспорт файла", Classes = { "secondaryAction" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        import.Click += async (_, _) => await Transfer(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Импорт темы", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Тема Messenger") { Patterns = new[] { "*.json" } } } });
            if (files.Count == 0) { _exchangeStatus.Text = ""; return; }
            await using var stream = await files[0].OpenReadAsync();
            var theme = ThemeExchange.Parse(await ThemeExchange.ReadBounded(stream));
            if (_closed) return;
            _remote = null; _updateShared.IsVisible = false; _themeLink.Text = ""; Load(theme); ThemeSettings.Apply(theme);
            _exchangeStatus.Text = "Предпросмотр импортированной темы. Нажмите «Применить», чтобы сохранить.";
        });
        export.Click += async (_, _) => await Transfer(async () =>
        {
            var data = ThemeExchange.Export(ThemeSettings.Current);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Экспорт темы", SuggestedFileName = "messenger-theme.json", DefaultExtension = "json" });
            if (file == null) { _exchangeStatus.Text = ""; return; }
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await stream.WriteAsync(data);
            _exchangeStatus.Text = "Файл темы сохранён.";
        });
        Grid.SetColumn(export, 2);
        actions.Children.Add(import); actions.Children.Add(export); _exchange.Children.Add(actions);
        var load = new Button { Content = "Загрузить тему", Classes = { "secondaryAction" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 13, Padding = new Thickness(6, 8), IsEnabled = false };
        Avalonia.Automation.AutomationProperties.SetName(load, "Загрузить тему по ссылке");
        load.Click += async (_, _) => await Transfer(async () =>
        {
            if (!Uri.TryCreate(_themeLink.Text?.Trim(), UriKind.Absolute, out var uri)) throw new InvalidDataException("Проверьте ссылку.");
            var share = await ThemeExchange.Download(uri);
            if (_closed) return;
            _remote = _api != null && uri.AbsoluteUri.TrimEnd('/') == _api.ServerBase + "/themes/" + share.Id ? share : null;
            _updateShared.IsVisible = _remote?.OwnerId == _owner && _api != null;
            Load(share.Theme); ThemeSettings.Apply(share.Theme);
            _exchangeStatus.Text = "Тема открыта в предпросмотре. Можно изменить и сохранить свою копию.";
        });
        var publish = new Button { Content = "Опубликовать на сервере", Classes = { "secondaryAction" }, IsEnabled = _api != null };
        publish.Click += async (_, _) => await Transfer(async () =>
        {
            ThemeExchange.Validate(ThemeSettings.Current);
            _remote = await _api!.Send<ThemeShare>(HttpMethod.Post, "themes", new ThemeFile(1, ThemeSettings.Current), default);
            _themeLink.Text = _api.ServerBase + "/themes/" + _remote!.Id;
            _updateShared.IsVisible = true;
            _exchangeStatus.Text = "Тема опубликована. Скопируйте ссылку и отправьте её собеседнику.";
        });
        _updateShared.Click += async (_, _) => await Transfer(async () =>
        {
            if (_remote == null || _api == null) return;
            ThemeExchange.Validate(ThemeSettings.Current);
            _remote = await _api.Send<ThemeShare>(HttpMethod.Patch, "themes/" + _remote.Id,
                new { version = _remote.Version, theme = ThemeSettings.Current }, default);
            _exchangeStatus.Text = "Тема обновлена по прежней ссылке.";
        });
        var copy = new Button { Content = "Копировать ссылку", Classes = { "secondaryAction" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
            FontSize = 13, Padding = new Thickness(6, 8) };
        _themeLink.TextChanged += (_, _) =>
        {
            load.IsEnabled = !string.IsNullOrWhiteSpace(_themeLink.Text);
            copy.IsEnabled = Uri.TryCreate(_themeLink.Text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
        };
        copy.Click += async (_, _) => await Transfer(async () =>
        {
            if (!Uri.TryCreate(_themeLink.Text?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidDataException("Сначала откройте или опубликуйте тему.");
            if (Clipboard == null) throw new InvalidOperationException("Буфер обмена недоступен.");
            await Clipboard.SetTextAsync(uri.AbsoluteUri); _exchangeStatus.Text = "Ссылка скопирована.";
        });
        _exchange.Children.Add(new TextBlock { Text = "Ссылка на сервере", Classes = { "field" }, Margin = new Thickness(0, 4, 0, 0) });
        _exchange.Children.Add(_themeLink);
        Avalonia.Automation.AutomationProperties.SetName(_themeLink, "Ссылка на тему");
        var links = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        Grid.SetColumn(copy, 2);
        links.Children.Add(load);
        links.Children.Add(copy);
        _exchange.Children.Add(links);
        _exchange.Children.Add(publish);
        _exchange.Children.Add(_updateShared);
        _exchange.Children.Add(_exchangeStatus);
        return _exchange;
    }
    async Task Transfer(Func<Task> action)
    {
        _exchange.IsEnabled = false; _exchangeStatus.Text = "Подождите…";
        try { await action(); }
        catch (Exception ex) { if (!_closed) _exchangeStatus.Text = ex switch
            {
                ApiException { Code: "theme_limit" } => "Достигнут лимит публикаций. Можно обновить существующую тему или передать её файлом.",
                ApiException { Status: 409 } => "Тема была изменена. Откройте ссылку заново перед обновлением.",
                ApiException { Status: 403 } => "Изменить эту публикацию может только владелец. Опубликуйте свою копию.",
                HttpRequestException => "Сервер тем недоступен или ссылка не найдена.",
                OperationCanceledException => "Сервер не ответил вовремя. Попробуйте ещё раз.",
                System.Text.Json.JsonException => "Файл не является корректной темой Messenger.",
                _ => ex.Message
            }; }
        finally { if (!_closed) _exchange.IsEnabled = true; }
    }
}
