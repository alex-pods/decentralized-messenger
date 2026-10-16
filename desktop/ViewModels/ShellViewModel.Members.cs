using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    [RelayCommand]
    async Task ShowMembers()
    {
        if (SelectedChat == null) return;
        ResetSearch();
        Overlay = "members";
        await LoadMembersPanel();
    }

    async Task LoadMembersPanel()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        var members = await _api.GetMembers(chat.Id);
        var myRole = members.FirstOrDefault(m => m.Id == _me.Id)?.Role ?? "";
        _myRole = myRole;
        var owner = myRole == "owner";
        var staff = myRole is "owner" or "admin";
        await Ui(() =>
        {
            DisposeItems(Members);
            foreach (var m in members)
            {
                _names[m.Id] = m.DisplayName;
                Members.Add(new MemberRow
                {
                    Id = m.Id,
                    Title = $"{m.DisplayName}  @{m.Tag}",
                    Role = m.Role,
                    RoleLabel = m.Role switch { "owner" => "владелец", "admin" => "админ", _ => "участник" },
                    PromoteLabel = m.Role == "admin" ? "Снять админа" : "Сделать админом",
                    CanKick = staff && m.Id != _me.Id && m.Role != "owner" && (m.Role != "admin" || owner),
                    CanPromote = owner && m.Id != _me.Id && m.Role != "owner",
                    CanTransfer = owner && m.Id != _me.Id
                });
                _ = LoadAvatar(m.Id, image =>
                {
                    var row = Members.FirstOrDefault(x => x.Id == m.Id);
                    if (row != null) row.Avatar = CreateAvatar(image);
                });
            }
            OnPropertyChanged(nameof(CanDeleteEveryone));
        });
    }

    [RelayCommand]
    async Task Kick(MemberRow row)
    {
        if (SelectedChat == null) return;
        try { await _api.RemoveMember(SelectedChat.Id, row.Id); await LoadMembersPanel(); }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    [RelayCommand]
    async Task ToggleAdmin(MemberRow row)
    {
        if (SelectedChat == null) return;
        try
        {
            await _api.SetRole(SelectedChat.Id, row.Id, row.Role == "admin" ? "member" : "admin");
            await LoadMembersPanel();
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    [RelayCommand]
    async Task Transfer(MemberRow row)
    {
        if (SelectedChat == null) return;
        try { await _api.TransferOwner(SelectedChat.Id, row.Id); await LoadMembersPanel(); }
        catch (Exception ex) { Error = Friendly(ex); }
    }
}
