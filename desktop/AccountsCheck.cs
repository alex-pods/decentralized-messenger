using System.Text.Json;

namespace MessengerDesktop;

public static class AccountsCheck
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "messenger-accounts-" + Guid.NewGuid().ToString("N"));
        var oldPath = AccountStore.FilePath;
        var oldPreviewServer = Preview.Server;
        var oldPreviewOutDir = Preview.OutDir;
        Directory.CreateDirectory(directory);
        try
        {
            Preview.Server = "http://127.0.0.1:5080/messenger";
            Preview.OutDir = directory;
            AccountStore.FilePath = Path.Combine(directory, "accounts.json");
            var legacyPath = Path.Combine(directory, "test-session.json");
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { Server = "https://EXAMPLE.com/messenger/", Token = "legacy-token" }));

            var migrated = AccountStore.MigrateLegacyAsync((server, token, _) =>
            {
                Require(server == "https://EXAMPLE.com/messenger/" && token == "legacy-token", "Legacy session values changed during migration");
                return Task.FromResult(new ProfileDto { Id = 1, Tag = "first", DisplayName = "Первый" });
            }).GetAwaiter().GetResult();
            var first = AccountStore.Load();
            Require(migrated && !File.Exists(legacyPath), "Legacy session was not removed after successful migration");
            Require(first.Accounts.Count == 1 && first.ActiveAccount == AccountIdentity.Create("https://example.com/messenger", 1),
                "Legacy session was not imported as the active account");
            Require(first.Accounts[0].Token == "legacy-token" && first.Accounts[0].DisplayName == "Первый",
                "Migrated account metadata or token was lost");
            if (!OperatingSystem.IsWindows())
                Require(File.GetUnixFileMode(AccountStore.FilePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "Account file permissions are not 0600");

            AccountStore.AddOrUpdate(new StoredAccount("https://EXAMPLE.COM/messenger/", 1, "first", "Обновлённый", "rotated-token"));
            Require(AccountStore.Load().Accounts.Count == 1 && AccountStore.Load().Accounts[0].Token == "rotated-token",
                "Server host case or trailing slash caused a duplicate account");

            for (var id = 2; id <= AccountStore.MaxAccounts; id++)
                AccountStore.AddOrUpdate(new StoredAccount($"https://server{id}.example/messenger", id, $"user{id}", $"User {id}", $"token-{id}"));
            var limitRejected = false;
            try { AccountStore.AddOrUpdate(new StoredAccount("https://six.example", 6, "six", "Six", "token-6")); }
            catch (InvalidOperationException) { limitRejected = true; }
            Require(limitRejected && AccountStore.Load().Accounts.Count == AccountStore.MaxAccounts,
                "Account limit was not enforced");

            AccountStore.MarkNeedsLogin(AccountIdentity.Create("https://server2.example/messenger", 2));
            Require(AccountStore.Find(AccountIdentity.Create("https://server2.example/messenger", 2))?.NeedsLogin == true,
                "NeedsLogin was not persisted");
            AccountStore.Remove(AccountIdentity.Create("https://server3.example/messenger", 3));
            Require(AccountStore.Load().Accounts.All(account => account.UserId != 3),
                "Removing an account left its token in the account store");

            var active = AccountStore.Load().ActiveAccount!;
            var next = AccountStore.Remove(active);
            Require(next is not null && AccountStore.Load().ActiveAccount == next.Identity,
                "Removing the active account did not select the next account");
            Require(AccountStore.Load().Accounts.Count == AccountStore.MaxAccounts - 2,
                "Unexpected account removal count");

            File.Delete(AccountStore.FilePath);
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { Server = "https://example.com", Token = "keep-me" }));
            var migrationFailed = false;
            try
            {
                AccountStore.MigrateLegacyAsync((_, _, _) => throw new HttpRequestException("offline"))
                    .GetAwaiter().GetResult();
            }
            catch (HttpRequestException) { migrationFailed = true; }
            Require(migrationFailed && File.Exists(legacyPath) && !File.Exists(AccountStore.FilePath),
                "A failed legacy import removed or overwrote the only session");

            Console.WriteLine("PASS accounts: migration, retention on failure, deduplication, 0600 permissions, limit, NeedsLogin, removal, next active");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
        finally
        {
            AccountStore.FilePath = oldPath;
            Preview.Server = oldPreviewServer;
            Preview.OutDir = oldPreviewOutDir;
            Directory.Delete(directory, recursive: true);
        }
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
