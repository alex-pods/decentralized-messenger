using Avalonia.Controls;
using Avalonia.Interactivity;
namespace MessengerDesktop.Views.Dialogs;
public partial class MemberList : UserControl
{
    public MemberList() => InitializeComponent();
    void OnMemberMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MemberRow member } anchor || DataContext is not ShellViewModel vm) return;
        var menu = ShellView.CreateMenu();
        if (member.CanPromote) menu.Items.Add(ShellView.ActionItem(member.PromoteLabel, "IcSettings", vm.ToggleAdminCommand, member));
        if (member.CanTransfer) menu.Items.Add(ShellView.ActionItem("Передать владение", "IcGroup", vm.TransferCommand, member));
        if (member.CanKick) menu.Items.Add(ShellView.ActionItem("Исключить из группы", "IcTrash", vm.KickCommand, member));
        menu.Open(anchor);
    }
}
