using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

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
