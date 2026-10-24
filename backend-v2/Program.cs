using Messenger.Api.Common.Errors;
using Messenger.Api.Database;
using Messenger.Api.Endpoints;
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
builder.Services.AddSingleton<ISessionsRepository, SessionsRepository>();
builder.Services.AddSingleton<IFileStorage, FileStorage>();

// Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUsersService, UsersService>();
builder.Services.AddScoped<IMessagesService, MessagesService>();
builder.Services.AddScoped<IAvatarsService, AvatarsService>();
builder.Services.AddScoped<IAttachmentsService, AttachmentsService>();

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 110 * 1024 * 1024);

var app = builder.Build();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<SessionAuthenticationMiddleware>();
MigrationRunner.Run(app.Services.GetRequiredService<DbConnectionFactory>());

// Endpoints («ручки»)
app.MapUsersEndpoints();
app.MapMessagesEndpoints();
app.MapAttachmentsEndpoints();
app.MapChatsEndpoints();
app.MapAuthEndpoints();

app.Run();
