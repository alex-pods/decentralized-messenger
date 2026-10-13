using MessengerBackend;
using MessengerBackend.Core;
using MessengerBackend.Endpoints;
using MessengerBackend.Hubs;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

AppCtx.Configure(builder.Environment, builder.Configuration);

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = 64_000_000;
});

builder.Services.AddEndpointsApiExplorer();
// строгий контракт: неизвестные поля JSON-тел отклоняются (400), как и заявлено в схемах
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
builder.Services.AddSwaggerGen(SwaggerSetup.Configure);
builder.Services.AddSingleton<Hub>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

var ctx = AppCtx.Resolve(app);
ErrorMapping.Install(app);
ctx.Install(app);          // pathbase, swagger, cors, ws, shutdown-обработка
app.MapAuthEndpoints();
app.MapProfileEndpoints();
app.MapChatEndpoints();
app.MapMessageEndpoints();
app.MapWsEndpoints(); // включая /health и /ws/protocol

Db.Init();
app.Run();
