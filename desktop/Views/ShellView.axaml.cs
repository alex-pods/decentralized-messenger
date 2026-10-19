using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace MessengerDesktop.Views;

public partial class ShellView : UserControl
{
    ShellViewModel? _vm;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        this.FindControl<ListBox>("Log")?.AddHandler(InputElement.PointerPressedEvent, OnLogPressed, RoutingStrategies.Tunnel);
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

    void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: ReactionChip chip } && DataContext is ShellViewModel vm)
            vm.ToggleChipCommand.Execute(chip);
        e.Handled = true;   // ListBox не увидит нажатие и не выберет сообщение
    }
    
    void OnLogPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Control src) return;
        if (src.FindAncestorOfType<ScrollBar>() != null) return;

        bool isBubble = false;
        for (Control? v = src; v != null; v = v.GetVisualParent() as Control)
        {
            if (v is Border b && b.Classes.Contains("bubble")) 
            { 
                isBubble = true; 
                break; 
            }
            if (v is ListBoxItem) break; // дошли до строки, пузырь не найден
        }

        if (isBubble) return;

        if (DataContext is ShellViewModel vm)
            vm.SelectedMessage = null;

        e.Handled = true;
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
        _vm?.UploadAvatarAsync(path) ?? Task.CompletedTask;
}
