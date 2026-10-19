using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;

namespace MessengerDesktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (!OperatingSystem.IsMacOS()) return;

        // Content runs under the title bar; the native traffic lights sit on the sidebar.
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = 52;
        Resources["ChromeLeading"] = new Thickness(84, 0, 10, 0);
    }

    public static void DragFrom(Visual source, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(source).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(source) is Window window)
            window.BeginMoveDrag(e);
    }
}
