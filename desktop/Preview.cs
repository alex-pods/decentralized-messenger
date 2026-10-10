using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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

        var vm = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var core = await DesktopBootstrap.OpenAsync(api, me);
            var shell = new ShellViewModel(nav, api, me, core);
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

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.SelectedMessage = null;
            vm.SelectedChat = vm.Chats.First(c => c.IsGroup);
        });
        await Task.Delay(2000);
        await Shot(window, "group");

        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowMembersCommand.Execute(null));
        await Task.Delay(1500);
        await Shot(window, "members");

        await Dispatcher.UIThread.InvokeAsync(() => vm.CloseOverlayCommand.Execute(null));
        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowSettingsCommand.Execute(null));
        await Task.Delay(1200);
        await Shot(window, "settings");

        Dispatcher.UIThread.Post(() => window.Close());
    }

    static Task Shot(Window window, string name) => Dispatcher.UIThread.InvokeAsync(() =>
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
        await max.api.SendMessage(group, "Огонь. Стекло с градиентом смотрится прям дорого");
        await alice.api.SendMessage(group, "Согласна! Давайте ещё анимацию появления сообщений добавим");
        await mira.api.SendMessage(group, "Уже в работе ✨");
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
