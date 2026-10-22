using Messenger.Api.Common.Errors;
using Messenger.Api.Database;
using Messenger.Api.Endpoints;
using Messenger.Api.Repositories;
using Messenger.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddSingleton(new DbConnectionFactory(builder.Configuration["Db"] ?? "messenger.db"));

// Repositories: один на сущность
builder.Services.AddSingleton<IUsersRepository, UsersRepository>();
builder.Services.AddSingleton<IAvatarsRepository, AvatarsRepository>();
builder.Services.AddSingleton<IMessagesRepository, MessagesRepository>();

// Services: правила и сборка ответа
builder.Services.AddSingleton<UsersService>();
builder.Services.AddSingleton<MessagesService>();

var app = builder.Build();
app.UseMiddleware<ErrorMiddleware>();
MigrationRunner.Run(app.Services.GetRequiredService<DbConnectionFactory>());

// Endpoints («ручки»)
app.MapUsersEndpoints();
app.MapMessagesEndpoints();
app.MapChatsEndpoints();
app.MapAuthEndpoints();

app.Run();
