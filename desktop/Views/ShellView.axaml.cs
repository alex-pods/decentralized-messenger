using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace MessengerDesktop.Views;

public partial class ShellView : UserControl
{
    ShellViewModel? _vm;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
    }

    void Hook()
    {
        if (_vm != null) _vm.ScrollToEnd -= Scroll;
        _vm = DataContext as ShellViewModel;
        if (_vm != null) _vm.ScrollToEnd += Scroll;
    }

    void Scroll()
    {
        if (_vm == null || _vm.Messages.Count == 0) return;
        if (this.FindControl<ListBox>("Log") is { } list)
            list.ScrollIntoView(_vm.Messages[^1]);
    }

    void OnDragAreaPressed(object? sender, PointerPressedEventArgs e) => MainWindow.DragFrom(this, e);

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
        _vm?.UploadAvatarAsync(path) ?? Task.CompletedTask;
}
