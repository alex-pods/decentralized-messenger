using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    int _membersRequest;
    public bool CanInviteMembers => IsGroup && _myRole is "owner" or "admin";
    [RelayCommand]
    async Task ShowMembers()
    {
        if (SelectedChat == null) return;
        ResetSearch();
        NavigateWorkspace(WorkspacePage.Members);
        try { await LoadMembersPanel(); }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    async Task LoadMembersPanel()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        var request = ++_membersRequest;
        var members = await _api.GetMembers(chat.Id);
        if (request != _membersRequest || SelectedChat?.Id != chat.Id) return;
        var myRole = members.FirstOrDefault(m => m.Id == _me.Id)?.Role ?? "";
        var owner = myRole == "owner";
        var staff = myRole is "owner" or "admin";
        await Ui(() =>
        {
            if (request != _membersRequest || SelectedChat?.Id != chat.Id) return;
            _myRole = myRole;
            DisposeItems(Members);
            foreach (var m in members)
            {
                _names[m.Id] = m.DisplayName;
                Members.Add(new MemberRow
                {
                    Id = m.Id,
                    Title = string.IsNullOrWhiteSpace(m.DisplayName) ? "@" + m.Tag : m.DisplayName,
                    TagLabel = "@" + m.Tag,
                    Role = m.Role,
                    RoleLabel = (m.Id == _me.Id, m.Role) switch
                    {
                        (true, "owner") => "вы, владелец",
                        (true, "admin") => "вы, администратор",
                        (true, _) => "вы",
                        (_, "owner") => "владелец",
                        (_, "admin") => "администратор",
                        _ => ""
                    },
                    PromoteLabel = m.Role == "admin" ? "Снять права администратора" : "Назначить администратором",
                    CanKick = chat.IsGroup && staff && m.Id != _me.Id && m.Role != "owner" && (m.Role != "admin" || owner),
                    CanPromote = chat.IsGroup && owner && m.Id != _me.Id && m.Role != "owner",
                    CanTransfer = chat.IsGroup && owner && m.Id != _me.Id
                });
                _ = LoadAvatar(m.Id, image =>
                {
                    var row = Members.FirstOrDefault(x => x.Id == m.Id);
                    if (row != null) row.Avatar = CreateAvatar(image);
                });
            }
            OnPropertyChanged(nameof(CanDeleteEveryone));
            OnPropertyChanged(nameof(CanInviteMembers));
            OnPropertyChanged(nameof(CanManagePins));
            OnPropertyChanged(nameof(CanPin));
            RefreshPinList();
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
