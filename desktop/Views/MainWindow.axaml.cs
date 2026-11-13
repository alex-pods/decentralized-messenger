using System.Diagnostics;
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
        if (ReduceMotion()) Classes.Add("reduce-motion");
        if (!OperatingSystem.IsMacOS()) return;

        // Как в тулбаре macOS: светофоры по центру верхней строки, корпус вплотную к краю окна.
        // Винты в этом углу убираем — угол занят системными кнопками.
        const double titleBar = 52;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome
            | ExtendClientAreaChromeHints.OSXThickTitleBar;
        ExtendClientAreaTitleBarHeightHint = titleBar;
        ChassisFrame.Margin = new Thickness(0);
        Housing.Classes.Add("flush");
        ScrewLayer.IsVisible = false;
        Resources["HeaderInset"] = new Thickness(70, 0, 0, 0);
        Resources["LoginMargin"] = new Thickness(28, 60, 28, 22);
    }

    static bool ReduceMotion()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "/usr/bin/defaults",
                ArgumentList = { "read", "com.apple.universalaccess", "reduceMotion" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start);
            if (process == null) return false;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(800);
            return output is "1" or "true";
        }
        catch
        {
            return false;
        }
    }

    public static void DragFrom(Visual source, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(source).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(source) is Window window)
            window.BeginMoveDrag(e);
    }
}
