using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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
        var shell = await Dispatcher.UIThread.InvokeAsync(() => window.GetVisualDescendants().OfType<Views.ShellView>().Single());
        var scroll = shell.FindControl<ScrollViewer>("OverlayScroll")!;
        var header = shell.FindControl<Grid>("OverlayHeader")!;
        var submit = shell.FindControl<Button>("SubmitGroup")!;
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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var content = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
                    var bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                        .First(item => item.Orientation == Avalonia.Layout.Orientation.Vertical);
                    if (!bar.IsEffectivelyVisible) return;
                    var edge = content.TranslatePoint(new Point(content.Bounds.Width, 0), scroll)!.Value.X;
                    var barStart = bar.TranslatePoint(new Point(), scroll)!.Value.X;
                    Expect(edge <= barStart + .5 && bar.Bounds.Width <= 13.5, $"Scrollbar does not fit the {screen} dialog gutter");
                    var track = bar.GetVisualDescendants().OfType<Track>().Single();
                    track.IncreaseButton!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    Expect(scroll.Offset.Y > 0, $"Scrollbar track could not scroll the {screen} dialog");
                    scroll.ScrollToHome();
                });
                await Shot(window, $"audit-{screen}-{suffix}-360");
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.Height = window.MinHeight;
                vm.Overlay = "group";
            });
            await Until(() => submit.IsEffectivelyVisible && window.ClientSize.Height <= window.MinHeight + 1,
                "Group footer is unavailable at minimum height");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var scrollerBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)!.Value.Y;
                var buttonTop = submit.TranslatePoint(new Point(), window)!.Value.Y;
                var titleBottom = header.TranslatePoint(new Point(0, header.Bounds.Height), window)!.Value.Y;
                var scrollerTop = scroll.TranslatePoint(new Point(), window)!.Value.Y;
                Expect(titleBottom <= scrollerTop && scrollerBottom <= buttonTop &&
                    buttonTop + submit.Bounds.Height <= window.ClientSize.Height, "Group header, body and footer overlap");
            });
            await Shot(window, $"audit-group-{suffix}-short");
            await Dispatcher.UIThread.InvokeAsync(() => window.Height = 600);
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
