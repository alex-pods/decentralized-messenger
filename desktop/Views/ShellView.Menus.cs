using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace MessengerDesktop.Views;

public partial class ShellView
{
    async void OnReplyQuoteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var target = sender is Control { DataContext: MessageItem quote } ? quote.ReplyToId : vm.ReplyTo?.Id;
        if (target is { } id) await vm.JumpToReply(id);
        e.Handled = true;
    }

    async void OnChatInfoClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedChat is not { } chat) return;
        if (!chat.IsGroup && chat.AvatarUserId is { } userId)
            _vm.NavigateWorkspace(WorkspacePage.Contact, userId);
        else await _vm.ShowMembersCommand.ExecuteAsync(null);
    }

    void OnSenderProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && sender is Control { DataContext: MessageItem message })
            _vm.NavigateWorkspace(WorkspacePage.Contact, message.SenderId);
        e.Handled = true;
    }

    void OnActivityProfileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GroupActivityLine { Event.UserId: { } userId } })
            _vm?.NavigateWorkspace(WorkspacePage.Contact, userId);
        e.Handled = true;
    }

    static Glyph MenuIcon(string name) => new()
    {
        Data = (Geometry)Application.Current!.FindResource(name)!, Width = 17, Height = 17
    };

    internal static MenuItem ActionItem(string title, string icon, ICommand command, object? parameter = null) => new()
    {
        Header = title,
        Command = command,
        CommandParameter = parameter,
        Icon = MenuIcon(icon)
    };

    internal static ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        menu.Background = (IBrush)Application.Current!.FindResource("RaisedBrush")!;
        menu.Foreground = (IBrush)Application.Current!.FindResource("InkBrush")!;
        return menu;
    }

    internal void OpenAccountMenu(Control anchor)
    {
        if (_vm is not { } vm) return;
        var menu = CreateMenu();
        IReadOnlyList<StoredAccount> accounts;
        try { accounts = vm.SavedAccounts; }
        catch (Exception ex)
        {
            vm.Error = "Не удалось прочитать список аккаунтов: " + ex.Message;
            return;
        }
        if (AccountStore.TakeLoadWarning() is { } warning)
            vm.Error = AccountManager.JoinWarnings(vm.Error, warning);
        var showServers = accounts.Select(account => AccountStore.NormalizeServer(account.Server))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        var images = new List<Bitmap>();
        var avatarLoads = new CancellationTokenSource();
        menu.Closed += (_, _) =>
        {
            avatarLoads.Cancel();
            avatarLoads.Dispose();
            foreach (var image in images) image.Dispose();
        };

        foreach (var account in accounts)
        {
            var active = account.Identity == vm.CurrentIdentity;
            var (header, image, initials) = AccountHeader(account, active, showServers, vm);
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = !active
            };
            item.Click += async (_, _) =>
            {
                menu.Close();
                try { await vm.SwitchAccountCommand.ExecuteAsync(account); }
                catch (Exception ex) { vm.Error = LoginViewModel.Friendly(ex); }
            };
            menu.Items.Add(item);
            if (ShouldLoadAccountAvatar(account, active) && image is not null && initials is not null)
                _ = LoadAccountMenuAvatar(account, image, initials, images, avatarLoads.Token);
        }

        menu.Open(anchor);
    }

    internal static bool ShouldLoadAccountAvatar(StoredAccount account, bool active) =>
        !active && !account.NeedsLogin;

    static (Grid Header, Image? Avatar, TextBlock? Initials) AccountHeader(
        StoredAccount account, bool active, bool showServers, ShellViewModel vm)
    {
        var displayName = account.DisplayName ?? "";
        var tag = account.Tag ?? "";
        var avatarText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(displayName) ? new string(tag.Take(1).ToArray()).ToUpperInvariant() : new string(displayName.Trim().Take(1).ToArray()).ToUpperInvariant(),
            Classes = { "avatarText" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var avatar = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            ClipToBounds = true,
            Background = (IBrush)Application.Current!.FindResource("RaisedBrush")!
        };
        var image = new Image { Stretch = Avalonia.Media.Stretch.UniformToFill, IsVisible = active && vm.HasAvatar };
        if (active) image.Source = vm.Avatar;
        avatar.Child = new Panel { Children = { image, avatarText } };
        avatarText.IsVisible = !image.IsVisible;

        var lines = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        lines.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(displayName) ? "@" + tag : displayName,
            FontWeight = FontWeight.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        lines.Children.Add(new TextBlock { Text = "@" + tag, Classes = { "caption" } });
        if (showServers)
            lines.Children.Add(new TextBlock { Text = account.Server, Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis });
        if (account.NeedsLogin)
            lines.Children.Add(new TextBlock { Text = "Требуется вход", Foreground = (IBrush)Application.Current!.FindResource("DangerBrush")! });

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinWidth = 250 };
        header.Children.Add(avatar);
        var detail = new Border { Child = lines, Margin = new Thickness(10, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(detail, 1);
        header.Children.Add(detail);
        if (active)
        {
            var marker = new TextBlock
            {
                Text = "✓",
                Foreground = (IBrush)Application.Current!.FindResource("AccentBrush")!,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(marker, 2);
            header.Children.Add(marker);
        }
        return (header, image, avatarText);
    }

    static async Task LoadAccountMenuAvatar(
        StoredAccount account, Image image, TextBlock initials, List<Bitmap> owned, CancellationToken cancellationToken)
    {
        try
        {
            using var api = new MessengerClient(account.Server, account.Token);
            var bytes = await api.GetAvatar(account.UserId, cancellationToken);
            if (bytes is null) return;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (!image.IsAttachedToVisualTree()) return;
                using var stream = new MemoryStream(bytes);
                var bitmap = Bitmap.DecodeToWidth(stream, 80);
                owned.Add(bitmap);
                image.Source = bitmap;
                image.IsVisible = true;
                initials.IsVisible = false;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiException or TaskCanceledException or IOException)
        {
        }
    }

    void OnNewConversation(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || _vm == null) return;
        var menu = CreateMenu();
        menu.Items.Add(ActionItem("Новый личный чат", "IcCompose", _vm.ShowDirectCommand));
        menu.Items.Add(ActionItem("Новая группа", "IcGroup", _vm.ShowGroupCommand));
        menu.Open(anchor);
    }

    void OnMessageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: MessageItem message } anchor)
        {
            OpenMessageMenu(anchor, message);
            e.Handled = true;
        }
    }

    void OnMessageMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MessageItem message } anchor)
            OpenMessageMenu(anchor, message);
        e.Handled = true;
    }

    internal ContextMenu? ActiveMessageMenu { get; private set; }

    public void OpenMessageMenu(Control anchor, MessageItem message)
    {
        if (_vm == null) return;
        _vm.SelectedMessage = message;
        var menu = CreateMenu();
        ActiveMessageMenu = menu;
        if (!message.IsDeleted)
        {
            var reactions = new ReactionPicker(compact: true) { Width = 264 };
            reactions.Picked += emoji => { _vm.ToggleReactionCommand.Execute(emoji); menu.Close(); };
            menu.Items.Add(new MenuItem { Header = reactions });
            menu.Items.Add(new Separator());
            menu.Items.Add(ActionItem("Ответить", "IcReply", _vm.ReplyCommand));
        }
        if (!message.IsDeleted && !message.IsNotStored)
        {
            var copy = new MenuItem { Header = "Копировать текст", Icon = MenuIcon("IcCopy") };
            copy.Click += async (_, _) =>
            {
                try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(message.Text); }
                catch { if (_vm != null) _vm.Error = "Не удалось скопировать текст"; }
            };
            menu.Items.Add(copy);
        }
        if (_vm.CanForward) menu.Items.Add(ActionItem("Переслать", "IcForward", _vm.ShowForwardCommand));
        if (_vm.CanEdit) menu.Items.Add(ActionItem("Редактировать", "IcEdit", _vm.BeginEditCommand));
        if (_vm.CanPinLocally) menu.Items.Add(ActionItem(_vm.LocalPinActionLabel, "IcDevice", _vm.ToggleLocalPinCommand));
        if (_vm.CanPin && !_vm.SelectedIsPinned) menu.Items.Add(ActionItem(_vm.PinActionLabel, "IcPin", _vm.TogglePinCommand));
        else if (_vm.CanPin)
        {
            var unpin = new MenuItem { Header = _vm.PinActionLabel, Icon = MenuIcon("IcPinOff") };
            unpin.Click += async (_, _) =>
            {
                var vm = _vm;
                var chatId = vm?.SelectedChat?.Id;
                if (vm != null && await PinnedMessagesView.ConfirmSharedUnpin(this) && vm.SelectedChat?.Id == chatId &&
                    vm.SelectedMessage == message && vm.SelectedIsPinned)
                    await vm.TogglePinCommand.ExecuteAsync(null);
            };
            menu.Items.Add(unpin);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(ActionItem("Удалить у меня", "IcTrash", _vm.DeleteForMeCommand));
        if (_vm.CanDeleteEveryone)
        {
            var delete = new MenuItem { Header = "Удалить у всех", Icon = MenuIcon("IcTrash"), Classes = { "danger" } };
            delete.Click += async (_, _) =>
            {
                if (await Dialogs.ConfirmAction.Show(this, "Удалить сообщение у всех?", "Сообщение исчезнет у всех участников. Отменить это действие нельзя.", "Удалить у всех") && _vm?.SelectedMessage == message)
                    _vm.DeleteForEveryoneCommand.Execute(null);
            };
            menu.Items.Add(delete);
        }
        menu.Open(anchor);
    }

}
