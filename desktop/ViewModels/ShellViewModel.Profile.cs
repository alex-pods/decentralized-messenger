using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    /// <summary>Профиль: имя и bio. Настройки save_history в v4 удалены.</summary>
    [RelayCommand]
    async Task ShowProfile()
    {
        Overlay = "profile";
        try
        {
            var me = await _api.GetMe();
            await Ui(() =>
            {
                _me = me;
                _names[me.Id] = me.DisplayName;
                Header = $"{me.DisplayName}  @{me.Tag}";
                DisplayName = me.DisplayName;
                Bio = me.Bio ?? "";
            });
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    [RelayCommand]
    async Task SaveProfile()
    {
        var name = DisplayName.Trim();
        if (name.Length is < 1 or > 32) { Error = "Имя: 1–32 символа"; return; }
        if (Bio.Length > 500) { Error = "О себе длиннее 500 символов"; return; }
        try
        {
            Error = null;
            await _api.UpdateProfile(name, Bio);
            var me = await _api.GetMe();
            await Ui(() =>
            {
                _me = me;
                _names[me.Id] = me.DisplayName;
                Header = $"{me.DisplayName}  @{me.Tag}";
                Overlay = "";
            });
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    public async Task UploadAvatarAsync(string filePath)
    {
        try
        {
            Error = null;
            var file = new FileInfo(filePath);
            if (!file.Exists) { Error = "Файл не найден"; return; }
            if (file.Length > 5_000_000) { Error = "Аватар не должен быть больше 5 МБ"; return; }
            await _api.UploadAvatar(filePath);
            InvalidateAvatar(_me.Id);
            var me = await _api.GetMe();
            await Ui(() =>
            {
                _me = me;
            });
            await LoadAvatar(me.Id, bytes => RefreshAvatar(me.Id, bytes));
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }
}
