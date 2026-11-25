using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
        if (e.PropertyName == nameof(ShellViewModel.Overlay) && _profileVm?.Overlay == "profile")
        {
            AccountPanel.IsVisible = true;
            EditPanel.IsVisible = false;
        }
        if (e.PropertyName is nameof(ShellViewModel.Overlay) or nameof(ShellViewModel.AllowTagSearch)) RefreshSummaries();
    }

    void RefreshSummaries()
    {
        PrivacySummary.Text = _profileVm?.AllowTagSearch == true ? "Вас могут найти по тегу" : "Поиск по вашему тегу выключен";
        var key = AppPreferences.Current.SendMode == SendMode.Enter ? "Enter" : OperatingSystem.IsMacOS() ? "⌘ + Enter" : "Ctrl + Enter";
        MessagesSummary.Text = "Отправка: " + key + " · быстрые действия";
        ThemeSummary.Text = Appearance.ThemeSettings.Current.Name + " · цвета и обмен темами";
    }

    void OnEditProfile(object? sender, RoutedEventArgs e)
    {
        AccountPanel.IsVisible = false;
        EditPanel.IsVisible = true;
        Dispatcher.UIThread.Post(() => ProfileName.Focus(), DispatcherPriority.Loaded);
    }

    void OnCancelEdit(object? sender, RoutedEventArgs e)
    {
        _profileVm?.ResetProfileDraft();
        EditPanel.IsVisible = false;
        AccountPanel.IsVisible = true;
        EditProfile.Focus();
    }

    async void OnCopyTag(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_profileVm != null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(_profileVm.UserTag);
        }
        catch { if (_profileVm != null) _profileVm.Error = "Не удалось скопировать тег"; }
    }

    void OnPreferencesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel vm && TopLevel.GetTopLevel(this) is Window owner)
            SettingsWindow.Open(vm, owner, (sender as Button)?.Tag as string, RefreshSummaries);
    }

    async void OnAppearanceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel vm && TopLevel.GetTopLevel(this) is Window owner)
            await new Appearance.AppearanceWindow(vm.ThemeApi, vm.CurrentUserId).ShowDialog(owner);
        RefreshSummaries();
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
        e.DragEffects = e.Data.GetFiles()?.Any(file => file.Path.IsFile) == true
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
        (DataContext as ShellViewModel)?.UploadAvatarAsync(path) ?? Task.CompletedTask;
}
