using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Appearance;
using MessengerDesktop.Settings;
using MessengerDesktop.Views;

namespace MessengerDesktop;

public static partial class Preview
{
    static async Task AuditWorkspaceNavigation(Window window, ShellView shell, ShellViewModel vm, MessengerClient api)
    {
        var chat = vm.SelectedChat!;
        for (var i = 0; i < 14; i++)
            await api.SendMessage(chat.Id, $"Проверка возврата к сообщению {i + 1}.");
        var log = shell.FindControl<ListBox>("Log")!;
        await Until(() => vm.Messages.Count >= 15 && log.FindDescendantOfType<ScrollViewer>() is { } scroll &&
            scroll.Extent.Height - scroll.Viewport.Height >= 120, "Navigation test did not load a scrollable conversation");

        var draft = shell.FindControl<TextBox>("ComposerInput")!;
        var workspace = shell.FindControl<Border>("WorkspaceSurface")!;
        var frame = shell.FindControl<Border>("WorkspaceFrame")!;
        var bay = shell.FindControl<Border>("ScreenBay")!;
        var wallpaper = shell.FindControl<WallpaperLayer>("ConversationWallpaper")!;
        var scrim = shell.FindControl<Border>("OverlayScrim")!;
        var sidebar = shell.FindControl<Border>("ChatSidebar")!;
        var back = shell.FindControl<Button>("WorkspaceBack")!;
        var quote = vm.Messages.First();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.SelectedMessage = quote;
            vm.ReplyCommand.Execute(null);
            vm.Draft = "Черновик остаётся в переписке";
            draft.Focus();
            draft.CaretIndex = 9;
            draft.SelectionStart = 3;
            draft.SelectionEnd = 9;
        });
        await Until(() => shell.GetVisualDescendants().OfType<Border>().Any(border =>
            border.Classes.Contains("composeContext") && border.IsEffectivelyVisible && border.Bounds.Height > 0),
            "Reply composer did not complete layout");
        await Dispatcher.UIThread.InvokeAsync(() => log.FindDescendantOfType<ScrollViewer>()!.Offset = new Vector(0, 90),
            DispatcherPriority.ContextIdle);
        await Until(() => Math.Abs(log.FindDescendantOfType<ScrollViewer>()!.Offset.Y - 90) < 1, "Could not set the test scroll position");
        await Dispatcher.UIThread.InvokeAsync(() =>
            shell.FindControl<Button>("OpenSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Until(() => workspace.IsEffectivelyVisible && sidebar.IsEffectivelyVisible && !scrim.IsEffectivelyVisible &&
            !log.IsEffectivelyVisible && window.OwnedWindows.Count == 0, "Settings opened over the chat");
        await Until(() => wallpaper.IsEffectivelyVisible &&
            frame.TranslatePoint(default, bay) is { X: >= 16 } &&
            frame.TranslatePoint(default, shell) is { } origin &&
            Math.Abs(origin.X * 2 + frame.Bounds.Width - shell.Bounds.Width) <= 2 &&
            frame.Bounds.Width <= 656 && back.TranslatePoint(default, frame) is { X: >= 0 },
            "Wide panel does not contain its header or leave visible wallpaper on both sides");
        var settings = (SettingsPage)shell.FindControl<ContentControl>("WorkspaceContent")!.Content!;
        await Shot(window, "workspace-settings-wide");
        await Dispatcher.UIThread.InvokeAsync(() => settings.FindControl<Button>("Appearance")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Until(() => shell.FindControl<ContentControl>("WorkspaceContent")!.Content is AppearanceEditor &&
            window.OwnedWindows.Count == 0, "Appearance opened a child window");
        await Dispatcher.UIThread.InvokeAsync(() => back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Until(() => settings.IsEffectivelyVisible, "Back lost the settings page");
        var originalWallpaper = WallpaperSettings.Current;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            WallpaperSettings.Apply(new WallpaperSettings("topography"));
            Resize(window, 1600);
        });
        await Until(() => shell.Bounds.Width >= 1500 && frame.Bounds.Width <= 656 &&
            frame.TranslatePoint(default, shell) is { } wideOrigin &&
            Math.Abs(wideOrigin.X * 2 + frame.Bounds.Width - shell.Bounds.Width) <= 2 && wallpaper.IsEffectivelyVisible,
            "A maximized panel did not keep its bounded width over the wallpaper");
        await Shot(window, "workspace-settings-wallpaper-wide");
        await OnUiAsync(() => vm.ShowProfileCommand.ExecuteAsync(null));
        await Shot(window, "workspace-account-wallpaper-wide");
        await Dispatcher.UIThread.InvokeAsync(() => back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Until(() => settings.IsEffectivelyVisible, "Back from account did not return to the parent settings page");
        await Dispatcher.UIThread.InvokeAsync(() => Resize(window, 360));
        await Until(() => !sidebar.IsEffectivelyVisible && Math.Abs(frame.Bounds.Width - bay.Bounds.Width) < 2 &&
            !wallpaper.IsEffectivelyVisible, "Compact panel did not fill the available screen");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Resize(window, 1180);
            WallpaperSettings.Apply(originalWallpaper);
        });
        await Until(() => sidebar.IsEffectivelyVisible && frame.Bounds.Width <= 656, "Wide panel did not return after resize");
        await Dispatcher.UIThread.InvokeAsync(() => back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Until(() => log.IsEffectivelyVisible && !workspace.IsEffectivelyVisible &&
            Math.Abs(log.FindDescendantOfType<ScrollViewer>()!.Offset.Y - 90) < 2 &&
            vm.Draft == "Черновик остаётся в переписке" && vm.ReplyTo?.Id == quote.Id &&
            draft.SelectionStart == 3 && draft.SelectionEnd == 9,
            "Back did not restore scroll, draft, reply or selection");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.CancelComposeCommand.Execute(null);
            vm.Draft = "";
        });
        Console.WriteLine("PASS workspace navigation: centered panels, wallpaper, compact layout and Back restore scroll, draft, selection and reply");
    }
}
