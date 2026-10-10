using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LocalBackend.Db;
using LocalBackend.Services;
using Messenger.Core;
using Messenger.Core.Interfaces;

namespace LocalBackend;

public static class SqliteStoreFactory
{
    public static async Task<IMessageStore> OpenAsync(
        AccountIdentity account,
        string dataDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var serverKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(account.ServerBase)));

        var dbPath = Path.Combine(
            dataDirectory,
            "accounts",
            serverKey,
            account.UserId.ToString(CultureInfo.InvariantCulture),
            "messenger.db");

        var factory = MessengerDb.CreateFactory(dbPath);

        await DatabaseInitializer.InitializeAsync(factory, ct);

        return new MessageStore(factory);
    }
}