using Avalonia;

namespace MessengerDesktop;

internal static class Program
{
    internal static string? ServerOverride { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--server")
        {
            if (!Uri.TryCreate(args[1], UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            {
                Console.Error.WriteLine("Expected an HTTP(S) backend URL without credentials, query or fragment."); return 1;
            }
            ServerOverride = uri.AbsoluteUri.TrimEnd('/');
        }
        if (args.Length > 0 && args[0] == "--check")
            return SelfCheck.Run(args.Length > 1 ? args[1] : "http://127.0.0.1:5080/messenger");
        if (args.Length > 0 && args[0] == "--check-pins")
            return PinStripCheck.Run();
        if (args.Length > 1 && args[0] == "--check-themes")
            return ThemeExchangeCheck.Run(args[1]);
        if (args.Length > 2 && args[0] == "--preview")
            (Preview.Server, Preview.OutDir) = (args[1], args[2]);
        if (args.Length > 2 && args[0] == "--preview-sharing")
        {
            (Preview.Server, Preview.OutDir) = (args[1], args[2]);
            Preview.SharingOnly = true;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
