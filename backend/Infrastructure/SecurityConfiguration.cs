using Microsoft.AspNetCore.DataProtection;

namespace MessengerBackend.Infrastructure;

public static class SecurityConfiguration
{
    public static string[] AllowedOrigins => (Environment.GetEnvironmentVariable("MESSENGER_ALLOWED_ORIGINS") ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static void AddStorageSecurity(this IServiceCollection services)
    {
        var keyPath = Environment.GetEnvironmentVariable("MESSENGER_KEYS");
        if (string.IsNullOrWhiteSpace(keyPath))
            keyPath = Path.Combine(PathUtilities.DirOf(Db.Path), "keys");
        Directory.CreateDirectory(keyPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        services.AddDataProtection().SetApplicationName("MessengerBackend.Storage.v1")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
    }
}
