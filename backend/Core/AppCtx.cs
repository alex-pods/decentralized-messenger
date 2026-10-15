using MessengerBackend.Hubs;
using Microsoft.AspNetCore.HttpOverrides;

namespace MessengerBackend.Core;

/// <summary>Составной корень: общие зависимости для всех модулей. Никакой бизнес-логики.</summary>
public sealed class AppCtx
{
    public required Hub Hub { get; init; }
    public required string FilesRoot { get; init; }
    public required ILoggerFactory Logging { get; init; }
    public IHostApplicationLifetime? Lifetime { get; private set; }

    public static string FilesRootFromEnv() =>
        Environment.GetEnvironmentVariable("MESSENGER_FILES") ?? "/var/lib/messenger/files";

    public static void Configure(IWebHostEnvironment env, IConfiguration config) { /* зарезервировано */ }

    public static AppCtx Resolve(WebApplication app)
    {
        var root = FilesRootFromEnv();
        foreach (var sub in new[] { "attachments", "avatars" })
            Directory.CreateDirectory(Path.Combine(root, sub));
        var ctx = new AppCtx
        {
            Hub = app.Services.GetRequiredService<Hub>(),
            FilesRoot = root,
            Logging = app.Services.GetRequiredService<ILoggerFactory>()
        };
        ctx.Lifetime = app.Lifetime;
        return ctx;
    }

    /// <summary>Общая middleware: pathbase, swagger, cors, websockets, graceful shutdown сокетов.</summary>
    public void Install(WebApplication app)
    {
        var pathBase = Environment.GetEnvironmentVariable("MESSENGER_PATH_BASE") ?? "/messenger";
        if (string.IsNullOrWhiteSpace(pathBase) || pathBase[0] != '/') pathBase = "/messenger";

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
        app.UsePathBase(pathBase);
        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseCors();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // graceful shutdown: закрыть все WS при остановке приложения
        Lifetime?.ApplicationStopping.Register(() =>
            Hub.CloseAll(1001, "server shutting down").GetAwaiter().GetResult());
    }
}
