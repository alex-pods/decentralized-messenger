using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Views;

namespace MessengerDesktop;

/// <summary>
/// Снимки экранов для проверки дизайна: наполняет сервер демо-перепиской и сохраняет PNG.
/// Запуск: dotnet run --project desktop -- --preview http://127.0.0.1:5099/messenger /tmp/shots
/// </summary>
public static class Preview
{
    public static string? Server { get; set; }
    public static string OutDir { get; set; } = ".";

    public static async Task Run(Window window, Navigator nav)
    {
        var (api, me) = await Seed(Server!);
        await Task.Delay(1500);
        await Shot(window, "login");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (window.DataContext is LoginViewModel login) login.SwitchModeCommand.Execute(null);
        });
        await Task.Delay(100);
        await Shot(window, "register");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (window.DataContext is LoginViewModel login)
            {
                login.SwitchModeCommand.Execute(null);
                login.ShowConnection = true;
                login.Error = "Не удалось подключиться к серверу. Проверьте адрес.";
            }
        });
        await Task.Delay(100);
        await Shot(window, "login-error");

        var vm = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var shell = new ShellViewModel(nav, api, me);
            nav.Go(shell);
            return shell;
        });
        await Task.Delay(2500);
        await Shot(window, "empty");

        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedChat = vm.Chats.First(c => c.Title.StartsWith("Максим")));
        await Task.Delay(2000);
        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedMessage = vm.Messages.Last(m => m.Mine));
        await Task.Delay(800);
        await Shot(window, "chat");
        foreach (var theme in Appearance.ThemeSettings.Presets.Skip(1))
        {
            await Dispatcher.UIThread.InvokeAsync(() => Appearance.ThemeSettings.Apply(theme));
            await Task.Delay(200);
            await Shot(window, "chat-" + Array.IndexOf(Appearance.ThemeSettings.Presets, theme));
        }
        await Dispatcher.UIThread.InvokeAsync(() => Appearance.ThemeSettings.Apply(Appearance.ThemeSettings.Presets[0]));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.SelectedMessage = null;
            vm.SelectedChat = vm.Chats.First(c => c.IsGroup);
        });
        await Task.Delay(2000);
        await Shot(window, "group");
        await Dispatcher.UIThread.InvokeAsync(() => window.Width = 520);
        await Task.Delay(300);
        await Shot(window, "narrow");
        await Dispatcher.UIThread.InvokeAsync(() => window.Width = 1180);

        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowMembersCommand.Execute(null));
        await Task.Delay(1500);
        await Shot(window, "members");

        await Dispatcher.UIThread.InvokeAsync(() => vm.CloseOverlayCommand.Execute(null));
        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowProfileCommand.Execute(null));
        await Task.Delay(1200);
        await Shot(window, "profile");

        await AuditScreens(window, vm);
        Dispatcher.UIThread.Post(() => window.Close());
    }

    static async Task AuditScreens(Window window, ShellViewModel vm)
    {
        var shell = await Dispatcher.UIThread.InvokeAsync(() => window.GetVisualDescendants().OfType<ShellView>().Single());
        foreach (var preset in Appearance.ThemeSettings.Presets)
        {
            if (Appearance.ThemeSettings.Contrast(Avalonia.Media.Color.Parse(preset.Text), Avalonia.Media.Color.Parse(preset.Surface)) < 4.5)
                throw new Exception("Preset text contrast is insufficient");
        }
        var start = new DateTimeOffset(DateTime.Today.AddHours(12));
        var grouped = new[]
        {
            new MessageItem { SenderId = 1, SentAt = start },
            new MessageItem { SenderId = 1, SentAt = start.AddMinutes(1) },
            new MessageItem { SenderId = 2, SentAt = start.AddMinutes(2) },
            new MessageItem { SenderId = 2, SentAt = start.AddDays(1) }
        };
        ShellViewModel.GroupMessages(grouped);
        if (!grouped[0].StartsDay || grouped[1].StartsRun || !grouped[2].StartsRun || !grouped[3].StartsDay)
            throw new Exception("Message author/day grouping failed");
        var originalChat = vm.SelectedChat!;
        var otherChat = vm.Chats.First(c => c.Id != originalChat.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.CloseOverlayCommand.Execute(null);
            vm.Draft = "Черновик первой беседы";
            vm.SelectedChat = otherChat;
            if (vm.Draft != "") throw new Exception("Draft leaked to another conversation");
            vm.Draft = "Черновик второй беседы";
            vm.SelectedChat = originalChat;
            if (vm.Draft != "Черновик первой беседы") throw new Exception("Draft was not restored");
        });
        await Task.Delay(700);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.SelectedMessage = vm.Messages.First(m => m.Mine);
            vm.BeginEditCommand.Execute(null);
            vm.CancelComposeCommand.Execute(null);
            if (vm.Draft != "Черновик первой беседы") throw new Exception("Cancel edit lost the draft");
            vm.Draft = "";
        });
        foreach (var theme in Appearance.ThemeSettings.Presets.Take(2))
        {
            var suffix = theme.Dark ? "dark" : "light";
            await Dispatcher.UIThread.InvokeAsync(() => Appearance.ThemeSettings.Apply(theme));
            foreach (var screen in new[] { "direct", "group", "profile", "members", "forward" })
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.Error = null;
                    vm.Overlay = screen;
                });
                await Task.Delay(120);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (shell.FindControl<Grid>("ShellGrid")!.IsEnabled) throw new Exception("Modal leaves background enabled");
                });
                await Shot(window, screen + "-" + suffix);
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.CloseOverlayCommand.Execute(null);
                vm.SelectedMessage = vm.Messages.First();
                vm.ReplyCommand.Execute(null);
            });
            await Task.Delay(120);
            await Shot(window, "reply-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() => vm.CancelComposeCommand.Execute(null));
            var appearance = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dialog = new Appearance.AppearanceWindow();
                dialog.Show(window);
                return dialog;
            });
            await Task.Delay(180);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!appearance.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "✓ " + theme.Name))
                    throw new Exception("Current theme is not marked in appearance settings");
            });
            await Shot(appearance, "appearance-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                appearance.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
            });
            await Task.Delay(120);
            await Shot(appearance, "appearance-custom-" + suffix);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var preset = appearance.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Tag, "Бумага"));
                preset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                if (Appearance.ThemeSettings.Current.Name != "Бумага") throw new Exception("Preset did not apply");
                var path = Path.Combine(Path.GetTempPath(), "messenger-theme-" + Guid.NewGuid().ToString("N"), "appearance.json");
                try
                {
                    Appearance.ThemeSettings.SaveTo(path);
                    if (Appearance.ThemeSettings.ReadFrom(path) != Appearance.ThemeSettings.Current)
                        throw new Exception("Appearance persistence failed");
                }
                finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
                appearance.Close();
                if (Appearance.ThemeSettings.Current != theme) throw new Exception("Cancel failed to restore theme");
            });
            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Appearance.ThemeSettings.Current != theme) throw new Exception("Closed appearance window changed the theme asynchronously");
            });
        }

        var menu = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.CloseOverlayCommand.Execute(null);
            var message = vm.Messages.First(m => m.Mine);
            shell.OpenMessageMenu(shell.FindControl<ListBox>("Log")!, message);
            return shell.ActiveMessageMenu!;
        });
        await Task.Delay(200);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Редактировать")))
                throw new Exception("Own message edit action is missing");
        });
        await Shot(menu, "message-menu");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            menu.Close();
            vm.CloseOverlayCommand.Execute(null);
            var selected = vm.SelectedChat;
            shell.FindControl<TextBox>("ChatSearch")!.Text = "not-a-chat";
            if (vm.SelectedChat != selected) throw new Exception("Filtering changed the active chat");

        });
        await Task.Delay(150);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!shell.FindControl<TextBlock>("ChatEmpty")!.IsVisible) throw new Exception("Missing empty-search state");
        });
        await Shot(window, "search-empty");
        await Dispatcher.UIThread.InvokeAsync(() => shell.FindControl<TextBox>("ChatSearch")!.Text = "");
        foreach (var width in new[] { 360, 520 })
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.Width = width;
                window.Height = 600;
                vm.Overlay = "profile";
            });
            await Task.Delay(150);
            await Shot(window, "profile-" + width);
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.Width = 1180;
            window.Height = 780;
            vm.CloseOverlayCommand.Execute(null);
            Appearance.ThemeSettings.Apply(Appearance.ThemeSettings.Presets[0]);
        });
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.ShowGroupCommand.Execute(null);
            vm.GroupTitle = "Поездка на выходные";
            vm.GroupDraft.Add(new UserCardDto { Id = -101, DisplayName = "Очень длинное имя участника группы для проверки переноса", Tag = "preview" });
            vm.SearchResults.Add(new UserCardDto { Id = -102, DisplayName = "Мария Александрова", Tag = "maria" });
        });
        await Task.Delay(150);
        await Shot(window, "group-filled");
        await Dispatcher.UIThread.InvokeAsync(() => window.Width = 360);
        await Task.Delay(150);
        await Shot(window, "group-filled-360");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.RemoveDraftCommand.Execute(vm.GroupDraft.Single());
            if (vm.GroupDraft.Count != 0) throw new Exception("Removing a selected group member failed");
            vm.SearchResults.Clear();
            vm.CloseOverlayCommand.Execute(null);
            window.Width = 1180;
            shell.OpenMessageMenu(shell.FindControl<ListBox>("Log")!, vm.Messages.First(m => !m.Mine));
            if (shell.ActiveMessageMenu!.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Редактировать")))
                throw new Exception("Foreign message exposes edit action");
            shell.ActiveMessageMenu.Close();
        });
        Task<bool>? confirmation = null;
        await Dispatcher.UIThread.InvokeAsync(() => { confirmation = Views.Dialogs.ConfirmAction.Show(shell, "Удалить сообщение у всех?", "Сообщение исчезнет у всех участников. Отменить это действие нельзя.", "Удалить"); });
        await Task.Delay(150);
        var confirmWindow = await Dispatcher.UIThread.InvokeAsync(() =>
            ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows.Single(w => w.Title == "Удалить сообщение у всех?"));
        await Shot(confirmWindow, "confirm-delete");
        await Dispatcher.UIThread.InvokeAsync(() => confirmWindow.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Отмена")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
        if (await confirmation!) throw new Exception("Cancel confirmed a destructive action");
        Console.WriteLine("PASS UI screen matrix and filter selection retention");
    }

    static Task Shot(Control window, string name) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        Directory.CreateDirectory(OutDir);
        bitmap.Save(Path.Combine(OutDir, name + ".png"));
    }).GetTask();

    static async Task<(MessengerClient, ProfileDto)> Seed(string server)
    {
        var suffix = Guid.NewGuid().ToString("N")[..4];
        async Task<(MessengerClient api, ProfileDto me)> User(string tag, string name)
        {
            var api = new MessengerClient(server);
            await api.Register(tag + suffix, "password1");
            await api.Login(tag + suffix, "password1");
            await api.UpdateProfile(name, null);
            return (api, await api.GetMe());
        }

        var alice = await User("alice", "Алиса Королёва");
        var max = await User("max", "Максим Орлов");
        var eva = await User("eva", "Ева Лебедева");
        var lev = await User("lev", "Лев Тимофеев");
        var mira = await User("mira", "Мира Соколова");

        var withLev = await alice.api.CreateDirect(lev.me.Id);
        await lev.api.SendMessage(withLev, "Скинешь ссылку на сервер?");
        var withMira = await alice.api.CreateDirect(mira.me.Id);
        await alice.api.SendMessage(withMira, "Спасибо за вчерашнее 🙌");

        var group = await alice.api.CreateGroup("Дизайн-команда", new[] { max.me.Id, eva.me.Id, mira.me.Id });
        await eva.api.SendMessage(group, "Ребята, я собрала новые макеты главного экрана");
        await max.api.SendMessage(group, "Посмотрел. Поиск стал заметнее, список читается лучше");
        await alice.api.SendMessage(group, "Давайте проверим на ноутбуке и с длинными именами");
        await mira.api.SendMessage(group, "Проверю сегодня вечером");
        await eva.api.SendMessage(group, "Тогда созвон в 19:00?");

        var withMax = await alice.api.CreateDirect(max.me.Id);
        await max.api.SendMessage(withMax, "Привет! Ты видела, что сервер уже поднят?");
        await alice.api.SendMessage(withMax, "Да! Только что зашла с десктопа, всё летает");
        await max.api.SendMessage(withMax, "Круто. А сообщения через вебсокет приходят сразу?");
        var q = await alice.api.SendMessage(withMax, "Мгновенно. Даже правки и удаление прилетают в реальном времени");
        await max.api.SendMessage(withMax, "Тогда зову всех наших 🚀", q);
        await alice.api.SendMessage(withMax, "Давай! Я уже сделала группу для команды");
        await max.api.SendMessage(withMax, "Супер, вечером напишу туда");

        foreach (var u in new[] { max, eva, lev, mira }) u.api.Dispose();
        return alice;
    }
}
