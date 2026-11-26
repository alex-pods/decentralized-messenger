using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MessengerDesktop.Views;

public partial class PinnedMessagesView : UserControl
{
    public PinnedMessagesView() => InitializeComponent();

    internal static Task<bool> ConfirmSharedUnpin(Control owner) =>
        Dialogs.ConfirmAction.Show(owner, "Открепить сообщение для всех?",
            "Закреп исчезнет у всех участников чата.", "Открепить");

    void OnPinActions(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PinListItem { CanRemove: true } pin } anchor ||
            DataContext is not ShellViewModel vm) return;

        var chatId = vm.SelectedChat?.Id;
        var menu = ShellView.CreateMenu();
        var remove = new MenuItem { Header = pin.RemoveLabel };
        remove.Click += async (_, _) =>
        {
            if ((pin.Local || await ConfirmSharedUnpin(this)) && chatId == vm.SelectedChat?.Id &&
                vm.ShowingPinnedMessages && vm.PinList.Contains(pin))
                await vm.RemovePinFromListCommand.ExecuteAsync(pin);
        };
        menu.Items.Add(remove);
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }
}
