using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Appearance;

namespace MessengerDesktop;

public static partial class Preview
{
    public static bool SharingOnly { get; set; }

    static async Task AuditSharing(Window window)
    {
        using var api = new MessengerClient(Server!);
        var tag = "uitheme" + Guid.NewGuid().ToString("N")[..8];
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await api.Register(tag, password); await api.Login(tag, password);
        var me = await api.GetMe();
        var original = ThemeSettings.Current;
        var dialog = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var appearance = new AppearanceWindow(api, me.Id); appearance.Show(window); return appearance;
        });
        await Task.Delay(200);
        T Named<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        async Task Click(string title)
        {
            await Dispatcher.UIThread.InvokeAsync(() => dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, title))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            for (var i = 0; i < 160; i++)
            {
                if (await Dispatcher.UIThread.InvokeAsync(() => Named<StackPanel>("ThemeSharing").IsEnabled)) return;
                await Task.Delay(250);
            }
            throw new Exception("Theme action did not finish: " + title);
        }
        string? id = null;
        try
        {
            await Click("Опубликовать копию");
            var link = await Dispatcher.UIThread.InvokeAsync(() => Named<TextBox>("ThemeLink").Text!);
            var published = await ThemeExchange.Download(new Uri(link)); id = published.Id;
            await Dispatcher.UIThread.InvokeAsync(() => dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "Тёмная"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Click("Обновить опубликованную");
            var updated = await ThemeExchange.Download(new Uri(link));
            if (updated.Version != 2 || !updated.Theme.Dark) throw new Exception("UI update did not publish the changed theme");
            await Click("Открыть ссылку");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ThemeSettings.Current != updated.Theme) throw new Exception("UI link import did not apply theme preview");
                dialog.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            });
            await Task.Delay(150);
            await Shot(dialog, "sharing-live-published");
            await Dispatcher.UIThread.InvokeAsync(() => Named<TextBox>("ThemeLink").Text = api.ServerBase + "/health");
            await Task.Delay(100);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Обновить опубликованную")).IsVisible)
                    throw new Exception("Editing the link left update bound to a different publication");
            });
            await Click("Открыть ссылку");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ThemeSettings.Current != updated.Theme) throw new Exception("Invalid link changed active theme");
                if (Named<TextBlock>("ThemeExchangeStatus").Text == "Подождите…") throw new Exception("Invalid link left the UI busy");
                dialog.Close();
                if (ThemeSettings.Current != original) throw new Exception("Cancel did not restore original theme after importing");
            });
            Console.WriteLine("PASS sharing UI: publish, change preset, owner update, HTTPS import, invalid link, cancel");
        }
        finally { if (id != null) await api.Send(HttpMethod.Delete, "themes/" + id, null, default); }
        await Dispatcher.UIThread.InvokeAsync(() => window.Close());
    }
}
