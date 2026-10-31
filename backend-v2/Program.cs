using Messenger.Api.Common.Errors;
using Messenger.Api.Database;
using Messenger.Api.Endpoints;
using Messenger.Api.Infrastructure;
using Messenger.Api.Repositories;
using Messenger.Api.Services;

var builder = WebApplication.CreateBuilder(args);

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

// Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUsersService, UsersService>();
builder.Services.AddScoped<IMessagesService, MessagesService>();
builder.Services.AddScoped<IChatsService, ChatsService>();
builder.Services.AddScoped<IReactionsService, ReactionsService>();
builder.Services.AddScoped<IAvatarsService, AvatarsService>();
builder.Services.AddScoped<IAttachmentsService, AttachmentsService>();

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 110 * 1024 * 1024);

var app = builder.Build();

app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<SessionAuthenticationMiddleware>();

var dbFactory = app.Services.GetRequiredService<DbConnectionFactory>();
MigrationRunner.Run(dbFactory);
DevSeed.Run(dbFactory);

// Endpoints («ручки»)
app.MapUsersEndpoints();
app.MapMessagesEndpoints();
app.MapAttachmentsEndpoints();
app.MapChatsEndpoints();
app.MapAuthEndpoints();

app.Run();
