using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    public IReadOnlyList<StoredAccount> SavedAccounts => _accounts.Accounts;

    [RelayCommand]
    Task SwitchAccount(StoredAccount account) =>
        account.Identity == AccountIdentity.Create(ServerLabel, CurrentUserId)
            ? Task.CompletedTask
            : _accounts.SwitchToAsync(account, this);

    [RelayCommand]
    void AddAccount() => _accounts.AddAccount(this);

    [RelayCommand]
    void ShowMyProfile() => ShowProfileCommand.Execute(null);
}
