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
            Require(!File.ReadAllText(AccountStore.FilePath).Contains("\"Warning\"", StringComparison.Ordinal),
                "Transient account warnings changed the accounts.json format");
            Require(!first.Accounts[0].ToString()!.Contains(first.Accounts[0].Token, StringComparison.Ordinal),
                "StoredAccount.ToString exposed its token");
            Require(AccountManager.SessionRevokedMessage("@first") ==
                    "Сессия @first завершена. Выполнен вход в другой аккаунт.",
                "Session-revoked message did not include the account tag");
            Require(!Views.ShellView.ShouldLoadAccountAvatar(
                    new StoredAccount("https://example.com", 1, "first", "Первый", "", true), active: false) &&
                    !Views.ShellView.ShouldLoadAccountAvatar(
                        new StoredAccount("https://example.com", 1, "first", "Первый", "token"), active: true) &&
                    Views.ShellView.ShouldLoadAccountAvatar(
                        new StoredAccount("https://example.com", 1, "first", "Первый", "token"), active: false),
                "Account menu avatar loading policy is incorrect");
            var oldSession = new StoredAccount("https://example.com", 1, "first", "Первый", "old-token");
            Require(!AccountManager.ShouldLogoutPreviousSession(oldSession with { NeedsLogin = true }, "new-token") &&
                    AccountManager.ShouldLogoutPreviousSession(oldSession, "new-token") &&
                    !AccountManager.ShouldLogoutPreviousSession(oldSession, "old-token") &&
                    AccountManager.IsExpiredSession(new ApiException(401, "unauthorized")),
                "Previous-session logout policy is incorrect");
            var restoreFailures = new List<Exception>();
            var restoredAccount = AccountManager.TryRestoreCandidatesAsync(
                [
                    new StoredAccount("https://first.example", 1, "first", "First", "token-1"),
                    new StoredAccount("https://second.example", 2, "second", "Second", "token-2")
                ],
                (account, _) => account.UserId == 1
                    ? Task.FromException<string?>(new ApiException(500, "server error"))
                    : Task.FromResult<string?>("second"),
                (_, exception) => restoreFailures.Add(exception),
                CancellationToken.None).GetAwaiter().GetResult();
            Require(restoredAccount == "second" && restoreFailures.Count == 1 &&
                    restoreFailures[0] is ApiException { Status: 500 },
                "A failed first account prevented restoring the next account");
            Require(!AccountManager.ShouldHandleSessionRevoked(false, true) &&
                    !AccountManager.ShouldHandleSessionRevoked(true, false) &&
                    AccountManager.ShouldHandleSessionRevoked(true, true),
                "A stale session-revoked event would navigate away from login");
            using (var cancellation = new CancellationTokenSource())
            {
                AccountManager.CancelOperationWithoutDisposing(cancellation);
                Require(cancellation.IsCancellationRequested && cancellation.Token.IsCancellationRequested,
                    "Cancelling an account operation disposed its cancellation source too early");
            }
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
            Require(AccountManager.HasReachedAccountLimit(AccountStore.MaxAccounts) &&
                    !AccountManager.HasReachedAccountLimit(AccountStore.MaxAccounts - 1),
                "The early account limit check is incorrect");
            Require(AccountManager.IsNetworkError(new HttpRequestException("offline")) &&
                    !AccountManager.IsNetworkError(new IOException("disk write failed")),
                "A filesystem error was classified as a network error");
            var needsLoginCandidate = new StoredAccount("https://needs.example", 10, "needs", "Needs", "", true);
            var availableCandidate = new StoredAccount("https://available.example", 11, "available", "Available", "token");
            Require(AccountManager.SelectNextAccount(
                    [needsLoginCandidate, availableCandidate], new HashSet<AccountIdentity>()) == availableCandidate,
                "Automatic account switching did not prefer an account that can be restored");

            AccountStore.MarkNeedsLogin(AccountIdentity.Create("https://server2.example/messenger", 2));
            Require(AccountStore.Find(AccountIdentity.Create("https://server2.example/messenger", 2))?.NeedsLogin == true,
                "NeedsLogin was not persisted");
            AccountStore.Remove(AccountIdentity.Create("https://server3.example/messenger", 3));
            Require(AccountStore.Load().Accounts.All(account => account.UserId != 3),
                "Removing an account left its token in the account store");

            var firstAccount = AccountStore.Find(AccountIdentity.Create("https://example.com/messenger", 1))!;
            AccountStore.AddOrUpdate(firstAccount);
            var active = AccountStore.Load().ActiveAccount!;
            var next = AccountStore.Remove(active);
            Require(next is not null && !next.NeedsLogin && AccountStore.Load().ActiveAccount == next.Identity,
                "Removing the active account did not select the next account");
            Require(AccountStore.Load().Accounts.Count == AccountStore.MaxAccounts - 2,
                "Unexpected account removal count");

            var oversized = new AccountData
            {
                Accounts = Enumerable.Range(1, AccountStore.MaxAccounts + 2)
                    .Select(id => new StoredAccount(
                        $"https://oversized{id}.example", id, $"user{id}", $"User {id}",
                        id == 1 ? "" : $"token-{id}"))
                    .ToList()
            };
            File.WriteAllText(AccountStore.FilePath, JsonSerializer.Serialize(oversized));
            var normalizedOversized = AccountStore.Load();
            Require(normalizedOversized.Accounts.Count == AccountStore.MaxAccounts + 2 &&
                    normalizedOversized.Accounts[0].Token == "" &&
                    normalizedOversized.Accounts[0].NeedsLogin &&
                    !string.IsNullOrWhiteSpace(normalizedOversized.Warning),
                "Normalization silently discarded an empty-token or over-limit account");
            AccountStore.AddOrUpdate(normalizedOversized.Accounts[1] with { Token = "updated-token" });
            Require(AccountStore.Load().Accounts.Count == AccountStore.MaxAccounts + 2,
                "Saving normalized account data discarded records beyond the limit");

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
            Require(AccountStore.LegacyServerForLogin() == "https://example.com",
                "The legacy server was not available for a manual login");
            AccountStore.AddOrUpdate(new StoredAccount("https://example.com", 7, "manual", "Manual", "manual-token"));
            Require(!File.Exists(legacyPath),
                "A successful manual login left the legacy session token on disk");

            const string damagedContents = "{ not valid json";
            File.WriteAllText(AccountStore.FilePath, damagedContents);
            var damaged = AccountStore.Load();
            var quarantined = Directory.GetFiles(directory, "accounts.json.corrupt-*").SingleOrDefault();
            Require(damaged.Accounts.Count == 0 && !File.Exists(AccountStore.FilePath) &&
                    !string.IsNullOrWhiteSpace(damaged.Warning) && quarantined is not null &&
                    File.ReadAllText(quarantined) == damagedContents,
                "Corrupt account data was not quarantined with a user warning");
            Require(AccountStore.Load().Warning is null,
                "Corrupt-account warning was not limited to the first read");
            Require(AccountStore.TakeLoadWarning() == damaged.Warning &&
                    AccountStore.TakeLoadWarning() is null,
                "The corrupt-account warning was not delivered exactly once");
            if (!OperatingSystem.IsWindows())
                Require(File.GetUnixFileMode(quarantined!) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "Quarantined account file permissions are not 0600");

            Console.WriteLine("PASS accounts: migration, recovery, deduplication, 0600 permissions, limit, NeedsLogin, removal, next active");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
        finally
        {
            AccountStore.FilePath = oldPath;
            Preview.Server = oldPreviewServer;
            Preview.OutDir = oldPreviewOutDir;
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
