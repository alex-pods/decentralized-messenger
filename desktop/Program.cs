using Avalonia;

namespace MessengerDesktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--check")
            return SelfCheck.Run(args.Length > 1 ? args[1] : "http://127.0.0.1:5080/messenger");
        if (args.Length > 2 && args[0] == "--preview")
            (Preview.Server, Preview.OutDir) = (args[1], args[2]);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
