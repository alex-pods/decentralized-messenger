using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;
namespace MessengerDesktop.Views.Dialogs;
public partial class MemberList : UserControl
{
    public MemberList() => InitializeComponent();
    internal ScrollViewer? Viewport => GroupMembers.FindDescendantOfType<ScrollViewer>();
    internal ListBox List => GroupMembers;
    void OnOpenProfile(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MemberRow member } && DataContext is ShellViewModel vm)
            vm.NavigateWorkspace(WorkspacePage.Contact, member.Id);
    }
    void OnMemberMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MemberRow member } anchor || DataContext is not ShellViewModel vm) return;
        var menu = ShellView.CreateMenu();
        if (member.CanPromote) menu.Items.Add(ShellView.ActionItem(member.PromoteLabel, "IcSettings", vm.ToggleAdminCommand, member));
        if (member.CanTransfer)
        {
            var transfer = new MenuItem { Header = "Передать владение", Icon = new Glyph { Data = (Geometry)Application.Current!.FindResource("IcShield")!, Width = 17, Height = 17 } };
            transfer.Click += async (_, _) =>
            {
                if (await ConfirmAction.Show(this, "Передать владение группой?", $"Новым владельцем станет {member.Title}. Ваши права изменятся.", "Передать"))
                    vm.TransferCommand.Execute(member);
            };
            menu.Items.Add(transfer);
        }
        if (member.CanKick)
        {
            var kick = new MenuItem { Header = "Исключить из группы", Icon = new Glyph { Data = (Geometry)Application.Current!.FindResource("IcTrash")!, Width = 17, Height = 17 }, Foreground = (IBrush)Application.Current!.FindResource("DangerBrush")! };
            kick.Click += async (_, _) =>
            {
                if (await ConfirmAction.Show(this, "Исключить участника?", $"{member.Title} потеряет доступ к группе.", "Исключить"))
                    vm.KickCommand.Execute(member);
            };
            menu.Items.Add(kick);
        }
        menu.Open(anchor);
    }
}
