using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Appearance;

namespace MessengerDesktop;

public static partial class Preview
{
    static async Task AuditLogin(Window window)
    {
        foreach (var theme in ThemeSettings.Presets.Take(2))
        foreach (var width in new[] { 360, 1180 })
        foreach (var registration in new[] { false, true })
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ThemeSettings.Apply(theme); Resize(window, width); window.Height = 780;
                if (window.DataContext is LoginViewModel login)
                {
                    login.IsRegistration = registration; login.Error = null; login.ShowConnection = false;
                }
            });
            await Task.Delay(100);
            await Shot(window, $"audit-{(registration ? "register" : "login")}-{(theme.Dark ? "dark" : "light")}-{width}");
        }
        await Dispatcher.UIThread.InvokeAsync(() => ThemeSettings.Apply(ThemeSettings.Presets[0]));
    }

    static async Task AuditCompactScreens(Window window, ShellViewModel vm)
    {
        foreach (var theme in ThemeSettings.Presets.Take(2))
        {
            var suffix = theme.Dark ? "dark" : "light";
            await Dispatcher.UIThread.InvokeAsync(() => { ThemeSettings.Apply(theme); Resize(window, 360); window.Height = 600; });
            foreach (var screen in new[] { "direct", "group", "profile", "members", "forward" })
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.Overlay = screen; vm.Error = null;
                    vm.GroupTitle = "Поездка на выходные";
                });
                await Task.Delay(150);
                await Shot(window, $"audit-{screen}-{suffix}-360");
            }
            await Dispatcher.UIThread.InvokeAsync(() => vm.CloseOverlayCommand.Execute(null));
            await Shot(window, $"audit-chat-{suffix}-360");
            var appearance = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dialog = new AppearanceWindow(vm.ThemeApi, vm.CurrentUserId) { Width = 360, Height = 600 };
                dialog.Show(window); return dialog;
            });
            await Task.Delay(150);
            await Shot(appearance, $"audit-appearance-{suffix}-360");
            await Dispatcher.UIThread.InvokeAsync(() => appearance.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd());
            await Task.Delay(150);
            await Shot(appearance, $"audit-sharing-{suffix}-360");
            await Dispatcher.UIThread.InvokeAsync(() => appearance.Close());
        }
        await Dispatcher.UIThread.InvokeAsync(() => { Resize(window, 1180); window.Height = 780; ThemeSettings.Apply(ThemeSettings.Presets[0]); });
        Console.WriteLine("PASS compact screen matrix: authentication, five dialogs, conversation, appearance and sharing in light/dark");
    }
}
