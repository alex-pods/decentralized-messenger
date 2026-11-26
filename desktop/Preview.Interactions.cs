using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
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
        var list = shell.FindControl<ItemsControl>("ChatMatches")!;
        var people = shell.FindControl<ItemsControl>("PeopleMatches")!;
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
            vm.ShowGroupCommand.Execute(null);
        });
        await Until(() => vm.OverlayOpen, "New group dialog did not open");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var scrim = shell.FindControl<Border>("OverlayScrim")!;
            Press(shell.FindControl<Border>("OverlaySheet")!, window);
            Expect(vm.OverlayOpen, "Click inside sheet dismissed it");
            Press(scrim, window);
            Expect(!vm.OverlayOpen, "Click outside sheet did not dismiss it");
        });
        await AuditWorkspaceNavigation(window, shell, vm, api);
        await AuditForwarding(window, shell, vm, api);

        using var guest = new MessengerClient(api.ServerBase);
        var tag = "audit" + Guid.NewGuid().ToString("N")[..8];
        await guest.Register(tag, "temporary-password");
        await guest.Login(tag, "temporary-password");
        await guest.UpdateProfile("Саша Ветров", null);
        var guestId = (await guest.GetMe()).Id;
        var prefix = tag[..^3].ToUpperInvariant();
        Expect((await api.Search(prefix)).All(user => user.Id != guestId), "Legacy tag search should remain exact");
        Expect((await api.Search(prefix, prefix: true)).Any(user => user.Id == guestId), "Tag prefix search did not find a visible profile");
        Expect((await api.Search("au_it", prefix: true)).All(user => user.Id != guestId), "Tag prefix treated underscore as a wildcard");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            search.Text = "@wrongtag";
            search.Text = "@" + prefix;
        });
        await Until(() => people.Items.OfType<UserCardDto>().Any(user => user.Id == guestId), "Sidebar prefix search did not find a new contact");
        await Shot(window, "audit-search-new-contact");
        await Dispatcher.UIThread.InvokeAsync(() =>
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }));
        await Until(() => vm.SelectedChat?.AvatarUserId == guestId && string.IsNullOrEmpty(search.Text), "Search result did not open the new direct chat");
        await Dispatcher.UIThread.InvokeAsync(() => search.Text = "@" + tag);
        await Until(() => list.Items.OfType<ChatItem>().SingleOrDefault()?.AvatarUserId == guestId &&
            !shell.FindControl<TextBlock>("SearchStatus")!.IsVisible, "Existing contact did not appear as a local chat");
        Expect(people.Items.OfType<UserCardDto>().All(user => user.Id != guestId), "Existing contact was duplicated in global results");
        await Dispatcher.UIThread.InvokeAsync(() => search.Clear());
        var directChatId = vm.SelectedChat!.Id;
        var originalMessage = await guest.SendMessage(directChatId, "Встретимся у главного входа?");
        var quotedMessage = await api.SendMessage(directChatId, "Да, в семь.", originalMessage);
        await Until(() => vm.Messages.Any(message => message.Id == quotedMessage && message.ReplyAuthor == "Саша Ветров"
            && message.Reply.Contains("главного входа")), "Reply did not resolve source author and text");
        await Until(() => vm.Chats.First().Id == directChatId, "New messages did not move the chat to the top");
        await OnUiAsync(() => vm.JumpToReply(originalMessage));
        await Until(() => vm.SelectedMessage?.Id == originalMessage, "Reply jump did not reach the original");
        await Shot(window, "audit-reply-jump");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.ShowDirectCommand.Execute(null);
            vm.SearchQuery = "@wrongtag";
            vm.SearchQuery = " @" + prefix + " ";
        });
        await Until(() => vm.SearchResults.SingleOrDefault()?.Id == guestId, "Debounced tag lookup did not show current user");
        await Shot(window, "audit-people-search");
        await Dispatcher.UIThread.InvokeAsync(() =>
            shell.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PersonTag" && box.IsEffectivelyVisible)
                .RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }));
        await Until(() => !vm.OverlayOpen && vm.SelectedChat?.AvatarUserId == guestId,
            "Enter did not open the user selected by the direct chat dialog");
        await guest.UpdateProfile(null, null, false);
        using var stranger = new MessengerClient(Server!);
        var strangerLogin = "stranger" + Guid.NewGuid().ToString("N")[..8];
        await stranger.Register(strangerLogin, "preview-check-password");
        await stranger.Login(strangerLogin, "preview-check-password");
        foreach (var (query, isPrefix) in new[] { (tag, false), (prefix, true) })
        {
            Expect((await stranger.Search(query, prefix: isPrefix)).All(user => user.Id != guestId),
                "A stranger found a private tag");
            Expect((await api.Search(query, prefix: isPrefix)).Any(user => user.Id == guestId),
                "Existing direct chat did not make the private tag findable");
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.ShowDirectCommand.Execute(null);
            vm.SearchQuery = tag;
        });
        await Until(() => vm.SearchResults.Any(user => user.Id == guestId), "Private tag lookup omitted a known contact");
        await Dispatcher.UIThread.InvokeAsync(() => vm.CloseOverlayCommand.Execute(null));

        var group = await api.CreateGroup("Проверка событий", []);
        var groupOnly = await stranger.CreateGroup("Поиск в общей группе", [guestId]);
        Expect((await stranger.Search(tag)).Any(user => user.Id == guestId), "Shared group did not make a private tag findable");
        await stranger.RemoveMember(groupOnly, guestId);
        Expect((await stranger.Search(prefix, prefix: true)).Any(user => user.Id == guestId),
            "A former group member disappeared from search");
        await stranger.AddMember(groupOnly, guestId);
        await stranger.RemoveMember(groupOnly, guestId);
        var beforeJoin = await api.SendMessage(group, "Обсуждаем обновление.");
        await OnUiAsync(() => vm.ReloadChatsCommand.ExecuteAsync(null));
        await Until(() => vm.Chats.Any(c => c.Id == group), "New group missing");
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.Single(c => c.Id == group));
        var path = $"chats/{group}/activity";
        var empty = await api.Send<GroupActivityItem[]>(HttpMethod.Get, path, null, default);
        Expect(empty is { Length: 0 }, "New group history should be empty");
        await ExpectActivityDenied(guest, path);
        await Until(() => vm.Timeline.OfType<MessageItem>().Any(message => message.Id == beforeJoin),
            "Opening the group did not load the existing message");
        await api.AddMember(group, guestId);
        await Until(() => vm.Timeline.OfType<GroupActivityLine>().Any(line => line.Description.Contains("Саша Ветров")),
            "Join event did not appear in the conversation timeline");
        var afterJoin = await guest.SendMessage(group, "Всем привет!");
        await api.AddMember(group, guestId);
        await guest.Leave(group);
        await api.AddMember(group, guestId);
        await api.RemoveMember(group, guestId);
        await Until(() => vm.Timeline.OfType<GroupActivityLine>().Count() == 4 &&
            vm.Timeline.LastOrDefault() is GroupActivityLine { Event.Kind: "removed" }, "Live group events did not update the timeline");
        var history = await api.Send<GroupActivityItem[]>(HttpMethod.Get, path, null, default) ?? [];
        Expect(history.Select(item => item.Kind).SequenceEqual(new[] { "removed", "joined", "left", "joined" }), "Wrong event order or duplicate join");
        await ExpectActivityDenied(guest, path);
        var older = await api.Send<GroupActivityItem[]>(HttpMethod.Get, path + "?before_id=" + history[0].Id, null, default);
        Expect(older!.Select(item => item.Id).SequenceEqual(history.Skip(1).Select(item => item.Id)), "Activity history cursor repeated the current page");
        var log = shell.FindControl<ListBox>("Log")!;
        await Until(() => log.Items.OfType<MessageItem>().Any(message => message.Id == afterJoin) &&
            log.GetVisualDescendants().OfType<StackPanel>().Count(row => row.Classes.Contains("groupEvent")) == 4,
            "Group events were not rendered in the message list");
        await Dispatcher.UIThread.InvokeAsync(() =>
            Expect(vm.Timeline.Take(3).ToArray() is [MessageItem first, GroupActivityLine { Event.Kind: "joined" }, MessageItem last]
                && first.Id == beforeJoin && last.Id == afterJoin, "Activity was not placed between its surrounding messages"));
        await Shot(window, "audit-group-activity");
        foreach (var kind in new[] { "joined", "removed" })
        {
            await Dispatcher.UIThread.InvokeAsync(() => log.GetVisualDescendants().OfType<Button>().First(button =>
                button.DataContext is GroupActivityLine line && line.Event.Kind == kind && button.IsEffectivelyVisible)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Until(() => vm.Workspace?.UserId == guestId, "Clicking a group event opened the wrong profile");
            await Dispatcher.UIThread.InvokeAsync(vm.BackWorkspace);
        }
        for (var i = 0; i < 14; i++)
        {
            await api.AddMember(group, guestId);
            await guest.Leave(group);
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.SelectedChat = vm.Chats.First(chat => !chat.IsGroup);
            vm.SelectedChat = vm.Chats.Single(chat => chat.Id == group);
        });
        await Until(() => vm.Timeline.OfType<GroupActivityLine>().Count() == 30 && vm.HasOlderTimeline,
            "Group did not show its most recent event page after reopening");
        await OnUiAsync(() => vm.LoadOlderCommand.ExecuteAsync(null));
        await Until(() => vm.Timeline.OfType<GroupActivityLine>().Select(line => line.Event.Id).Distinct().Count() == 32 &&
            !vm.HasOlderTimeline, "Older group events did not appear in the same timeline");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Resize(window, 1180);
            vm.ShowMembersCommand.Execute(null);
        });
        await Until(() => vm.Workspace?.Page == WorkspacePage.Members && !vm.ShowMembersCommand.IsRunning &&
            shell.FindControl<Border>("WorkspaceSurface")!.IsEffectivelyVisible && !vm.OverlayOpen, "Group settings did not replace the chat");
        await Shot(window, "audit-group-details-in-pane");
        await Dispatcher.UIThread.InvokeAsync(vm.BackWorkspace);
        await AuditProfilePhoto(window, vm, api);
        Console.WriteLine("PASS native input routing: outside dismissal, keyboard search, tag privacy and debouncing");
        Console.WriteLine("PASS group activity: chronological timeline, live updates, pagination, duplicate invite ignored and former-member access denied");
    }

    static async Task AuditProfilePhoto(Window window, ShellViewModel vm, MessengerClient api)
    {
        var profile = await api.GetMe();
        using var bitmap = new SkiaSharp.SKBitmap(32, 32);
        bitmap.Erase(SkiaSharp.SKColors.SteelBlue);
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var file = Path.Combine(OutDir, "profile-test-photo.png");
        File.WriteAllBytes(file, bytes);
        try
        {
            await api.UpdateProfileWithAvatar("Недопустимое фото", "Эти строки не должны сохраниться", [1, 2, 3], "invalid.png");
            throw new Exception("Server accepted an invalid avatar");
        }
        catch (ApiException ex) when (ex.Status == 400) { }
        Expect((await api.GetMe()).DisplayName == profile.DisplayName, "Invalid avatar request changed the profile name");

        await OnUiAsync(() => vm.ShowProfileCommand.ExecuteAsync(null));
        await Dispatcher.UIThread.InvokeAsync(() => { vm.ProfileEditing = true; vm.DisplayName = "Черновик профиля"; });
        await OnUiAsync(() => vm.StageAvatar(file));
        await Until(() => vm.HasDraftAvatar, "The selected photo did not appear in the profile draft");
        var pending = await api.GetMe();
        Expect(pending.DisplayName == profile.DisplayName && pending.PathToAvatarFile == profile.PathToAvatarFile,
            "Selecting a profile photo updated the server before save");
        await Dispatcher.UIThread.InvokeAsync(() => vm.ResetProfileDraft());
        await Until(() => !vm.ProfileEditing && !vm.HasDraftAvatar && vm.DisplayName == profile.DisplayName,
            "Cancel did not remove the staged profile photo");

        await Dispatcher.UIThread.InvokeAsync(() => { vm.ProfileEditing = true; vm.DisplayName = "Алиса Светлова"; vm.Bio = "Профиль проверен."; });
        await OnUiAsync(() => vm.StageAvatar(file));
        await OnUiAsync(() => vm.SaveProfileCommand.ExecuteAsync(null));
        var saved = await api.GetMe();
        Expect(saved.DisplayName == "Алиса Светлова" && saved.Bio == "Профиль проверен." &&
            (await api.GetAvatar(saved.Id))?.SequenceEqual(bytes) == true, "Profile text and avatar were not committed together");
        await Until(() => !vm.ProfileEditing && !vm.HasDraftAvatar && vm.HasAvatar, "Saved avatar did not replace the preview");
        await Shot(window, "audit-profile-photo-saved");
        await Dispatcher.UIThread.InvokeAsync(vm.BackWorkspace);
        Console.WriteLine("PASS profile: image validation, cancellable draft, atomic save and uploaded avatar");
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
        var sourceText = string.Join('\n', Enumerable.Repeat("https://example.org/share/" + new string('a', 80), 14));
        var longMessage = await api.SendMessage(sourceChat.Id, sourceText);
        await Until(() => vm.Messages.Any(message => message.Id == longMessage), "Long source message was not delivered");
        var target = await Dispatcher.UIThread.InvokeAsync(() => vm.Chats.First(chat => chat.Title.StartsWith("Максим")));
        var before = await api.GetMessages(target.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.SelectedMessage = vm.Messages.Single(message => message.Id == longMessage);
            vm.ShowForwardCommand.Execute(null);
            search.Text = "неизвестный получатель";
        });
        await Until(() => empty.IsEffectivelyVisible && !send.IsEnabled, "Empty result still allows sending");
        var heading = shell.FindControl<Grid>("OverlayHeader")!;
        var emptyTop = await Dispatcher.UIThread.InvokeAsync(() => heading.TranslatePoint(new Point(), shell)!.Value.Y);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            search.Text = "ОРЛОВ Максим";
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        });
        await Until(() => (recipients.SelectedItem as ChatItem)?.Id == target.Id && send.IsEnabled,
            "Keyboard search did not select the correct recipient");
        await Dispatcher.UIThread.InvokeAsync(() =>
            Expect(Math.Abs(heading.TranslatePoint(new Point(), shell)!.Value.Y - emptyTop) <= 1,
                "Filtering moved the heading of the forward dialog"));
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
        var sourcePreview = panel.FindControl<Expander>("SourcePreview")!;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.Height = 560;
            search.Clear();
            sourcePreview.IsExpanded = true;
        });
        await Until(() => sourcePreview.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Any(text => text.IsEffectivelyVisible && text.Text == vm.ForwardSource!.Text), "Expanded forward preview lost the source text");
        await Until(() => recipients.Items.Count >= 5 &&
            send.TranslatePoint(new Point(0, send.Bounds.Height), shell.FindControl<Border>("OverlaySheet")!) is { } bottom &&
            bottom.Y <= shell.FindControl<Border>("OverlaySheet")!.Bounds.Height - 12 &&
            recipients.TranslatePoint(new Point(0, recipients.Bounds.Height), panel) is { } recipientsBottom &&
            recipientsBottom.Y < send.TranslatePoint(new Point(), panel)!.Value.Y,
            "Long forward preview or recipient list overlapped the send action");
        await Shot(window, "audit-forward-expanded-short");
        await Dispatcher.UIThread.InvokeAsync(() => window.Height = 600);

        var sourceId = await Dispatcher.UIThread.InvokeAsync(() => vm.ForwardSource!.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.CloseOverlayCommand.Execute(null);
            vm.SelectedMessage = vm.Messages.Single(message => message.Id == sourceId);
            vm.ShowForwardCommand.Execute(null);
        });
        await Until(() => vm.Overlay == "forward" && vm.ForwardSource?.Id == sourceId &&
                          search.IsEffectivelyVisible && string.IsNullOrEmpty(search.Text) && recipients.SelectedItem == null,
            "Reopening retained the old recipient or filter");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            search.Text = "@" + target.Tag;
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        });
        await Until(() => (recipients.SelectedItem as ChatItem)?.Id == target.Id &&
                          (send.CommandParameter as ChatItem)?.Id == target.Id && send.IsEnabled,
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
        var empty = shell.FindControl<TextBlock>("EmptyConversation")!;
        var sidebar = shell.FindControl<Border>("ChatSidebar")!;
        var composer = shell.FindControl<Border>("ConversationComposer")!;
        var pane = shell.FindControl<Border>("ScreenBay")!;
        var headerContent = shell.FindControl<Grid>("ConversationHeaderContent")!;
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var center = empty.TranslatePoint(new Point(empty.Bounds.Width / 2, empty.Bounds.Height / 2), shell)!.Value;
                Expect(empty.Text == "Выберите чат" && Math.Abs(center.X - shell.Bounds.Width / 2) < 2,
                    "Empty conversation does not use the centered, single-line prompt");
            });
            await Shot(window, $"audit-empty-{(dark ? "dark" : "light")}");
        }
        await Dispatcher.UIThread.InvokeAsync(() => Resize(window, 360));
        await Until(() => sidebar.IsEffectivelyVisible && !empty.IsEffectivelyVisible,
            "Compact window stranded the user on the empty conversation");
        await Shot(window, "audit-empty-compact");
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.First(chat => chat.Title.StartsWith("Максим")));
        await Until(() => header.IsEffectivelyVisible && !empty.IsEffectivelyVisible && !sidebar.IsEffectivelyVisible,
            "Selecting a conversation did not open it in the compact window");
        await Dispatcher.UIThread.InvokeAsync(() => Resize(window, 1440));
        await Until(() => window.ClientSize.Width > 1420 && pane.Bounds.Width > 1100 && composer.IsEffectivelyVisible,
            "Wide conversation layout did not open");
        var log = shell.FindControl<ListBox>("Log")!;
        await Until(() => log.GetVisualDescendants().OfType<Grid>().Any(row => row.Classes.Contains("messageRow")),
            "The message column did not load");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var headerLeft = headerContent.TranslatePoint(default, pane)!.Value.X;
            Expect(headerLeft is >= 10 and <= 18 && pane.Bounds.Width - headerLeft - headerContent.Bounds.Width is >= 10 and <= 18,
                "Chat header no longer spans the pane");
            var row = log.GetVisualDescendants().OfType<Grid>().First(control => control.Classes.Contains("messageRow"));
            foreach (var control in new Control[] { row, composer })
                Expect(control.TranslatePoint(new Point(control.Bounds.Width / 2, 0), shell) is { } center &&
                    Math.Abs(center.X - shell.Bounds.Width / 2) < 2, "Chat content is off the window axis");
        });
        await Shot(window, "audit-centered-chat-wide");
        await Dispatcher.UIThread.InvokeAsync(() => Resize(window, 900));
        await Until(() => composer.TranslatePoint(default, pane) is { X: >= 12 } && composer.Bounds.Width > 510 &&
            sidebar.IsEffectivelyVisible, "The chat column overlapped the sidebar or became too narrow");
        await Dispatcher.UIThread.InvokeAsync(() => Resize(window, 1180));
        Console.WriteLine("PASS chat layout: window centering, sidebar clearance, wide header and compact navigation");
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
