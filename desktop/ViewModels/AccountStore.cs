using System.Text;
using System.Text.Json;

namespace MessengerDesktop;

public sealed record AccountIdentity(string Server, long UserId)
{
    public static AccountIdentity Create(string server, long userId) => new(AccountStore.NormalizeServer(server), userId);
}

public sealed record StoredAccount(
    string Server,
    long UserId,
    string Tag,
    string DisplayName,
    string Token,
    bool NeedsLogin = false)
{
    public AccountIdentity Identity => AccountIdentity.Create(Server, UserId);
}

public sealed class AccountData
{
    public List<StoredAccount> Accounts { get; set; } = new();
    public AccountIdentity? ActiveAccount { get; set; }
}

public static class AccountStore
{
    public const int MaxAccounts = 5;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "messenger-desktop", "accounts.json");

    static string LegacyFilePath => Preview.Server != null
        ? Path.Combine(Preview.OutDir, "test-session.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "messenger-desktop", "session.json");

    public static AccountData Load()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return new();
            var data = JsonSerializer.Deserialize<AccountData>(File.ReadAllText(FilePath), Json)
                ?? throw new InvalidDataException("Файл аккаунтов пуст или повреждён");
            return Normalize(data);
        }
    }

    public static async Task<bool> MigrateLegacyAsync(
        Func<string, string, CancellationToken, Task<ProfileDto>> getProfile,
        CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            if (File.Exists(FilePath) || !File.Exists(LegacyFilePath)) return false;
        }

        var saved = JsonSerializer.Deserialize<LegacySession>(await File.ReadAllTextAsync(LegacyFilePath, cancellationToken))
            ?? throw new InvalidDataException("Старая сессия пуста или повреждена");
        if (string.IsNullOrWhiteSpace(saved.Server) || string.IsNullOrWhiteSpace(saved.Token))
            throw new InvalidDataException("В старой сессии отсутствуют сервер или токен");

        var profile = await getProfile(saved.Server, saved.Token, cancellationToken);
        if (profile.Id <= 0)
            throw new InvalidDataException("Сервер не вернул идентификатор пользователя для старой сессии");
        var account = new StoredAccount(
            NormalizeServer(saved.Server), profile.Id, profile.Tag ?? "", profile.DisplayName ?? "", saved.Token);
        lock (Gate)
        {
            if (File.Exists(FilePath)) return false;
            SaveLocked(new AccountData { Accounts = [account], ActiveAccount = account.Identity });
            File.Delete(LegacyFilePath);
        }
        return true;
    }

    public static StoredAccount? Find(AccountIdentity identity) =>
        Load().Accounts.FirstOrDefault(account => account.Identity == identity);

    public static void AddOrUpdate(StoredAccount account, bool makeActive = true)
    {
        lock (Gate)
        {
            var data = LoadLocked();
            var normalized = account with { Server = NormalizeServer(account.Server) };
            if (string.IsNullOrWhiteSpace(normalized.Server) || normalized.UserId <= 0 ||
                string.IsNullOrWhiteSpace(normalized.Token))
                throw new ArgumentException("Для аккаунта нужны сервер, положительный UserId и токен", nameof(account));
            var index = data.Accounts.FindIndex(item => item.Identity == normalized.Identity);
            if (index < 0 && data.Accounts.Count >= MaxAccounts)
                throw new InvalidOperationException($"Можно добавить не более {MaxAccounts} аккаунтов");
            if (index < 0) data.Accounts.Add(normalized);
            else data.Accounts[index] = normalized;
            if (makeActive) data.ActiveAccount = normalized.Identity;
            SaveLocked(data);
        }
    }

    public static void SetActive(AccountIdentity identity)
    {
        lock (Gate)
        {
            var data = LoadLocked();
            if (!data.Accounts.Any(account => account.Identity == identity))
                throw new InvalidOperationException("Аккаунт не найден");
            data.ActiveAccount = identity;
            SaveLocked(data);
        }
    }

    public static StoredAccount? MarkNeedsLogin(AccountIdentity identity)
    {
        lock (Gate)
        {
            var data = LoadLocked();
            var index = data.Accounts.FindIndex(account => account.Identity == identity);
            if (index < 0) return null;
            data.Accounts[index] = data.Accounts[index] with { NeedsLogin = true };
            SaveLocked(data);
            return data.Accounts[index];
        }
    }

    public static StoredAccount? Remove(AccountIdentity identity)
    {
        lock (Gate)
        {
            var data = LoadLocked();
            var index = data.Accounts.FindIndex(account => account.Identity == identity);
            if (index < 0) return null;
            var wasActive = data.ActiveAccount == identity;
            data.Accounts.RemoveAt(index);
            if (wasActive)
                data.ActiveAccount = data.Accounts.Count == 0 ? null : data.Accounts[index % data.Accounts.Count].Identity;
            else if (data.ActiveAccount is { } active && !data.Accounts.Any(account => account.Identity == active))
                data.ActiveAccount = data.Accounts.FirstOrDefault()?.Identity;
            SaveLocked(data);
            return wasActive && data.ActiveAccount is { } next
                ? data.Accounts.FirstOrDefault(account => account.Identity == next)
                : null;
        }
    }

    internal static string NormalizeServer(string? server)
    {
        var value = server?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return value;
        var builder = new UriBuilder(uri) { Host = uri.Host.ToLowerInvariant() };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    static AccountData LoadLocked()
    {
        if (!File.Exists(FilePath)) return new();
        return Normalize(JsonSerializer.Deserialize<AccountData>(File.ReadAllText(FilePath), Json)
            ?? throw new InvalidDataException("Файл аккаунтов пуст или повреждён"));
    }

    static AccountData Normalize(AccountData data)
    {
        var accounts = new List<StoredAccount>();
        foreach (var account in data.Accounts ?? [])
        {
            if (account is null) continue;
            if (account.UserId <= 0 || string.IsNullOrWhiteSpace(account.Server) ||
                string.IsNullOrWhiteSpace(account.Token)) continue;
            var normalized = account with { Server = NormalizeServer(account.Server) };
            var duplicate = accounts.FindIndex(item => item.Identity == normalized.Identity);
            if (duplicate >= 0) accounts[duplicate] = normalized;
            else if (accounts.Count < MaxAccounts) accounts.Add(normalized);
        }
        var active = data.ActiveAccount is { } key && !string.IsNullOrWhiteSpace(key.Server)
            ? AccountIdentity.Create(key.Server, key.UserId)
            : null;
        if (active is not null && !accounts.Any(account => account.Identity == active))
            active = accounts.FirstOrDefault()?.Identity;
        return new AccountData { Accounts = accounts, ActiveAccount = active ?? accounts.FirstOrDefault()?.Identity };
    }

    static void SaveLocked(AccountData data)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Normalize(data), Json));
        if (File.Exists(temporary)) File.Delete(temporary);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    sealed class LegacySession
    {
        public string Server { get; set; } = "";
        public string Token { get; set; } = "";
    }
}
