using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MessengerDesktop.Views.Dialogs;

public partial class ConversationDetails : UserControl
{
    public ConversationDetails()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !IsVisible) return;
            AddMemberSection.IsExpanded = false;
            MemberDirectory.Viewport?.ScrollToHome();
        };
    }

    void OnPinsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        vm.ShowPinListCommand.Execute(null);
    }
    async void OnHideClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedChat is not { } chat) return;
        if (await ConfirmAction.Show(this, "Скрыть чат?", "Чат исчезнет только из вашего списка. У собеседников переписка останется.", "Скрыть") && vm.SelectedChat?.Id == chat.Id)
            await vm.HideChatCommand.ExecuteAsync(null);
    }
    async void OnLeaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedChat is not { } chat) return;
        if (await ConfirmAction.Show(this, "Покинуть группу?", $"Вы покинете «{chat.Title}». Для возвращения потребуется приглашение.", "Выйти") && vm.SelectedChat?.Id == chat.Id)
            await vm.LeaveChatCommand.ExecuteAsync(null);
    }
}
