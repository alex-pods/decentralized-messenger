using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MessengerDesktop.Views;

public partial class ShellView
{
    async void OnGroupActivityClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedChat is not { IsGroup: true } chat) return;
        await _vm.ShowMembersCommand.ExecuteAsync(null);
        if (_vm.SelectedChat?.Id == chat.Id && _vm.Overlay == "members")
            GroupDetailsPanel.ShowActivity();
    }

    async void OnChatInfoClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedChat is not { } chat || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!chat.IsGroup && chat.AvatarUserId is { } userId)
            await new Dialogs.UserProfileWindow(_vm, userId).ShowDialog(owner);
        else await _vm.ShowMembersCommand.ExecuteAsync(null);
    }

    async void OnSenderProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && sender is Control { DataContext: MessageItem message } && TopLevel.GetTopLevel(this) is Window owner)
            await new Dialogs.UserProfileWindow(_vm, message.SenderId).ShowDialog(owner);
        e.Handled = true;
    }

    internal static MenuItem ActionItem(string title, string icon, ICommand command, object? parameter = null) => new()
    {
        Header = title,
        Command = command,
        CommandParameter = parameter,
        Icon = new Glyph { Data = (Geometry)Application.Current!.FindResource(icon)!, Width = 17, Height = 17 }
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
            var reactions = new ReactionPicker { Width = 264 };
            reactions.Picked += emoji => { _vm.ToggleReactionCommand.Execute(emoji); menu.Close(); };
            menu.Items.Add(new MenuItem { Header = reactions });
            menu.Items.Add(new Separator());
            menu.Items.Add(ActionItem("Ответить", "IcReply", _vm.ReplyCommand));
        }
        if (!message.IsDeleted && !message.IsNotStored)
        {
            var copy = new MenuItem { Header = "Копировать текст" };
            copy.Click += async (_, _) =>
            {
                try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(message.Text); }
                catch { if (_vm != null) _vm.Error = "Не удалось скопировать текст"; }
            };
            menu.Items.Add(copy);
        }
        if (_vm.CanForward) menu.Items.Add(ActionItem("Переслать", "IcForward", _vm.ShowForwardCommand));
        if (_vm.CanPinLocally) menu.Items.Add(ActionItem(_vm.LocalPinActionLabel, "IcPin", _vm.ToggleLocalPinCommand));
        if (_vm.CanPin) menu.Items.Add(ActionItem(_vm.PinActionLabel, "IcPin", _vm.TogglePinCommand));
        if (_vm.CanEdit) menu.Items.Add(ActionItem("Редактировать", "IcEdit", _vm.BeginEditCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(ActionItem("Удалить у меня", "IcTrash", _vm.DeleteForMeCommand));
        if (_vm.CanDeleteEveryone)
        {
            var delete = new MenuItem { Header = "Удалить у всех" };
            delete.Click += async (_, _) =>
            {
                if (await Dialogs.ConfirmAction.Show(this, "Удалить сообщение у всех?", "Сообщение исчезнет у всех участников. Отменить это действие нельзя.", "Удалить") && _vm?.SelectedMessage == message)
                    _vm.DeleteForEveryoneCommand.Execute(null);
            };
            menu.Items.Add(delete);
        }
        menu.Open(anchor);
    }

}
