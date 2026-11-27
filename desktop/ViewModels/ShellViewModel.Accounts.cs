using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    public IReadOnlyList<StoredAccount> SavedAccounts => _accounts.Accounts;

    [RelayCommand]
    Task SwitchAccount(StoredAccount account) =>
        account.Identity == CurrentIdentity
            ? Task.CompletedTask
            : _accounts.SwitchToAsync(account, this);

    [RelayCommand]
    void AddAccount() => _accounts.AddAccount(this);
}
