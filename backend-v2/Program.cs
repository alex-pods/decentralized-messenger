using Messenger.Api.Common.Errors;
using Messenger.Api.Database;
using Messenger.Api.Endpoints;
using Messenger.Api.Infrastructure;
using Messenger.Api.Repositories;
using Messenger.Api.Services;
using Messenger.Api.Realtime;

var builder = WebApplication.CreateBuilder(args);
var allowedOrigins =
    (builder.Configuration["MESSENGER_ALLOWED_ORIGINS"] ?? "")
    .Split(
        ',',
        StringSplitOptions.TrimEntries |
        StringSplitOptions.RemoveEmptyEntries);

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy
                .WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    }));

// Database
builder.Services.AddSingleton(new DbConnectionFactory(builder.Configuration["Db"] ?? "messenger.db"));

// Repositories
builder.Services.AddSingleton<IUsersRepository, UsersRepository>();
builder.Services.AddSingleton<IAvatarsRepository, AvatarsRepository>();
builder.Services.AddSingleton<IMessagesRepository, MessagesRepository>();
builder.Services.AddSingleton<IAttachmentsRepository, AttachmentsRepository>();
builder.Services.AddSingleton<IChatMembershipRepository, ChatMembershipRepository>();
builder.Services.AddSingleton<IChatsRepository, ChatsRepository>();
builder.Services.AddSingleton<IReactionsRepository, ReactionsRepository>();
builder.Services.AddSingleton<ISessionsRepository, SessionsRepository>();
builder.Services.AddSingleton<IFileStorage, FileStorage>();

builder.Services.AddSingleton<WsConnectionRegistry>();

// Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUsersService, UsersService>();
builder.Services.AddScoped<IMessagesService, MessagesService>();
builder.Services.AddScoped<IChatsService, ChatsService>();
builder.Services.AddScoped<IReactionsService, ReactionsService>();
builder.Services.AddScoped<IAvatarsService, AvatarsService>();
builder.Services.AddScoped<IAttachmentsService, AttachmentsService>();

builder.Services.AddScoped<WsEventPublisher>();

builder.Services.AddHostedService<WsSessionMonitor>();

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 110 * 1024 * 1024);

var app = builder.Build();

var wsConnections =
    app.Services.GetRequiredService<WsConnectionRegistry>();

using var shutdownRegistration =
    app.Lifetime.ApplicationStopping.Register(
        wsConnections.BeginShutdown);

app.UseMiddleware<ErrorMiddleware>();

var pathBase =
    builder.Configuration["MESSENGER_PATH_BASE"] ?? "/messenger";

if (!string.IsNullOrWhiteSpace(pathBase) && pathBase != "/")
    app.UsePathBase(pathBase.TrimEnd('/'));

app.UseRouting();
app.UseCors();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.Use(async (context, next) =>
{
    var isWsEndpoint = string.Equals(
        context.Request.Path.Value?.TrimEnd('/'),
        "/ws",
        StringComparison.OrdinalIgnoreCase);

    if (isWsEndpoint)
    {
        var origin = context.Request.Headers.Origin.ToString();

        if (origin.Length > 0 &&
            !allowedOrigins.Contains(
                origin,
                StringComparer.OrdinalIgnoreCase))
        {
            throw AppException.Forbidden(
                "WebSocket origin not allowed");
        }
    }

    await next(context);
});

app.UseMiddleware<SessionAuthenticationMiddleware>();
MigrationRunner.Run(app.Services.GetRequiredService<DbConnectionFactory>());

// Endpoints («ручки»)
app.MapReactionsEndpoints();
app.MapWsEndpoints();
app.MapUsersEndpoints();
app.MapMessagesEndpoints();
app.MapAttachmentsEndpoints();
app.MapChatsEndpoints();
app.MapAuthEndpoints();

app.Run();
