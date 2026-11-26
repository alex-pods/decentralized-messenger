using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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
            var contact = await Dispatcher.UIThread.InvokeAsync(() => shell.GetVisualDescendants().OfType<UserProfilePage>().Single());
            await Until(() => !contact.FindControl<ProgressBar>("Loading")!.IsVisible, "Contact profile did not load");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Expect(contact.FindControl<TextBlock>("ContactName")!.Text == "Максим Орлов", "Header opened the wrong user profile");
                Expect(!contact.FindControl<Button>("WriteMessage")!.IsVisible, "Current direct chat shows a redundant write action");
            });
            await Task.Delay(120);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var profile = contact.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
                foreach (var name in new[] { "Activity", "Username", "Biography" })
                {
                    var text = contact.FindControl<TextBlock>(name)!;
                    var origin = text.TranslatePoint(new Point(), profile)!.Value;
                    Expect(origin.X >= -.5 && origin.X + text.Bounds.Width <= profile.Bounds.Width + .5,
                        $"Contact field {name} runs outside the profile viewport at {width}");
                }
            });
            await Shot(window, "mvp-contact-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() => shell.FindControl<Button>("WorkspaceBack")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Until(() => vm.Workspace == null && vm.SelectedChat?.Title == "Максим Орлов", "Back did not restore the same private chat");

            await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.First(c => c.Title == "Дизайн-команда"));
            await OnUiAsync(() => vm.ShowMembersCommand.ExecuteAsync(null));
            Expect(vm.CanInviteMembers && vm.Members.Count == 4, "Group management lacks owner permissions or members");
            await Task.Delay(150);
            var details = await Dispatcher.UIThread.InvokeAsync(() => shell.GetVisualDescendants().OfType<ConversationDetails>().Single());
            var directory = details.FindControl<MemberList>("MemberDirectory")!;
            await Until(() => directory.Viewport is { Offset.Y: < 1 }, "Group details opened below the header");
            await Shot(window, "mvp-group-info-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() => directory.Viewport!.ScrollToEnd());
            await Task.Delay(120);
            await Shot(window, "mvp-group-actions-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var member = shell.GetVisualDescendants().OfType<MemberList>().Single();
                member.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row") && b.DataContext is MemberRow { Title: "Ева Лебедева" })
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            contact = await Dispatcher.UIThread.InvokeAsync(() => shell.GetVisualDescendants().OfType<UserProfilePage>().Single());
            await Until(() => !contact.FindControl<ProgressBar>("Loading")!.IsVisible, "Member profile did not load");
            Expect(await Dispatcher.UIThread.InvokeAsync(() => contact.FindControl<TextBlock>("ContactName")!.Text) == "Ева Лебедева",
                "Member click opened a different profile");
            await Dispatcher.UIThread.InvokeAsync(() => shell.FindControl<Button>("WorkspaceBack")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Until(() => details.IsEffectivelyVisible && vm.Workspace?.Page == WorkspacePage.Members, "Back did not restore the same group page");
            await Dispatcher.UIThread.InvokeAsync(() => shell.FindControl<Button>("WorkspaceBack")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            var senderId = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var avatar = shell.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("messageAvatar") && b.IsEffectivelyVisible);
                avatar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return ((MessageItem)avatar.DataContext!).SenderId;
            });
            contact = await Dispatcher.UIThread.InvokeAsync(() => shell.GetVisualDescendants().OfType<UserProfilePage>().Single());
            await Until(() => !contact.FindControl<ProgressBar>("Loading")!.IsVisible, "Message author profile did not load");
            Expect(vm.Workspace?.UserId == senderId, "Message avatar opened the wrong sender");
            await Dispatcher.UIThread.InvokeAsync(vm.BackWorkspace);
        }
        using var groupOwner = new MessengerClient(Server!);
        var login = "owner" + Guid.NewGuid().ToString("N")[..8];
        await groupOwner.Register(login, "preview-check-password");
        await groupOwner.Login(login, "preview-check-password");
        var longName = "АлександраКонстантинопольская";
        var longBio = string.Join('\n', Enumerable.Repeat("https://example.org/" + new string('а', 79), 5)) + "!";
        await groupOwner.UpdateProfile(longName, longBio);
        var longContact = await groupOwner.GetMe();
        var card = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.NavigateWorkspace(WorkspacePage.Contact, longContact.Id);
            return shell.GetVisualDescendants().OfType<UserProfilePage>().Single();
        });
        await Until(() =>
        {
            var content = card.GetVisualDescendants().OfType<ScrollContentPresenter>().SingleOrDefault();
            var bio = card.FindControl<TextBlock>("Biography")!;
            var name = card.FindControl<TextBlock>("ContactName")!;
            var write = card.FindControl<Button>("WriteMessage")!;
            return content != null && name.Text == longName && bio.Text == longBio && write.IsEnabled &&
                bio.Bounds.Width <= content.Bounds.Width && name.Bounds.Width < shell.Bounds.Width &&
                write.TranslatePoint(new Point(0, write.Bounds.Height), card) is { } edge && edge.Y <= card.Bounds.Height;
        }, "Long profile text escaped the viewport or covered its write action");
        await Shot(window, "audit-contact-long-bio-360");
        await Dispatcher.UIThread.InvokeAsync(vm.BackWorkspace);
        var limitedGroup = await groupOwner.CreateGroup("Группа: права участника", [vm.CurrentUserId]);
        await OnUiAsync(() => vm.ReloadChatsCommand.ExecuteAsync(null));
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.Single(c => c.Id == limitedGroup));
        await OnUiAsync(() => vm.ShowMembersCommand.ExecuteAsync(null));
        Expect(!vm.CanInviteMembers && !vm.CanManagePins && vm.Members.All(m => !m.CanKick && !m.CanPromote && !m.CanTransfer), "Ordinary member sees admin actions");
        await Task.Delay(100);
        await Shot(window, "mvp-group-member-permissions");
        var groupDetails = await Dispatcher.UIThread.InvokeAsync(() => shell.GetVisualDescendants().OfType<ConversationDetails>().Single());
        var members = groupDetails.FindControl<MemberList>("MemberDirectory")!;
        var leave = groupDetails.FindControl<Button>("LeaveConversation")!;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            for (var i = 0; i < 24; i++)
                vm.Members.Add(new MemberRow { Id = -800 - i, Title = "Длинное имя участника демонстрационной группы", TagLabel = $"@contact_{i:00}" });
            var ownerIndex = vm.Members.ToList().FindIndex(member => member.Id == longContact.Id);
            if (ownerIndex >= 0) vm.Members.Move(ownerIndex, vm.Members.Count - 1);
            Resize(window, 360);
            window.Height = 560;
        });
        await Until(() => members.Viewport is { } viewport && viewport.Extent.Height > viewport.Viewport.Height && viewport.Viewport.Height > 120 &&
            leave.TranslatePoint(new Point(0, leave.Bounds.Height), shell) is { } bottom &&
            bottom.Y < shell.Bounds.Height, "Large member list pushed group actions outside the window");
        await Dispatcher.UIThread.InvokeAsync(() => members.Viewport!.ScrollToEnd());
        await Until(() => members.Viewport is { Offset.Y: > 200 }, "Group member list does not scroll independently of the action footer");
        await Shot(window, "audit-group-many-members-360");
        var memberOffset = await Dispatcher.UIThread.InvokeAsync(() => members.Viewport!.Offset.Y);
        await Dispatcher.UIThread.InvokeAsync(() => members.GetVisualDescendants().OfType<Button>().First(
            button => button.Classes.Contains("row") && button.DataContext is MemberRow row && row.Id == longContact.Id)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Until(() => vm.Workspace?.Page == WorkspacePage.Contact, "The participant did not open in the same workspace");
        await Dispatcher.UIThread.InvokeAsync(vm.BackWorkspace);
        await Until(() => members.IsEffectivelyVisible && members.Viewport is { } scroll &&
            Math.Abs(scroll.Offset.Y - memberOffset) < 2, "Back did not restore the virtualized member list position");
        await Dispatcher.UIThread.InvokeAsync(() => groupDetails.FindControl<Button>("OpenConversationPins")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        var pinPage = shell.FindControl<PinnedMessagesView>("PinnedMessages")!;
        var pinBack = shell.FindControl<Button>("WorkspaceBack")!;
        var workspace = shell.FindControl<Border>("WorkspaceSurface")!;
        await Until(() => vm.ShowingPinnedMessages && pinPage.IsEffectivelyVisible && pinBack.IsEffectivelyVisible &&
            !workspace.IsEffectivelyVisible,
            "Pinned messages did not replace the group page");
        await Dispatcher.UIThread.InvokeAsync(() => pinBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Until(() => !vm.ShowingPinnedMessages && workspace.IsEffectivelyVisible && members.Viewport is { } groupScroll &&
            Math.Abs(groupScroll.Offset.Y - memberOffset) < 2, "Back from pins did not restore the group page and member position");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var member in vm.Members.Where(member => member.Id < 0).ToArray()) vm.Members.Remove(member);
        });
        await OnUiAsync(() => vm.HideChatCommand.ExecuteAsync(null));
        await OnUiAsync(() => vm.ReloadChatsCommand.ExecuteAsync(null));
        await Dispatcher.UIThread.InvokeAsync(() => { Resize(window, 1180); window.Height = 780; ThemeSettings.Apply(ThemeSettings.Presets[0]); });
        Console.WriteLine("PASS conversation details: header and member profiles, data from API, start conversation, group participants and management in light/dark at 360/1180");
    }
}
