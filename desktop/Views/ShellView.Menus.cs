using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MessengerDesktop.Views;

public partial class ShellView
{
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

    void OnAccountMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || _vm == null) return;
        var menu = CreateMenu();
        menu.Items.Add(ActionItem("Мой профиль", "IcInfo", _vm.ShowProfileCommand));
        var appearance = new MenuItem { Header = "Оформление" };
        appearance.Click += OnAppearanceClick;
        menu.Items.Add(appearance);
        menu.Items.Add(new Separator());
        menu.Items.Add(ActionItem("Выйти из аккаунта", "IcLogout", _vm.LogoutCommand));
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
            var reactions = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2 };
            foreach (var emoji in _vm.QuickReactions)
            {
                var button = new Button { Content = new ReactionIcon { Emoji = emoji, Width = 22, Height = 22 }, Padding = new Thickness(8) };
                ToolTip.SetTip(button, emoji);
                button.Click += (_, _) => { _vm.ToggleReactionCommand.Execute(emoji); menu.Close(); };
                reactions.Children.Add(button);
            }
            menu.Items.Add(new MenuItem { Header = reactions });
            var more = new MenuItem { Header = "Другие реакции" };
            foreach (var emoji in _vm.MoreReactions)
                more.Items.Add(new MenuItem { Header = emoji, Command = _vm.ToggleReactionCommand, CommandParameter = emoji });
            menu.Items.Add(more);
            menu.Items.Add(new Separator());
            menu.Items.Add(ActionItem("Ответить", "IcReply", _vm.ReplyCommand));
        }
        if (_vm.CanForward) menu.Items.Add(ActionItem("Переслать", "IcForward", _vm.ShowForwardCommand));
        if (_vm.CanPin) menu.Items.Add(ActionItem(_vm.PinActionLabel, "IcPin", _vm.TogglePinCommand));
        if (_vm.CanEdit) menu.Items.Add(ActionItem("Редактировать", "IcEdit", _vm.BeginEditCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(ActionItem("Удалить у меня", "IcTrash", _vm.DeleteForMeCommand));
        if (_vm.CanDeleteEveryone) menu.Items.Add(ActionItem("Удалить у всех", "IcTrash", _vm.DeleteForEveryoneCommand));
        menu.Open(anchor);
    }

}
