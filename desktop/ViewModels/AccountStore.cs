using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Server = {Server}, UserId = {UserId}, Tag = {Tag}, DisplayName = {DisplayName}, NeedsLogin = {NeedsLogin}");
        return true;
    }
}

public sealed class AccountData
{
    public List<StoredAccount> Accounts { get; set; } = new();
    public AccountIdentity? ActiveAccount { get; set; }
    [JsonIgnore] public string? Warning { get; set; }
}

public static class AccountStore
{
    public const int MaxAccounts = 5;

    // TODO: перейти на системное хранилище секретов (DPAPI, Keychain или libsecret).
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static string? _pendingLoadWarning;

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
            return LoadLocked();
    }

    public static string? TakeLoadWarning()
    {
        lock (Gate)
        {
            var warning = _pendingLoadWarning;
            _pendingLoadWarning = null;
            return warning;
        }
    }

    internal static string? LegacyServerForLogin()
    {
        if (!File.Exists(LegacyFilePath)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<LegacySession>(File.ReadAllText(LegacyFilePath));
            return string.IsNullOrWhiteSpace(saved?.Server) ? null : saved.Server;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
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
            var warning = DeleteLegacyFileIfPresent();
            if (warning is not null) throw new IOException(warning);
        }
        return true;
    }

    public static StoredAccount? Find(AccountIdentity identity) =>
        Load().Accounts.FirstOrDefault(account => account.Identity == identity);

    public static AccountData AddOrUpdate(StoredAccount account, bool makeActive = true)
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
            data.Warning ??= DeleteLegacyFileIfPresent();
            return data;
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
            {
                var count = data.Accounts.Count;
                var start = count == 0 ? 0 : index % count;
                var ordered = data.Accounts.Skip(start).Concat(data.Accounts.Take(start)).ToList();
                var selectedNext = ordered.FirstOrDefault(account => !account.NeedsLogin) ?? ordered.FirstOrDefault();
                data.ActiveAccount = selectedNext?.Identity;
            }
            else if (data.ActiveAccount is { } active && !data.Accounts.Any(account => account.Identity == active))
                data.ActiveAccount = (data.Accounts.FirstOrDefault(account => !account.NeedsLogin) ??
                    data.Accounts.FirstOrDefault())?.Identity;
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
        try
        {
            return Normalize(JsonSerializer.Deserialize<AccountData>(File.ReadAllText(FilePath), Json)
                ?? throw new InvalidDataException("Файл аккаунтов пуст или повреждён"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var corruptPath = QuarantineCorruptFile();
            var warning = $"Список аккаунтов повреждён. Копия сохранена в {Path.GetFileName(corruptPath)}.";
            _pendingLoadWarning ??= warning;
            return new AccountData
            {
                Warning = warning
            };
        }
    }

    static AccountData Normalize(AccountData data)
    {
        var accounts = new List<StoredAccount>();
        var warnings = new List<string>();
        foreach (var account in data.Accounts ?? [])
        {
            if (account is null)
            {
                warnings.Add("В списке были пропущены пустые записи.");
                continue;
            }
            if (account.UserId <= 0 || string.IsNullOrWhiteSpace(account.Server))
            {
                warnings.Add("В списке были пропущены записи с некорректной идентичностью.");
                continue;
            }
            var normalized = account with { Server = NormalizeServer(account.Server) };
            if (string.IsNullOrWhiteSpace(normalized.Token))
            {
                normalized = normalized with { NeedsLogin = true };
                warnings.Add($"Для аккаунта @{normalized.Tag} требуется повторный вход (токен отсутствует).");
            }
            var duplicate = accounts.FindIndex(item => item.Identity == normalized.Identity);
            if (duplicate >= 0)
            {
                accounts[duplicate] = normalized;
                warnings.Add("Повторяющиеся записи аккаунтов объединены.");
            }
            else
            {
                accounts.Add(normalized);
                if (accounts.Count == MaxAccounts + 1)
                    warnings.Add($"В файле больше {MaxAccounts} аккаунтов; лишние записи сохранены.");
            }
        }
        var active = data.ActiveAccount is { } key && !string.IsNullOrWhiteSpace(key.Server)
            ? AccountIdentity.Create(key.Server, key.UserId)
            : null;
        if (active is not null && !accounts.Any(account => account.Identity == active))
            active = accounts.FirstOrDefault()?.Identity;
        return new AccountData
        {
            Accounts = accounts,
            ActiveAccount = active ?? accounts.FirstOrDefault()?.Identity,
            Warning = string.Join(Environment.NewLine, new[] { data.Warning, string.Join(Environment.NewLine, warnings) }
                .Where(message => !string.IsNullOrWhiteSpace(message)))
        };
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

    static string QuarantineCorruptFile()
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var corruptPath = FilePath + ".corrupt-" + timestamp;
        while (File.Exists(corruptPath))
            corruptPath = FilePath + ".corrupt-" + timestamp + "-" + Guid.NewGuid().ToString("N");
        File.Move(FilePath, corruptPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(corruptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return corruptPath;
    }

    static string? DeleteLegacyFileIfPresent()
    {
        if (string.Equals(FilePath, LegacyFilePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(LegacyFilePath))
            return null;
        try
        {
            File.Delete(LegacyFilePath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Не удалось удалить старый файл сессии: " + ex.Message;
        }
    }

    sealed class LegacySession
    {
        public string Server { get; set; } = "";
        public string Token { get; set; } = "";
    }
}
