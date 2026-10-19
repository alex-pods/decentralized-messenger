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
        Environment.GetEnvironmentVariable("MESSENGER_FILES") ?? Path.Combine(PathUtilities.DirOf(Db.Path), "files");

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
        app.UseSwagger(o => o.PreSerializeFilters.Add((doc, request) =>
            doc.Servers = [new Microsoft.OpenApi.Models.OpenApiServer { Url = request.PathBase.HasValue ? request.PathBase.Value : "/" }]));
        app.UseSwaggerUI();
        app.UseCors();
        app.UseRateLimiter();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers.CacheControl = "no-store";
            await next(ctx);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // graceful shutdown: закрыть все WS при остановке приложения
        Lifetime?.ApplicationStopping.Register(() =>
            _ = Hub.CloseAll(1001, "server shutting down"));
    }
}
