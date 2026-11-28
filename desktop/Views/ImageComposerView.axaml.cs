using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace MessengerDesktop.Views;

public partial class ImageComposerView : UserControl
{
    static readonly FilePickerFileType PhotoFiles = new("Изображения")
    {
        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp"],
        MimeTypes = ["image/jpeg", "image/png", "image/webp", "image/gif", "image/bmp"]
    };

    public ImageComposerView() => InitializeComponent();

    internal bool FocusFirstAction() => ChooseImageFiles.Focus() || SendImageBatch.Focus();

    async void OnChooseImageFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { ImageComposer: { } composer }) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage?.CanOpen != true)
        {
            composer.Error = "Системное окно выбора файлов недоступно.";
            return;
        }

        await composer.ChooseImages(() => storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите фото для отправки",
            AllowMultiple = true,
            FileTypeFilter = [PhotoFiles]
        }));

        if (DataContext is ShellViewModel vm && ReferenceEquals(vm.ImageComposer, composer) && IsEffectivelyVisible)
        {
            if (composer.HasImages) ImageCaption.Focus();
            else FocusFirstAction();
        }
    }
}
