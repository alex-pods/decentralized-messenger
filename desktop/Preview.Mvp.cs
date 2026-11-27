using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Appearance;
using MessengerDesktop.Settings;
using MessengerDesktop.Views;

namespace MessengerDesktop;

public static partial class Preview
{
    static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine("CHECK FAIL " + message);
            throw new Exception(message);
        }
    }

    static async Task OnUiAsync(Func<Task> action)
    {
        Task running = null!;
        await Dispatcher.UIThread.InvokeAsync(() => { running = action(); });
        await running;
    }

    static async Task Until(Func<bool> condition, string failure)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await Dispatcher.UIThread.InvokeAsync(condition)) return;
            await Task.Delay(50);
        }
        throw new Exception(failure);
    }

    static async Task AuditMvp(Window window, ShellViewModel vm)
    {
        var shell = await Dispatcher.UIThread.InvokeAsync(() => window.GetVisualDescendants().OfType<ShellView>().Single());
        var input = shell.FindControl<TextBox>("ComposerInput")!;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.CloseOverlayCommand.Execute(null);
            vm.SelectedChat = vm.Chats.First(c => c.Title.StartsWith("Максим"));
        });
        await Until(() => !vm.Busy && vm.Messages.Count > 3, "Conversation did not load");
        await AuditComposerKeys(input, vm);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AppPreferences.Update(_ => new UserPreferences { DoubleClick = DoubleClickAction.Reply });
            var message = vm.Messages.First(m => !m.IsDeleted);
            Expect(vm.ActivateMessage(message) && vm.ReplyTo == message, "Double click reply failed");
            vm.CancelComposeCommand.Execute(null);
            AppPreferences.Update(p => p with { DoubleClick = DoubleClickAction.None });
            Expect(!vm.ActivateMessage(message) && vm.ReplyTo == null, "Disabled double click still replies");
            AppPreferences.Update(p => p with { DoubleClick = DoubleClickAction.React, DoubleClickReaction = "🔥" });
            vm.ActivateMessage(message);
        });
        await Until(() => vm.Messages.First().Chips.Any(c => c.Emoji == "🔥" && c.Mine), "Double click reaction failed");
        var log = shell.FindControl<ListBox>("Log")!;
        var menuMessage = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var item = vm.Messages.Last(m => !m.IsDeleted);
            log.ScrollIntoView(item);
            return item.Id;
        });
        Button? menuButton = null;
        await Until(() =>
        {
            menuButton = log.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
                b.Classes.Contains("messageMenu") && b.IsEffectivelyVisible && b.Bounds.Width > 0 &&
                b.DataContext is MessageItem item && item.Id == menuMessage);
            return menuButton != null;
        }, "Scrolled message actions did not appear");
        var messageButton = menuButton!;
        Point MenuCenter() => messageButton.TranslatePoint(new Point(messageButton.Bounds.Width / 2, messageButton.Bounds.Height / 2), window)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() => messageButton.Focus(NavigationMethod.Tab));
        await Until(() => window.InputHitTest(MenuCenter()) is Visual hit &&
                         (hit == messageButton || hit.FindAncestorOfType<Button>() == messageButton), "Message actions are outside the clickable viewport");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var pointer = new Pointer(42, PointerType.Mouse, true);
            var position = MenuCenter();
            messageButton.RaiseEvent(new PointerPressedEventArgs(messageButton, pointer, window, position, 1,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            messageButton.RaiseEvent(new PointerReleasedEventArgs(messageButton, pointer, window, position, 2,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Expect(shell.ActiveMessageMenu?.IsOpen == true, "Message button press was swallowed by the log handler");
        });
        var menu = shell.ActiveMessageMenu!;
        var actions = (ReactionPicker)menu.Items.OfType<MenuItem>().First().Header!;
        await Until(() => actions.Children.OfType<Button>().Count(button => button.IsVisible) == ReactionCatalog.Main.Count + 1,
            "Message menu opened with the entire reaction palette");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var toggle = actions.Children.OfType<Button>().Single(button => button.Tag == null);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Expect(menu.IsOpen && actions.Children.OfType<Button>().Count(button => button.IsVisible) ==
                ReactionCatalog.Main.Count + ReactionCatalog.More.Count + 1, "More reactions did not expand in the open menu");
            menu.Close();
        });
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedMessage = vm.Messages.First(m => !m.IsDeleted));
        await OnUiAsync(() => vm.TogglePinCommand.ExecuteAsync(null));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.ToggleLocalPinCommand.Execute(null);
            Expect(vm.PinList.Count(p => p.MessageId == vm.SelectedMessage!.Id) == 2, "Shared and local pin scopes were collapsed");
            vm.ToggleLocalPinCommand.Execute(null);
            Expect(vm.PinList.Any(p => !p.Local && p.MessageId == vm.SelectedMessage!.Id), "Removing local pin removed shared pin");
            vm.SelectedMessage = vm.Messages.Last(m => !m.IsDeleted);
            vm.ToggleLocalPinCommand.Execute(null);
        });
        var serverPins = await vm.ThemeApi.GetPins(vm.SelectedChat!.Id);
        Expect(serverPins.Count == 1, "Local pin was sent to the server");
        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowPinListCommand.Execute(null));
        Button? pinActions = null;
        await Until(() =>
        {
            pinActions = shell.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.IsEffectivelyVisible && button.DataContext is PinListItem { Local: false } &&
                AutomationProperties.GetName(button) == "Действия закрепа");
            return pinActions != null;
        }, "Shared pin action is missing");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            pinActions!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pinMenu = pinActions.ContextMenu!;
            Expect(pinMenu.IsOpen, "Pin actions menu did not open");
            pinMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Открепить для всех"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            pinMenu.Close();
        });
        var windows = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        await Until(() => windows.Windows.Any(dialog => dialog.Title == "Открепить сообщение для всех?"),
            "Shared pin was removed without confirmation");
        await Dispatcher.UIThread.InvokeAsync(() => windows.Windows.Single(dialog => dialog.Title == "Открепить сообщение для всех?")
            .GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Отмена"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Expect((await vm.ThemeApi.GetPins(vm.SelectedChat!.Id)).Count == 1, "Cancelling shared unpin removed the server pin");

        foreach (var dark in new[] { false, true })
        foreach (var width in new[] { 1180, 360 })
        {
            var suffix = (dark ? "dark" : "light") + "-" + width;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ThemeSettings.Apply(ThemeSettings.Presets[dark ? 1 : 0]);
                Resize(window, width); window.Height = width == 360 ? 600 : 780;
                vm.ShowPinListCommand.Execute(null);
            });
            await Task.Delay(160);
            await Shot(window, "mvp-pins-" + suffix);
            await OnUiAsync(() => vm.OpenPinFromListCommand.ExecuteAsync(vm.PinList.First()));
            Expect(!vm.ShowingPinnedMessages, "Pin did not return to conversation");
            await Dispatcher.UIThread.InvokeAsync(() => log.FindDescendantOfType<ScrollViewer>()!.ScrollToEnd());
            await Until(() => AtEnd(log), "Message history did not scroll to the latest message");
            foreach (var (name, text) in new[] { ("empty", ""), ("text", "Привет! Встретимся завтра?"), ("multiline", "Первая строка\nВторая строка\nТретья строка"),
                ("long", new string('я', 3960)) })
            {
                await Dispatcher.UIThread.InvokeAsync(() => { vm.Draft = text; input.Focus(); });
                await Until(() => AtEnd(log), $"History lost the latest message after resizing the composer at {suffix}");
                await Task.Delay(100);
                await Shot(window, $"mvp-composer-{name}-{suffix}");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var send = shell.FindControl<Button>("SendButton")!;
                    var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
                    var watermark = input.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "PART_Watermark");
                    var watermarkOrigin = watermark.TranslatePoint(new Point(), input)!.Value;
                    var textOrigin = presenter.TranslatePoint(new Point(), input)!.Value;
                    Expect(Math.Abs(watermarkOrigin.X - textOrigin.X) <= 1, "Watermark and text start at different X positions");
                    Expect(AtEnd(log), "Part of the latest message was cut off after resizing the composer");
                    if (name == "empty")
                    {
                        var labelCenter = watermark.TranslatePoint(new Point(0, watermark.Bounds.Height / 2), shell)!.Value.Y;
                        var buttonCenter = send.TranslatePoint(new Point(0, send.Bounds.Height / 2), shell)!.Value.Y;
                        // Fractional display scales can represent an exact 2px gap as 2.0000000000001.
                        Expect(Math.Abs(labelCenter - buttonCenter) <= 2 + 1e-6, $"Composer is vertically misaligned: {labelCenter} vs {buttonCenter}");
                    }
                    Expect(send.IsEnabled == vm.CanSendDraft, "Send button disagrees with draft validity");
                });
                var composer = await Dispatcher.UIThread.InvokeAsync(() => input.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("composer")));
                await Shot(composer, $"mvp-field-{name}-{suffix}");
            }
            await Dispatcher.UIThread.InvokeAsync(() => vm.Draft = "");
            var settings = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.NavigateWorkspace(WorkspacePage.Settings);
                return (SettingsPage)shell.FindControl<ContentControl>("WorkspaceContent")!.Content!;
            });
            await Task.Delay(150);
            await Shot(window, "mvp-settings-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var react = settings.FindControl<Expander>("ReactionChoices")!;
                Expect(!react.IsExpanded, "Choosing a reaction expands all options on entry");
                react.IsExpanded = true;
                settings.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            });
            await Task.Delay(150);
            await Shot(window, "mvp-settings-bottom-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(vm.BackWorkspace);
            await OnUiAsync(() => vm.ShowProfileCommand.ExecuteAsync(null));
            await Task.Delay(100);
            await Shot(window, "mvp-account-" + suffix);
            var profile = await Dispatcher.UIThread.InvokeAsync(() => shell.GetVisualDescendants().OfType<Views.Dialogs.ProfileEditor>().Single());
            var originalName = vm.DisplayName;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                profile.FindControl<Button>("EditProfile")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                vm.Bio = "Разрабатываю мессенджер. Проверяю светлую и тёмную темы.";
            });
            await Task.Delay(100);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var control in profile.GetVisualDescendants().Where(c => c.IsEffectivelyVisible).OfType<Control>())
                {
                    if (control is not TextBox && control is not Button) continue;
                    var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), profile)!.Value.X;
                    Expect(right <= profile.Bounds.Width + 1,
                        $"Profile control overflows: {control.Name ?? control.GetType().Name}, right={right}, width={profile.Bounds.Width}");
                }
            });
            await Shot(window, "mvp-profile-edit-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.DisplayName = "Несохранённое имя";
                profile.FindControl<Button>("CancelEdit")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Expect(vm.DisplayName == originalName && !profile.FindControl<StackPanel>("EditPanel")!.IsVisible, "Cancel did not restore profile and account screen");
                vm.BackWorkspace();
            });
        }

        await Dispatcher.UIThread.InvokeAsync(() => { Resize(window, 1180); window.Height = 780; ThemeSettings.Apply(ThemeSettings.Presets[0]); });
        var preferences = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.NavigateWorkspace(WorkspacePage.Settings);
            return (SettingsPage)shell.FindControl<ContentControl>("WorkspaceContent")!.Content!;
        });
        var oldPrivacy = vm.AllowTagSearch;
        await Dispatcher.UIThread.InvokeAsync(() => preferences.FindControl<ToggleSwitch>("TagSearch")!.IsChecked = !oldPrivacy);
        await Until(() => preferences.FindControl<ToggleSwitch>("TagSearch")!.IsEnabled, "Privacy update did not complete");
        Expect((await vm.ThemeApi.GetMe()).AllowTagSearch == !oldPrivacy, "Privacy switch did not reach backend");
        await Dispatcher.UIThread.InvokeAsync(() => preferences.FindControl<ToggleSwitch>("TagSearch")!.IsChecked = oldPrivacy);
        await Until(() => preferences.FindControl<ToggleSwitch>("TagSearch")!.IsEnabled, "Privacy restoration did not complete");
        await Dispatcher.UIThread.InvokeAsync(vm.BackWorkspace);

        foreach (var pin in vm.PinList.ToArray()) await OnUiAsync(() => vm.RemovePinFromListCommand.ExecuteAsync(pin));
        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowPinListCommand.Execute(null));
        Expect(vm.PinListEmpty, "Removed pins left stale rows");
        await Task.Delay(100);
        await Shot(window, "mvp-pins-empty");
        await Dispatcher.UIThread.InvokeAsync(() => { vm.ClosePinListCommand.Execute(null); AppPreferences.Update(_ => new()); });
        await AuditConversations(window, vm);
        await AuditWallpapers(window, vm);
        Console.WriteLine("PASS MVP: composer alignment at 360/1180 in light/dark, send keys, double-click reply/reaction, privacy API, local/shared pins and empty state");
    }

    static bool AtEnd(ListBox messageLog)
    {
        var scroll = messageLog.FindDescendantOfType<ScrollViewer>();
        return scroll != null && scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y <= 1;
    }

    static async Task AuditComposerKeys(TextBox input, ShellViewModel vm)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.Draft = "";
            AppPreferences.Update(p => p with { SendMode = SendMode.Enter });
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Expect(!vm.Busy, "Empty draft was submitted");
            vm.Draft = "Проверка переноса";
            input.CaretIndex = vm.Draft.Length;
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift });
            Expect(!vm.Busy && vm.Draft.Contains('\n'), "Shift+Enter sent instead of inserting a newline");
            var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
            presenter.PreeditText = "未確定";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Expect(!vm.Busy, "IME composition was submitted before confirmation");
            presenter.PreeditText = null;
            vm.Draft = "Проверка отправки клавишей Enter";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        });
        await Until(() => !vm.Busy && vm.Draft == "", "Enter did not send in Enter mode");
        var sent = vm.Messages.Single(m => m.Text == "Проверка отправки клавишей Enter");
        await vm.ThemeApi.DeleteMessage(sent.Id, false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AppPreferences.Update(p => p with { SendMode = SendMode.CtrlEnter });
            vm.Draft = "Проверка Ctrl+Enter";
            input.CaretIndex = vm.Draft.Length;
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Expect(!vm.Busy && vm.Draft.Contains('\n'), "Enter sent in Ctrl+Enter mode");
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
        });
        await Until(() => !vm.Busy && vm.Draft == "", "Ctrl+Enter did not send");
        sent = vm.Messages.Single(m => m.Text == "Проверка Ctrl+Enter");
        await vm.ThemeApi.DeleteMessage(sent.Id, false);
    }
}
