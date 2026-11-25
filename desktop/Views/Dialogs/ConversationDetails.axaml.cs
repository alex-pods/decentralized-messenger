using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MessengerDesktop.Views.Dialogs;

public partial class ConversationDetails : UserControl
{
    public ConversationDetails() => InitializeComponent();
    internal void ShowActivity()
    {
        RecentActivity.IsExpanded = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => RecentActivity.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    void OnPinsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        vm.CloseOverlayCommand.Execute(null);
        vm.ShowPinListCommand.Execute(null);
    }
    async void OnHideClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedChat is not { } chat) return;
        if (await ConfirmAction.Show(this, "Скрыть чат?", "Чат исчезнет только из вашего списка. У собеседников переписка останется.", "Скрыть") && vm.SelectedChat == chat)
            await vm.HideChatCommand.ExecuteAsync(null);
    }
    async void OnLeaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedChat is not { } chat) return;
        if (await ConfirmAction.Show(this, "Покинуть группу?", $"Вы покинете «{chat.Title}». Для возвращения потребуется приглашение.", "Выйти") && vm.SelectedChat == chat)
            await vm.LeaveChatCommand.ExecuteAsync(null);
    }
}
