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
    public AccountIdentity? ActiveAccount => AccountStore.Load().ActiveAccount;

    internal void Attach(ShellViewModel shell) => CurrentShell = shell;

    public void AddAccount(ShellViewModel shell)
    {
        CancelOperation();
        CurrentShell = shell;
        _nav.Go(new LoginViewModel(_nav, shell.ServerLabel, accounts: this, adding: true, returnShell: shell));
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
            catch (Exception ex) when (IsConnectionError(ex) || ex is ApiException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                await ShowLogin(null, serverOverride, LoginViewModel.Friendly(ex), restore.Token);
                return;
            }

            AccountData data;
            try { data = AccountStore.Load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                await ShowLogin(null, serverOverride, "Не удалось прочитать список аккаунтов: " + ex.Message, restore.Token);
                return;
            }

            var candidates = OrderedAccounts(data);
            if (!string.IsNullOrWhiteSpace(serverOverride))
                candidates = candidates.Where(account =>
                    string.Equals(account.Server, AccountStore.NormalizeServer(serverOverride), StringComparison.OrdinalIgnoreCase)).ToList();

            Exception? lastConnectionError = null;
            foreach (var account in candidates)
            {
                if (account.NeedsLogin) continue;
                MessengerClient? api = null;
                try
                {
                    restore.Token.ThrowIfCancellationRequested();
                    api = new MessengerClient(account.Server, account.Token);
                    var me = await api.GetMe(restore.Token);
                    if (await ActivateRestored(account, api, me, restore.Token))
                    {
                        api = null;
                        return;
                    }
                }
                catch (ApiException ex) when (ex.Status == 401)
                {
                    if (restore.IsCancellationRequested) return;
                    AccountStore.MarkNeedsLogin(account.Identity);
                }
                catch (OperationCanceledException) when (restore.IsCancellationRequested) { return; }
                catch (Exception ex) when (IsConnectionError(ex))
                {
                    lastConnectionError = ex;
                }
                finally { api?.Dispose(); }
            }

            if (restore.IsCancellationRequested) return;
            var reauth = candidates.FirstOrDefault(account =>
                             account.Identity != data.ActiveAccount && AccountStore.Find(account.Identity)?.NeedsLogin == true) ??
                         candidates.FirstOrDefault(account => account.Identity != data.ActiveAccount) ??
                         candidates.FirstOrDefault(account => account.NeedsLogin) ??
                         candidates.FirstOrDefault();
            if (reauth is not null)
                await ShowLogin(reauth, serverOverride, lastConnectionError is null ? null : LoginViewModel.Friendly(lastConnectionError), restore.Token);
            else
                await ShowLogin(null, serverOverride, lastConnectionError is null ? null : LoginViewModel.Friendly(lastConnectionError), restore.Token);
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
                AccountStore.AddOrUpdate(account with
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
            });
        }
        catch (ApiException ex) when (ex.Status == 401 && IsCurrent(generation))
        {
            var invalid = AccountStore.MarkNeedsLogin(account.Identity) ?? account with { NeedsLogin = true };
            await Dispatcher.UIThread.InvokeAsync(() => Reauthenticate(invalid, requester));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsConnectionError(ex) && IsCurrent(generation))
        {
            await Dispatcher.UIThread.InvokeAsync(() => requester.Error = LoginViewModel.Friendly(ex));
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
        try
        {
            previous = AccountStore.Find(identity);
            if (previous is null && AccountStore.Load().Accounts.Count >= AccountStore.MaxAccounts)
                throw new InvalidOperationException($"Можно добавить не более {AccountStore.MaxAccounts} аккаунтов");
            AccountStore.AddOrUpdate(new StoredAccount(server, me.Id, me.Tag, me.DisplayName, api.Token!));
        }
        catch (Exception ex)
        {
            try { await api.Logout(); }
            catch (Exception logoutError) when (IsConnectionError(logoutError) || logoutError is ApiException)
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
        string? warning = null;
        if (previous is not null && !string.Equals(previous.Token, api.Token, StringComparison.Ordinal))
        {
            try
            {
                using var oldApi = new MessengerClient(previous.Server, previous.Token);
                await oldApi.Logout();
            }
            catch (Exception ex)
            {
                warning = "Вход выполнен, но предыдущую сессию не удалось завершить: " + LoginViewModel.Friendly(ex);
            }
        }

        var shell = new ShellViewModel(_nav, api, me, this);
        CurrentShell = shell;
        _nav.Go(shell);
        if (warning is not null) shell.Error = warning;
    }

    public async Task LogoutAsync(ShellViewModel shell)
    {
        if (shell.IsClosed || shell.IsEndingAccount) return;
        StoredAccount? account;
        try { account = AccountStore.Find(AccountIdentity.Create(shell.ServerLabel, shell.CurrentUserId)); }
        catch (Exception ex)
        {
            shell.Error = "Не удалось прочитать список аккаунтов: " + ex.Message;
            return;
        }
        if (account is null) return;

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
            await ShowLogin(account, null, "Не удалось удалить аккаунт с устройства: " + ex.Message);
            return;
        }
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
        if (AccountStore.Find(identity) is { } saved)
            AccountStore.AddOrUpdate(saved with { Tag = tag, DisplayName = displayName });
    }

    async Task HandleSessionRevoked(ShellViewModel shell)
    {
        if (shell.IsClosed || shell.IsEndingAccount) return;
        CancelOperation();
        var identity = AccountIdentity.Create(shell.ServerLabel, shell.CurrentUserId);
        var fallback = new StoredAccount(
            shell.ServerLabel, shell.CurrentUserId, shell.UserTag.TrimStart('@'), shell.AccountName, "", true);
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
        shell.ReleaseResources();
        CurrentShell = null;

        var next = alternatives.FirstOrDefault(item => !item.NeedsLogin) ?? alternatives.FirstOrDefault();
        if (next is not null)
            await SwitchAfterClosing(next, null);
        else
            await ShowLogin(account, null, "Сессия завершена. Войдите снова.");
    }

    async Task SwitchAfterClosing(StoredAccount account, string? error, HashSet<AccountIdentity>? attempted = null)
    {
        attempted ??= new();
        attempted.Add(account.Identity);
        MessengerClient? api = null;
        try
        {
            api = new MessengerClient(account.Server, account.Token);
            var me = await api.GetMe();
            AccountStore.AddOrUpdate(account with { Tag = me.Tag, DisplayName = me.DisplayName, NeedsLogin = false });
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
            var next = AccountStore.Load().Accounts.FirstOrDefault(item => !attempted.Contains(item.Identity));
            if (next is not null)
                await SwitchAfterClosing(next, error, attempted);
            else
                await ShowLogin(invalid, null, error);
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            await ShowLogin(account, null, error ?? LoginViewModel.Friendly(ex));
        }
        catch (Exception ex)
        {
            await ShowLogin(account, null, error ?? LoginViewModel.Friendly(ex));
        }
        finally { api?.Dispose(); }
    }

    async Task<bool> ActivateRestored(StoredAccount account, MessengerClient api, ProfileDto me, CancellationToken ct)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return false;
            AccountStore.AddOrUpdate(account with { Tag = me.Tag, DisplayName = me.DisplayName, NeedsLogin = false });
            var shell = new ShellViewModel(_nav, api, me, this);
            CurrentShell = shell;
            _nav.Go(shell);
            return true;
        });
    }

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
            _operation?.Cancel();
            _operation?.Dispose();
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

    static bool IsConnectionError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or IOException;
}
