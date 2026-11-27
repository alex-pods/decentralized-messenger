using Avalonia.Threading;

namespace MessengerDesktop;

public sealed class AccountManager
{
    readonly Navigator _nav;
    readonly object _operationGate = new();
    CancellationTokenSource? _operation;
    CancellationTokenSource? _restoreOperation;
    int _generation;

    public AccountManager(Navigator nav) => _nav = nav;
    public ShellViewModel? CurrentShell { get; private set; }
    public IReadOnlyList<StoredAccount> Accounts => AccountStore.Load().Accounts;

    internal void Attach(ShellViewModel shell) => CurrentShell = shell;

    public void AddAccount(ShellViewModel shell)
    {
        AccountData data;
        try { data = AccountStore.Load(); }
        catch (Exception ex)
        {
            shell.Error = "Не удалось проверить список аккаунтов: " + ex.Message;
            return;
        }
        data.Warning = JoinWarnings(data.Warning, AccountStore.TakeLoadWarning());
        if (HasReachedAccountLimit(data.Accounts.Count))
        {
            shell.Error = JoinWarnings(data.Warning,
                $"Можно добавить не более {AccountStore.MaxAccounts} аккаунтов");
            return;
        }
        CancelOperation();
        CurrentShell = shell;
        _nav.Go(new LoginViewModel(
            _nav, shell.CurrentIdentity.Server, data.Warning, accounts: this, adding: true, returnShell: shell));
    }

    public void Reauthenticate(StoredAccount account, ShellViewModel? returnShell, string? error = null)
    {
        CancelOperation();
        _nav.Go(new LoginViewModel(_nav, account.Server, error, this, adding: false, returnShell, account.Tag));
    }

    public void CancelLogin(ShellViewModel? returnShell, string server)
    {
        if (returnShell is not null && !returnShell.IsClosed)
        {
            CurrentShell = returnShell;
            _nav.Go(returnShell);
        }
        else
            _nav.Go(new LoginViewModel(_nav, server, accounts: this));
    }

    public async Task RestoreAsync(string? serverOverride = null)
    {
        var restore = new CancellationTokenSource();
        lock (_operationGate)
        {
            _restoreOperation?.Cancel();
            _restoreOperation = restore;
        }
        try
        {
            try
            {
                await AccountStore.MigrateLegacyAsync(async (server, token, ct) =>
                {
                    using var legacyApi = new MessengerClient(server, token);
                    return await legacyApi.GetMe(ct);
                }, restore.Token);
            }
            catch (OperationCanceledException) when (restore.IsCancellationRequested) { return; }
            catch (Exception ex) when (IsNetworkError(ex) ||
                ex is ApiException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                var legacyServer = AccountStore.LegacyServerForLogin() ?? serverOverride;
                await ShowLogin(null, legacyServer, LoginViewModel.Friendly(ex), restore.Token);
                return;
            }

            AccountData data;
            try { data = AccountStore.Load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                await ShowLogin(null, serverOverride, "Не удалось прочитать список аккаунтов: " + ex.Message, restore.Token);
                return;
            }

            var loadWarning = JoinWarnings(data.Warning, AccountStore.TakeLoadWarning());
            var candidates = OrderedAccounts(data);
            if (!string.IsNullOrWhiteSpace(serverOverride))
                candidates = candidates.Where(account =>
                    string.Equals(account.Server, AccountStore.NormalizeServer(serverOverride), StringComparison.OrdinalIgnoreCase)).ToList();

            Exception? lastError = null;
            var needsLogin = candidates.Where(account => account.NeedsLogin)
                .Select(account => account.Identity).ToHashSet();
            var restored = await TryRestoreCandidatesAsync(
                candidates.Where(account => !account.NeedsLogin),
                async (account, cancellationToken) =>
                {
                    MessengerClient? api = null;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        api = new MessengerClient(account.Server, account.Token);
                        var me = await api.GetMe(cancellationToken);
                        if (!await ActivateRestored(account, api, me, cancellationToken, loadWarning)) return null;
                        api = null;
                        return account;
                    }
                    finally { api?.Dispose(); }
                },
                (account, exception) =>
                {
                    lastError = exception;
                    if (exception is not ApiException { Status: 401 }) return;
                    needsLogin.Add(account.Identity);
                    try { AccountStore.MarkNeedsLogin(account.Identity); }
                    catch (Exception markError) { lastError = markError; }
                },
                restore.Token);
            if (restored is not null)
            {
                if (AccountStore.TakeLoadWarning() is { } warning && CurrentShell is { } activeShell)
                    activeShell.Error = JoinWarnings(activeShell.Error, warning);
                return;
            }
            loadWarning = JoinWarnings(loadWarning, AccountStore.TakeLoadWarning());

            if (restore.IsCancellationRequested) return;
            var reauth = candidates.FirstOrDefault(account =>
                             account.Identity != data.ActiveAccount && needsLogin.Contains(account.Identity)) ??
                         candidates.FirstOrDefault(account => account.Identity != data.ActiveAccount) ??
                         candidates.FirstOrDefault(account => account.NeedsLogin) ??
                         candidates.FirstOrDefault();
            if (reauth is not null)
                await ShowLogin(reauth, serverOverride, JoinWarnings(
                    loadWarning, lastError is null ? null : LoginViewModel.Friendly(lastError)), restore.Token);
            else
                await ShowLogin(null, serverOverride, JoinWarnings(
                    loadWarning, lastError is null ? null : LoginViewModel.Friendly(lastError)), restore.Token);
        }
        finally
        {
            lock (_operationGate)
                if (ReferenceEquals(_restoreOperation, restore)) _restoreOperation = null;
            restore.Dispose();
        }
    }

    public async Task SwitchToAsync(StoredAccount account, ShellViewModel requester)
    {
        if (account.NeedsLogin)
        {
            Reauthenticate(account, requester);
            return;
        }

        var (generation, operation) = StartOperation(requester);
        MessengerClient? api = null;
        try
        {
            api = new MessengerClient(account.Server, account.Token);
            var me = await api.GetMe(operation.Token);
            if (!IsCurrent(generation)) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrent(generation)) return;
                var saved = AccountStore.AddOrUpdate(account with
                {
                    Tag = me.Tag,
                    DisplayName = me.DisplayName,
                    NeedsLogin = false
                });
                requester.ReleaseResources();
                var shell = new ShellViewModel(_nav, api, me, this);
                api = null;
                CurrentShell = shell;
                _nav.Go(shell);
                shell.Error = JoinWarnings(
                    JoinWarnings(saved.Warning, AccountStore.TakeLoadWarning()), shell.Error);
            });
        }
        catch (ApiException ex) when (ex.Status == 401 && IsCurrent(generation))
        {
            var invalid = AccountStore.MarkNeedsLogin(account.Identity) ?? account with { NeedsLogin = true };
            var warning = AccountStore.TakeLoadWarning();
            await Dispatcher.UIThread.InvokeAsync(() => Reauthenticate(invalid, requester, warning));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsCurrent(generation))
        {
            await Dispatcher.UIThread.InvokeAsync(() => requester.Error = LoginViewModel.Friendly(ex));
        }
        finally
        {
            api?.Dispose();
            CompleteOperation(generation, requester, operation);
        }
    }

    public async Task CompleteLoginAsync(MessengerClient api, ProfileDto me, ShellViewModel? returnShell)
    {
        CancelOperation();
        CancelRestore();
        var server = AccountStore.NormalizeServer(api.ServerBase);
        var identity = AccountIdentity.Create(server, me.Id);
        StoredAccount? previous;
        string? warning;
        try
        {
            var current = AccountStore.Load();
            previous = current.Accounts.FirstOrDefault(account => account.Identity == identity);
            if (previous is null && current.Accounts.Count >= AccountStore.MaxAccounts)
                throw new InvalidOperationException($"Можно добавить не более {AccountStore.MaxAccounts} аккаунтов");
            var saved = AccountStore.AddOrUpdate(new StoredAccount(server, me.Id, me.Tag, me.DisplayName, api.Token!));
            warning = JoinWarnings(JoinWarnings(current.Warning, AccountStore.TakeLoadWarning()), saved.Warning);
        }
        catch (Exception ex)
        {
            try { await api.Logout(); }
            catch (Exception logoutError) when (IsNetworkError(logoutError) || logoutError is ApiException)
            {
                throw new IOException("Не удалось сохранить аккаунт и завершить новую сессию: " +
                    LoginViewModel.Friendly(logoutError), ex);
            }
            throw;
        }
        var shellToReplace = returnShell ?? CurrentShell;
        if (shellToReplace is not null && !shellToReplace.IsClosed)
            shellToReplace.ReleaseResources();
        CurrentShell = null;
        if (ShouldLogoutPreviousSession(previous, api.Token!))
        {
            try
            {
                using var oldApi = new MessengerClient(previous!.Server, previous.Token);
                await oldApi.Logout();
            }
            catch (ApiException ex) when (IsExpiredSession(ex))
            {
            }
            catch (Exception ex)
            {
                warning = JoinWarnings(warning,
                    "Вход выполнен, но предыдущую сессию не удалось завершить: " + LoginViewModel.Friendly(ex));
            }
        }

        var shell = new ShellViewModel(_nav, api, me, this);
        CurrentShell = shell;
        _nav.Go(shell);
        if (warning is not null) shell.Error = warning;
    }

    internal static bool ShouldLogoutPreviousSession(StoredAccount? previous, string newToken) =>
        previous is { NeedsLogin: false } &&
        !string.Equals(previous.Token, newToken, StringComparison.Ordinal);

    internal static bool IsExpiredSession(ApiException exception) => exception.Status == 401;

    internal static async Task<T?> TryRestoreCandidatesAsync<T>(
        IEnumerable<StoredAccount> candidates,
        Func<StoredAccount, CancellationToken, Task<T?>> restore,
        Action<StoredAccount, Exception> onFailure,
        CancellationToken cancellationToken) where T : class
    {
        foreach (var account in candidates)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await restore(account, cancellationToken);
                if (result is not null) return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                onFailure(account, ex);
            }
        }
        return null;
    }

    public async Task LogoutAsync(ShellViewModel shell)
    {
        if (shell.IsClosed || shell.IsEndingAccount) return;
        CancelOperation();
        StoredAccount? account;
        try { account = AccountStore.Find(shell.CurrentIdentity); }
        catch (Exception ex)
        {
            shell.Error = "Не удалось прочитать список аккаунтов: " + ex.Message;
            return;
        }
        if (account is null)
        {
            if (AccountStore.TakeLoadWarning() is { } loadWarning)
                shell.Error = loadWarning;
            return;
        }

        string? warning = null;
        try { await shell.LogoutFromServerAsync(); }
        catch (Exception ex)
        {
            warning = "Не удалось завершить сессию на сервере: " + LoginViewModel.Friendly(ex);
        }

        StoredAccount? next;
        try { next = AccountStore.Remove(account.Identity); }
        catch (Exception ex)
        {
            shell.ReleaseResources();
            CurrentShell = null;
            await ShowLogin(account, null, JoinWarnings(
                AccountStore.TakeLoadWarning(), "Не удалось удалить аккаунт с устройства: " + ex.Message));
            return;
        }
        warning = JoinWarnings(warning, AccountStore.TakeLoadWarning());
        shell.ReleaseResources();
        CurrentShell = null;
        if (next is not null)
        {
            await SwitchAfterClosing(next, warning);
            return;
        }
        await ShowLogin(account, account.Server, warning);
    }

    internal void SessionRevoked(ShellViewModel shell)
    {
        Dispatcher.UIThread.Post(() => _ = HandleSessionRevoked(shell));
    }

    internal void UpdateCurrentProfile(string server, long userId, string tag, string displayName)
    {
        var identity = AccountIdentity.Create(server, userId);
        var saved = AccountStore.Find(identity);
        var warning = AccountStore.TakeLoadWarning();
        if (saved is not null)
        {
            var result = AccountStore.AddOrUpdate(saved with { Tag = tag, DisplayName = displayName });
            warning = JoinWarnings(warning, result.Warning);
        }
        if (warning is not null && CurrentShell is { } currentShell)
            currentShell.Error = JoinWarnings(currentShell.Error, warning);
    }

    async Task HandleSessionRevoked(ShellViewModel shell)
    {
        var identity = shell.CurrentIdentity;
        if (!ShouldHandleSessionRevoked(
                ReferenceEquals(CurrentShell, shell),
                ReferenceEquals(_nav.Window.DataContext, shell)))
        {
            try { AccountStore.MarkNeedsLogin(identity); }
            catch (Exception ex) { Console.Error.WriteLine("Не удалось отметить отозванный аккаунт: " + ex.Message); }
            return;
        }
        if (shell.IsClosed || shell.IsEndingAccount) return;
        CancelOperation();
        var fallback = new StoredAccount(
            identity.Server, identity.UserId, shell.UserTag.TrimStart('@'), shell.AccountName, "", true);
        StoredAccount account;
        List<StoredAccount> alternatives;
        try
        {
            account = AccountStore.MarkNeedsLogin(identity) ?? fallback;
            alternatives = AccountStore.Load().Accounts.Where(item => item.Identity != identity).ToList();
        }
        catch (Exception ex)
        {
            shell.ReleaseResources();
            CurrentShell = null;
            await ShowLogin(fallback, null, "Сессия завершена. Не удалось обновить список аккаунтов: " + ex.Message);
            return;
        }
        var storeWarning = AccountStore.TakeLoadWarning();
        shell.ReleaseResources();
        CurrentShell = null;

        var next = alternatives.FirstOrDefault(item => !item.NeedsLogin) ?? alternatives.FirstOrDefault();
        if (next is not null)
            await SwitchAfterClosing(next, JoinWarnings(storeWarning, SessionRevokedMessage(shell.UserTag)));
        else
            await ShowLogin(account, null, JoinWarnings(storeWarning, "Сессия завершена. Войдите снова."));
    }

    internal static string SessionRevokedMessage(string tag) =>
        $"Сессия @{tag.TrimStart('@')} завершена. Выполнен вход в другой аккаунт.";

    internal static bool ShouldHandleSessionRevoked(bool isCurrentShell, bool isCurrentDataContext) =>
        isCurrentShell && isCurrentDataContext;

    internal static bool HasReachedAccountLimit(int accountCount) =>
        accountCount >= AccountStore.MaxAccounts;

    internal static StoredAccount? SelectNextAccount(
        IEnumerable<StoredAccount> accounts, HashSet<AccountIdentity> attempted)
    {
        var remaining = accounts.Where(account => !attempted.Contains(account.Identity)).ToList();
        return remaining.FirstOrDefault(account => !account.NeedsLogin) ?? remaining.FirstOrDefault();
    }

    internal static void CancelOperationWithoutDisposing(CancellationTokenSource? operation) =>
        operation?.Cancel();

    async Task SwitchAfterClosing(StoredAccount account, string? error, HashSet<AccountIdentity>? attempted = null)
    {
        attempted ??= new();
        attempted.Add(account.Identity);
        MessengerClient? api = null;
        try
        {
            api = new MessengerClient(account.Server, account.Token);
            var me = await api.GetMe();
            var saved = AccountStore.AddOrUpdate(account with
            {
                Tag = me.Tag,
                DisplayName = me.DisplayName,
                NeedsLogin = false
            });
            error = JoinWarnings(error, JoinWarnings(saved.Warning, AccountStore.TakeLoadWarning()));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var shell = new ShellViewModel(_nav, api, me, this);
                api = null;
                CurrentShell = shell;
                _nav.Go(shell);
                if (error is not null) shell.Error = error;
            });
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            var invalid = AccountStore.MarkNeedsLogin(account.Identity) ?? account with { NeedsLogin = true };
            var next = SelectNextAccount(AccountStore.Load().Accounts, attempted);
            if (next is not null)
                await SwitchAfterClosing(next, error, attempted);
            else
                await ShowLogin(invalid, null, JoinWarnings(error, "Не удалось выполнить вход в другой аккаунт."));
        }
        catch (Exception ex)
        {
            await ShowLogin(account, null, JoinWarnings(error, LoginViewModel.Friendly(ex)));
        }
        finally { api?.Dispose(); }
    }

    async Task<bool> ActivateRestored(
        StoredAccount account, MessengerClient api, ProfileDto me, CancellationToken ct, string? warning)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return false;
            var saved = AccountStore.AddOrUpdate(account with
            {
                Tag = me.Tag,
                DisplayName = me.DisplayName,
                NeedsLogin = false
            });
            var activationWarning = JoinWarnings(warning,
                JoinWarnings(saved.Warning, AccountStore.TakeLoadWarning()));
            var shell = new ShellViewModel(_nav, api, me, this);
            CurrentShell = shell;
            _nav.Go(shell);
            if (activationWarning is not null) shell.Error = activationWarning;
            return true;
        });
    }

    internal static string? JoinWarnings(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? second :
        string.Equals(first, second, StringComparison.Ordinal) ? first :
        string.IsNullOrWhiteSpace(second) ? first : first + Environment.NewLine + second;

    async Task ShowLogin(StoredAccount? account, string? server, string? error, CancellationToken cancellationToken = default)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            var targetServer = account?.Server ?? server;
            _nav.Go(new LoginViewModel(_nav, targetServer, error, this, adding: false, returnShell: null, login: account?.Tag));
        });
    }

    (int Generation, CancellationTokenSource Operation) StartOperation(ShellViewModel shell)
    {
        lock (_operationGate)
        {
            _operation?.Cancel();
            _operation = new CancellationTokenSource();
            var generation = ++_generation;
            shell.AccountOperationBusy = true;
            shell.Error = null;
            return (generation, _operation);
        }
    }

    bool IsCurrent(int generation)
    {
        lock (_operationGate) return generation == _generation && _operation?.IsCancellationRequested == false;
    }

    void CompleteOperation(int generation, ShellViewModel shell, CancellationTokenSource operation)
    {
        lock (_operationGate)
        {
            if (generation == _generation)
            {
                shell.AccountOperationBusy = false;
                _operation = null;
            }
            operation.Dispose();
        }
    }

    void CancelOperation()
    {
        lock (_operationGate)
        {
            ++_generation;
            CancelOperationWithoutDisposing(_operation);
            _operation = null;
            if (CurrentShell is { } shell) shell.AccountOperationBusy = false;
        }
    }

    void CancelRestore()
    {
        lock (_operationGate)
        {
            _restoreOperation?.Cancel();
            _restoreOperation = null;
        }
    }

    static List<StoredAccount> OrderedAccounts(AccountData data)
    {
        if (data.ActiveAccount is not { } active) return data.Accounts.ToList();
        return data.Accounts.OrderByDescending(account => account.Identity == active).ToList();
    }

    internal static bool IsNetworkError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException;
}
