using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Appearance;
using MessengerDesktop.Views;
using MessengerDesktop.Views.Dialogs;

namespace MessengerDesktop;

public static partial class Preview
{
    static async Task AuditConversations(Window window, ShellViewModel vm)
    {
        var shell = await Dispatcher.UIThread.InvokeAsync(() => window.GetVisualDescendants().OfType<ShellView>().Single());
        foreach (var dark in new[] { false, true })
        foreach (var width in new[] { 1180, 360 })
        {
            var suffix = (dark ? "dark" : "light") + "-" + width;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ThemeSettings.Apply(ThemeSettings.Presets[dark ? 1 : 0]);
                Resize(window, width); window.Height = width == 360 ? 600 : 780;
                vm.CloseOverlayCommand.Execute(null);
                vm.SelectedChat = vm.Chats.First(c => c.Title.StartsWith("Максим"));
                shell.FindControl<Button>("ChatIdentity")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var contact = await Dispatcher.UIThread.InvokeAsync(() => window.OwnedWindows.OfType<UserProfileWindow>().Single());
            await Until(() => !contact.FindControl<ProgressBar>("Loading")!.IsVisible, "Contact profile did not load");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (width == 360) { contact.Width = 360; contact.Height = 520; }
                Expect(contact.FindControl<TextBlock>("PersonName")!.Text == "Максим Орлов", "Header opened the wrong user profile");
                Expect(contact.FindControl<Button>("WriteMessage")!.IsEnabled, "Loaded contact cannot start a conversation");
            });
            await Task.Delay(120);
            await Shot(contact, "mvp-contact-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() => contact.FindControl<Button>("WriteMessage")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Until(() => !window.OwnedWindows.OfType<UserProfileWindow>().Any(), "Write message did not close contact profile");
            Expect(vm.SelectedChat?.Title == "Максим Орлов", "Write message selected a different chat");

            await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.First(c => c.Title == "Дизайн-команда"));
            await OnUiAsync(() => vm.ShowMembersCommand.ExecuteAsync(null));
            Expect(vm.CanInviteMembers && vm.Members.Count == 4, "Group management lacks owner permissions or members");
            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(() => Expect(shell.FindControl<ScrollViewer>("OverlayScroll")!.Offset.Y < 1,
                "Group details opened below the header"));
            await Shot(window, "mvp-group-info-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() => shell.FindControl<Border>("OverlaySheet")!.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd());
            await Task.Delay(120);
            await Shot(window, "mvp-group-actions-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var member = shell.GetVisualDescendants().OfType<MemberList>().Single();
                member.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row") && b.DataContext is MemberRow { Title: "Ева Лебедева" })
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            contact = await Dispatcher.UIThread.InvokeAsync(() => window.OwnedWindows.OfType<UserProfileWindow>().Single());
            await Until(() => !contact.FindControl<ProgressBar>("Loading")!.IsVisible, "Member profile did not load");
            Expect(contact.FindControl<TextBlock>("PersonName")!.Text == "Ева Лебедева", "Member click opened a different profile");
            await Dispatcher.UIThread.InvokeAsync(() => { contact.Close(); vm.CloseOverlayCommand.Execute(null); });
            await Dispatcher.UIThread.InvokeAsync(() => shell.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("authorName") && b.IsEffectivelyVisible)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            contact = await Dispatcher.UIThread.InvokeAsync(() => window.OwnedWindows.OfType<UserProfileWindow>().Single());
            await Until(() => !contact.FindControl<ProgressBar>("Loading")!.IsVisible, "Message author profile did not load");
            await Dispatcher.UIThread.InvokeAsync(() => contact.Close());
        }
        using var groupOwner = new MessengerClient(Server!);
        var login = "owner" + Guid.NewGuid().ToString("N")[..8];
        await groupOwner.Register(login, "preview-check-password");
        await groupOwner.Login(login, "preview-check-password");
        var limitedGroup = await groupOwner.CreateGroup("Группа: права участника", [vm.CurrentUserId]);
        await OnUiAsync(() => vm.ReloadChatsCommand.ExecuteAsync(null));
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.Single(c => c.Id == limitedGroup));
        await OnUiAsync(() => vm.ShowMembersCommand.ExecuteAsync(null));
        Expect(!vm.CanInviteMembers && !vm.CanManagePins && vm.Members.All(m => !m.CanKick && !m.CanPromote && !m.CanTransfer), "Ordinary member sees admin actions");
        await Task.Delay(100);
        await Shot(window, "mvp-group-member-permissions");
        await OnUiAsync(() => vm.HideChatCommand.ExecuteAsync(null));
        await OnUiAsync(() => vm.ReloadChatsCommand.ExecuteAsync(null));
        await Dispatcher.UIThread.InvokeAsync(() => { Resize(window, 1180); window.Height = 780; ThemeSettings.Apply(ThemeSettings.Presets[0]); });
        Console.WriteLine("PASS conversation details: header and member profiles, data from API, start conversation, group participants and management in light/dark at 360/1180");
    }
}
