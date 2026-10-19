using MessengerBackend;
using MessengerBackend.Infrastructure;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MessengerBackend.Core;
using MessengerBackend.Endpoints;
using MessengerBackend.Hubs;

var builder = WebApplication.CreateBuilder(args);
// Hosting diagnostics include the full query string (WS session tokens).
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = 64_000_000;
});

builder.Services.AddStorageSecurity();
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("auth", ctx => RateLimitPartition.GetSlidingWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new SlidingWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await Errors.Err(429, "слишком много запросов, повторите позже").ExecuteAsync(ctx.HttpContext);
    };
});
builder.Services.AddEndpointsApiExplorer();
// строгий контракт: неизвестные поля JSON-тел отклоняются (400), как и заявлено в схемах
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
builder.Services.AddSwaggerGen(SwaggerSetup.Configure);
builder.Services.AddSingleton<Hub>();
builder.Services.AddHostedService<MessengerBackend.Features.Realtime.SessionCleanup>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (SecurityConfiguration.AllowedOrigins.Length > 0)
        p.WithOrigins(SecurityConfiguration.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();

var ctx = AppCtx.Resolve(app);
ErrorMapping.Install(app);
ctx.Install(app);          // pathbase, swagger, cors, ws, shutdown-обработка
app.MapAuthEndpoints();
app.MapProfileEndpoints();
app.MapChatEndpoints();
app.MapMessageEndpoints();
app.MapReactionEndpoints();
app.MapAttachmentEndpoints();
app.MapMemberEndpoints();
app.MapWsEndpoints(); // включая /health и /ws/protocol

try
{
    StorageEncryption.Configure(app.Services);
    Db.Init();
    await StorageEncryption.Migrate(ctx.FilesRoot);
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Storage startup failed; no requests will be accepted");
    await app.DisposeAsync();
    Environment.ExitCode = 1;
    return;
}
app.Run();
