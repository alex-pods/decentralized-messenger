using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Settings;
using MessengerDesktop.Views;

namespace MessengerDesktop;

public static partial class Preview
{
    static async Task AuditInteractions(Window window, ShellViewModel vm, MessengerClient api)
    {
        var shell = await Dispatcher.UIThread.InvokeAsync(() => window.GetVisualDescendants().OfType<ShellView>().Single());
        await AuditEmptyConversation(window, shell, vm);
        var search = shell.FindControl<TextBox>("ChatSearch")!;
        var list = shell.FindControl<ListBox>("ChatList")!;
        var peer = await Dispatcher.UIThread.InvokeAsync(() => vm.Chats.First(c => c.Title.StartsWith("Максим")));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Resize(window, 1180);
            vm.CloseOverlayCommand.Execute(null);
            Appearance.ThemeSettings.Apply(Appearance.ThemeSettings.Presets[0]);
            search.Text = "  орлов    МАКСИМ ";
        });
        await Until(() => list.Items.Count == 1 && ReferenceEquals(list.Items[0], peer), "Multiword chat filter failed");
        await Dispatcher.UIThread.InvokeAsync(() => search.Text = "@" + peer.Tag);
        await Until(() => list.Items.Count == 1 && ReferenceEquals(list.Items[0], peer), "Tag chat filter failed");
        await Shot(window, "audit-search-tag");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Expect(vm.SelectedChat?.Id == peer.Id, "Enter did not open chat search result");
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Expect(string.IsNullOrEmpty(search.Text), "Escape did not clear chat search");
            vm.ShowProfileCommand.Execute(null);
        });
        await Until(() => vm.OverlayOpen, "Profile did not open");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var scrim = shell.FindControl<Border>("OverlayScrim")!;
            Press(shell.FindControl<Border>("OverlaySheet")!, window);
            Expect(vm.OverlayOpen, "Click inside sheet dismissed it");
            Press(scrim, window);
            Expect(!vm.OverlayOpen, "Click outside sheet did not dismiss it");
            SettingsWindow.Open(vm, window);
        });
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        await Until(() => lifetime.Windows.OfType<SettingsWindow>().Any(), "Settings did not open");
        await Dispatcher.UIThread.InvokeAsync(() => Press(window, window));
        await Until(() => !lifetime.Windows.OfType<SettingsWindow>().Any(), "Owner click did not dismiss settings");
        await AuditForwarding(window, shell, vm, api);

        using var guest = new MessengerClient(api.ServerBase);
        var tag = "audit" + Guid.NewGuid().ToString("N")[..8];
        await guest.Register(tag, "temporary-password");
        await guest.Login(tag, "temporary-password");
        await guest.UpdateProfile("Саша Ветров", null);
        var guestId = (await guest.GetMe()).Id;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.ShowDirectCommand.Execute(null);
            vm.SearchQuery = "@wrongtag";
            vm.SearchQuery = " @" + tag.ToUpperInvariant() + " ";
        });
        await Until(() => vm.SearchResults.SingleOrDefault()?.Id == guestId, "Debounced tag lookup did not show current user");
        await Shot(window, "audit-people-search");
        await Dispatcher.UIThread.InvokeAsync(() => vm.CloseOverlayCommand.Execute(null));
        await guest.UpdateProfile(null, null, false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.ShowDirectCommand.Execute(null);
            vm.SearchQuery = tag;
        });
        await Until(() => vm.SearchHint.StartsWith("Нет доступных"), "Private tag lookup returned an unexpected result");
        await Dispatcher.UIThread.InvokeAsync(() => vm.CloseOverlayCommand.Execute(null));

        var group = await api.CreateGroup("Проверка событий", []);
        await OnUiAsync(() => vm.ReloadChatsCommand.ExecuteAsync(null));
        await Until(() => vm.Chats.Any(c => c.Id == group), "New group missing");
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.Single(c => c.Id == group));
        var path = $"chats/{group}/activity";
        var empty = await api.Send<GroupActivityItem[]>(HttpMethod.Get, path, null, default);
        Expect(empty is { Length: 0 }, "New group history should be empty");
        await ExpectActivityDenied(guest, path);
        await api.AddMember(group, guestId);
        await Until(() => vm.LatestGroupActivity?.Contains("Саша Ветров") == true, "Join event did not appear in chat");
        await api.AddMember(group, guestId);
        await guest.Leave(group);
        await api.AddMember(group, guestId);
        await api.RemoveMember(group, guestId);
        await Until(() => vm.GroupActivity.Count == 4 && vm.GroupActivity[0].Kind == "removed", "Recent group events did not update");
        var history = await api.Send<GroupActivityItem[]>(HttpMethod.Get, path, null, default);
        Expect(history!.Select(item => item.Kind).SequenceEqual(new[] { "removed", "joined", "left", "joined" }), "Wrong event order or duplicate join");
        await ExpectActivityDenied(guest, path);
        await Shot(window, "audit-group-activity");
        await Dispatcher.UIThread.InvokeAsync(() =>
            shell.FindControl<Button>("GroupActivityButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
        await Until(() => vm.Overlay == "members" &&
            shell.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Недавние события")).IsExpanded,
            "Group event did not open the expanded activity history");
        await Shot(window, "audit-group-activity-details");
        Console.WriteLine("PASS native input routing: outside dismissal, keyboard search, tag privacy and debouncing");
        Console.WriteLine("PASS group activity: persisted join/leave/removal, duplicate invite ignored, live update and former-member access denied");
    }

    static async Task AuditForwarding(Window window, ShellView shell, ShellViewModel vm, MessengerClient api)
    {
        var panel = shell.FindControl<Control>("ForwardPanel")!;
        var search = panel.FindControl<TextBox>("DestinationSearch")!;
        var recipients = panel.FindControl<ListBox>("Destinations")!;
        var send = panel.FindControl<Button>("SendForward")!;
        var empty = panel.FindControl<TextBlock>("NoResults")!;
        var sourceChat = await Dispatcher.UIThread.InvokeAsync(() => vm.Chats.First(chat => chat.IsGroup));
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = sourceChat);
        await Until(() => vm.Messages.Any(message => !message.IsDeleted) && Equals(vm.SelectedChat, sourceChat),
            "No source message for forwarding");
        var target = await Dispatcher.UIThread.InvokeAsync(() => vm.Chats.First(chat => chat.Title.StartsWith("Максим")));
        var before = await api.GetMessages(target.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.SelectedMessage = vm.Messages.First(message => !message.IsDeleted && !message.IsNotStored);
            vm.ShowForwardCommand.Execute(null);
            search.Text = "неизвестный получатель";
        });
        await Until(() => empty.IsEffectivelyVisible && !send.IsEnabled, "Empty result still allows sending");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            search.Text = "ОРЛОВ Максим";
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        });
        await Until(() => recipients.SelectedItem == target && send.IsEnabled, "Keyboard search did not select the correct recipient");
        Expect((await api.GetMessages(target.Id)).Count == before.Count, "Selecting the recipient sent a message");

        foreach (var dark in new[] { false, true })
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Resize(window, 360);
                window.Height = 600;
                Appearance.ThemeSettings.Apply(Appearance.ThemeSettings.Presets[dark ? 1 : 0]);
            });
            await Until(() => send.IsEffectivelyVisible && send.Bounds.Height >= 32, "Send action is missing in the compact picker");
            await Shot(window, $"audit-forward-selected-{(dark ? "dark" : "light")}-360");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.CloseOverlayCommand.Execute(null);
            vm.ShowForwardCommand.Execute(null);
        });
        await Until(() => string.IsNullOrEmpty(search.Text) && recipients.SelectedItem == null,
            "Reopening retained the old recipient or filter");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            search.Text = "@" + target.Tag;
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        });
        await Until(() => recipients.SelectedItem == target && send.CommandParameter == target,
            "Tag selection did not bind to the send action");
        await OnUiAsync(() => vm.ForwardToCommand.ExecuteAsync(send.CommandParameter));
        var forwarded = (await api.GetMessages(target.Id)).Where(item => item.Id > before.Last().Id).ToArray();
        Expect(forwarded is [{ ForwardedFrom: not null }], "Expected one forwarded message with attribution");
        await Until(() => !vm.OverlayOpen, "Forward picker stayed open after confirmation");

        var sidebar = shell.FindControl<Border>("ChatSidebar")!;
        var back = shell.FindControl<Button>("BackToChats")!;
        await Dispatcher.UIThread.InvokeAsync(() => back.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
        await Until(() => sidebar.IsEffectivelyVisible, "Back did not reveal compact chat list");
        await OnUiAsync(() => vm.ReloadChatsCommand.ExecuteAsync(null));
        await Until(() => sidebar.IsEffectivelyVisible, "A background refresh changed the compact page");
        var chatList = shell.FindControl<ListBox>("ChatList")!;
        await Dispatcher.UIThread.InvokeAsync(() => chatList.ScrollIntoView(chatList.SelectedIndex));
        await Until(() => chatList.ContainerFromIndex(chatList.SelectedIndex) != null,
            "Active chat did not realize a row in the compact list");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var row = (ListBoxItem)chatList.ContainerFromIndex(chatList.SelectedIndex)!;
            using var pointer = new Pointer(82, PointerType.Mouse, true);
            var position = row.TranslatePoint(new Point(10, 10), window)!.Value;
            var press = new PointerPressedEventArgs(row, pointer, window, position, 1,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
            row.RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, press) { Source = row });
        });
        await Until(() => !sidebar.IsEffectivelyVisible, "Clicking the active chat did not reopen the compact conversation");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Resize(window, 1180);
            window.Height = 780;
            Appearance.ThemeSettings.Apply(Appearance.ThemeSettings.Presets[0]);
        });
        Console.WriteLine("PASS forwarding: select without sending, reopen/reset, multiword and tag lookup, explicit send and attribution");
        Console.WriteLine("PASS compact navigation: background refresh keeps the chat list open and a tap reopens the active chat");
    }

    static async Task AuditEmptyConversation(Window window, ShellView shell, ShellViewModel vm)
    {
        var header = shell.FindControl<Border>("ConversationHeader")!;
        var empty = shell.FindControl<StackPanel>("EmptyConversation")!;
        var sidebar = shell.FindControl<Border>("ChatSidebar")!;
        foreach (var dark in new[] { false, true })
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.CloseOverlayCommand.Execute(null);
                vm.SelectedChat = null;
                Resize(window, 1180);
                Appearance.ThemeSettings.Apply(Appearance.ThemeSettings.Presets[dark ? 1 : 0]);
            });
            await Until(() => empty.IsEffectivelyVisible && !header.IsEffectivelyVisible,
                "Empty conversation still shows chat header");
            await Shot(window, $"audit-empty-{(dark ? "dark" : "light")}");
        }
        await Dispatcher.UIThread.InvokeAsync(() => Resize(window, 360));
        await Until(() => sidebar.IsEffectivelyVisible && !empty.IsEffectivelyVisible,
            "Compact window stranded the user on the empty conversation");
        await Shot(window, "audit-empty-compact");
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.First());
        await Until(() => header.IsEffectivelyVisible && !empty.IsEffectivelyVisible && !sidebar.IsEffectivelyVisible,
            "Selecting a conversation did not open it in the compact window");
        Console.WriteLine("PASS empty conversation: light/dark, hidden header, compact list and opening a chat");
    }

    static void Press(Control target, Window root)
    {
        using var pointer = new Pointer(81, PointerType.Mouse, true);
        var position = target.TranslatePoint(new Point(3, 3), root) ?? new Point(3, 3);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, root, position, 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
    }

    static async Task ExpectActivityDenied(MessengerClient api, string path)
    {
        try { await api.Send<GroupActivityItem[]>(HttpMethod.Get, path, null, default); }
        catch (ApiException ex) when (ex.Status is 403 or 404) { return; }
        throw new Exception("Non-member can read group activity");
    }
}
