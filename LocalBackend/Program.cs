using LocalBackend.Db;
using LocalBackend.Interfaces;
using LocalBackend.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Environment.GetEnvironmentVariable("MESSENGER_DB_PATH")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Messenger", "messenger.db");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = dbPath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    ForeignKeys = true,
    DefaultTimeout = 30
}.ToString();

builder.Services.AddDbContextFactory<MessengerDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddSingleton<IMessageStore, MessageStore>();

using var host = builder.Build();

var factory = host.Services.GetRequiredService<IDbContextFactory<MessengerDbContext>>();
await DatabaseInitializer.InitializeAsync(factory);

await host.RunAsync();