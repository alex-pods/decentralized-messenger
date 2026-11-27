using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using MessengerDesktop.Settings;

namespace MessengerDesktop.Views.Dialogs;

public partial class ProfileEditor : UserControl
{
    ShellViewModel? _profileVm;

    public ProfileEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        AttachedToVisualTree += (_, _) => Hook();
        DetachedFromVisualTree += (_, _) =>
        {
            if (_profileVm != null) _profileVm.PropertyChanged -= ProfileChanged;
            _profileVm = null;
        };
    }

    void Hook()
    {
        if (_profileVm != null) _profileVm.PropertyChanged -= ProfileChanged;
        _profileVm = DataContext as ShellViewModel;
        if (_profileVm != null) _profileVm.PropertyChanged += ProfileChanged;
        RefreshSummaries();
    }

    void ProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Workspace)) TagCopied.IsVisible = false;
        if (e.PropertyName is nameof(ShellViewModel.Workspace) or nameof(ShellViewModel.AllowTagSearch)) RefreshSummaries();
    }

    void RefreshSummaries()
    {
        PrivacySummary.Text = _profileVm?.AllowTagSearch == true ? "Тег виден в поиске всем" : "Тег находят только знакомые";
        var key = AppPreferences.Current.SendMode == SendMode.Enter ? "Enter" : OperatingSystem.IsMacOS() ? "⌘ + Enter" : "Ctrl + Enter";
        MessagesSummary.Text = key;
        ThemeSummary.Text = Appearance.ThemeSettings.Current.Name;
    }

    void OnEditProfile(object? sender, RoutedEventArgs e)
    {
        if (_profileVm != null) _profileVm.ProfileEditing = true;
        Dispatcher.UIThread.Post(() => ProfileName.Focus(), DispatcherPriority.Loaded);
    }

    void OnCancelEdit(object? sender, RoutedEventArgs e)
    {
        _profileVm?.ResetProfileDraft();
        EditProfile.Focus();
    }

    async void OnCopyTag(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_profileVm != null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_profileVm.UserTag);
                TagCopied.IsVisible = true;
            }
        }
        catch { if (_profileVm != null) _profileVm.Error = "Не удалось скопировать тег"; }
    }

    void OnPreferencesClick(object? sender, RoutedEventArgs e)
    {
        _profileVm?.NavigateWorkspace(WorkspacePage.Settings, section: (sender as Button)?.Tag as string);
    }

    void OnAppearanceClick(object? sender, RoutedEventArgs e) =>
        _profileVm?.NavigateWorkspace(WorkspacePage.Appearance);

    void OnSwitchAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor && this.FindAncestorOfType<MessengerDesktop.Views.ShellView>() is { } shell)
        {
            shell.OpenAccountMenu(anchor);
            e.Handled = true;
        }
    }

    async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (_profileVm is { } vm && await ConfirmAction.Show(this, "Выйти из аккаунта?", "Для возвращения понадобятся логин и пароль от этого сервера.", "Выйти"))
            await vm.LogoutCommand.ExecuteAsync(null);
    }
    async void OnChooseAvatarClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите изображение для аватара",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Изображения")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"]
                }
            ]
        });
        if (files.Count > 0)
            await UploadAvatarFromPath(files[0].Path.LocalPath);
    }

    void OnAvatarDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _profileVm?.ProfileEditing == true && e.Data.GetFiles()?.Any(file => file.Path.IsFile) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnAvatarDrop(object? sender, DragEventArgs e)
    {
        var file = e.Data.GetFiles()?.FirstOrDefault(item => item.Path.IsFile);
        if (file != null)
            await UploadAvatarFromPath(file.Path.LocalPath);
        e.Handled = true;
    }

    Task UploadAvatarFromPath(string path) =>
        _profileVm?.StageAvatar(path) ?? Task.CompletedTask;
}
