using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WorkspaceTitle))]
    bool _profileEditing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDraftAvatar))]
    Bitmap? _draftAvatar;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    bool _profilePhotoLoading;
    public bool HasDraftAvatar => DraftAvatar != null;
    public string AccountName => _me.DisplayName;
    (byte[] Bytes, string Name)? _profilePhoto;
    int _profilePhotoGeneration;

    public void ResetProfileDraft()
    {
        ++_profilePhotoGeneration;
        ProfilePhotoLoading = false;
        _profilePhoto = null;
        SetDraftAvatar(null);
        ProfileEditing = false;
        DisplayName = _me.DisplayName;
        Bio = _me.Bio ?? "";
        AllowTagSearch = _me.AllowTagSearch;
        Error = null;
    }

    void SetDraftAvatar(Bitmap? image)
    {
        var previous = DraftAvatar;
        DraftAvatar = image;
        previous?.Dispose();
    }

    /// <summary>Профиль: имя, bio и доступность поиска по тегу.</summary>
    [RelayCommand]
    async Task ShowProfile()
    {
        ResetProfileDraft();
        NavigateWorkspace(WorkspacePage.Account);
        try
        {
            var me = await _api.GetMe();
            await Ui(() =>
            {
                _me = me;
                OnPropertyChanged(nameof(AccountName));
                if (!HasWorkspacePage(WorkspacePage.Account) || ProfileEditing) return;
                _names[me.Id] = me.DisplayName;
                Header = $"{me.DisplayName}  @{me.Tag}";
                DisplayName = me.DisplayName;
                Bio = me.Bio ?? "";
                AllowTagSearch = me.AllowTagSearch;
            });
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    bool CanSaveProfile() => !ProfilePhotoLoading;

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    async Task SaveProfile()
    {
        if (!ProfileEditing) return;
        var name = DisplayName.Trim();
        if (name.Length is < 1 or > 32) { Error = "Имя: 1–32 символа"; return; }
        if (Bio.Length > 500) { Error = "О себе длиннее 500 символов"; return; }
        try
        {
            Error = null;
            var bio = Bio;
            var photo = _profilePhoto;
            if (photo is { } image)
                await _api.UpdateProfileWithAvatar(name, bio, image.Bytes, image.Name);
            else
                await _api.UpdateProfile(name, bio);
            _me.DisplayName = name;
            OnPropertyChanged(nameof(AccountName));
            _me.Bio = bio;
            _names[_me.Id] = name;
            Header = $"{name}  @{_me.Tag}";
            _accounts.UpdateCurrentProfile(_api.ServerBase, _me.Id, _me.Tag, name);
            if (photo is { } saved)
            {
                InvalidateAvatar(_me.Id);
                RefreshAvatar(_me.Id, saved.Bytes);
            }
            ResetProfileDraft();
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    internal async Task StageAvatar(string filePath)
    {
        if (_closed || !ProfileEditing || SaveProfileCommand.IsRunning) return;
        var generation = ++_profilePhotoGeneration;
        ProfilePhotoLoading = true;
        try
        {
            Error = null;
            await using var file = File.OpenRead(filePath);
            if (file.Length is 0 or > 5 * 1024 * 1024)
            {
                Error = "Выберите изображение размером до 5 МБ";
                return;
            }
            var bytes = new byte[file.Length];
            await file.ReadExactlyAsync(bytes);
            if (_closed || generation != _profilePhotoGeneration || !ProfileEditing || SaveProfileCommand.IsRunning) return;
            using var stream = new MemoryStream(bytes, writable: false);
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec == null || codec.EncodedFormat is not (SkiaSharp.SKEncodedImageFormat.Png or
                SkiaSharp.SKEncodedImageFormat.Jpeg or SkiaSharp.SKEncodedImageFormat.Webp))
            {
                Error = "Выберите изображение в формате PNG, JPEG или WebP";
                return;
            }
            if ((long)codec.Info.Width * codec.Info.Height > 32_000_000)
            {
                Error = "Выберите изображение с меньшим разрешением";
                return;
            }
            stream.Position = 0;
            var preview = Bitmap.DecodeToWidth(stream, Math.Min(160, codec.Info.Width));
            _profilePhoto = (bytes, Path.GetFileName(filePath));
            SetDraftAvatar(preview);
        }
        catch (Exception) when (generation != _profilePhotoGeneration) { }
        catch (Exception) { Error = "Не удалось открыть изображение. Выберите PNG, JPEG или WebP."; }
        finally
        {
            if (generation == _profilePhotoGeneration) ProfilePhotoLoading = false;
        }
    }
}
