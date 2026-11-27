using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace MessengerDesktop;

/// <summary>Loads a thumbnail only while its message is attached to the view.</summary>
public sealed class MessageImagePreview : Button
{
    readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), FontSize = 12 };
    readonly Border _more = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
        IsVisible = false,
        Child = new TextBlock { Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
    };
    CancellationTokenSource? _load;
    Bitmap? _bitmap;
    bool _attached;

    public MessageImagePreview()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Padding = new Thickness(0);
        MinWidth = MinHeight = 0;
        ClipToBounds = true;
        Content = new Grid { ClipToBounds = true, Children = { _image, _status, _more } };
        DataContextChanged += (_, _) => { if (_attached) Load(); };
        Click += (_, e) =>
        {
            var message = this.GetVisualAncestors().OfType<ItemsControl>()
                .Select(c => c.DataContext).OfType<MessageItem>().FirstOrDefault();
            var vm = this.FindAncestorOfType<Views.ShellView>()?.DataContext as ShellViewModel;
            if (message != null) vm?.ShowImagesCommand.Execute(message);
            e.Handled = true;
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Clear();
        base.OnDetachedFromVisualTree(e);
    }

    async void Load()
    {
        Clear();
        if (DataContext is not AttachmentDto attachment ||
            this.FindAncestorOfType<Views.ShellView>()?.DataContext is not ShellViewModel vm) return;
        var cancellation = _load = new CancellationTokenSource();
        var ct = cancellation.Token;
        var message = this.GetVisualAncestors().OfType<ItemsControl>()
            .Select(c => c.DataContext).OfType<MessageItem>().FirstOrDefault();
        var remaining = message != null && message.Images.Count > 4 && message.Images[3].Id == attachment.Id
            ? message.Images.Count - 4 : 0;
        _more.IsVisible = remaining > 0;
        ((TextBlock)_more.Child!).Text = "+" + remaining;
        _status.Text = "Загрузка картинки…";
        _status.IsVisible = true;
        Avalonia.Automation.AutomationProperties.SetName(this, "Открыть картинку: " + attachment.FileName);
        try
        {
            var bytes = await vm.ThemeApi.GetAttachment(attachment.Id, ct);
            var bitmap = await Task.Run(() => ImagePreviewItem.Decode(bytes, 480), ct);
            if (ct.IsCancellationRequested) { bitmap.Dispose(); return; }
            _image.Source = _bitmap = bitmap;
            _status.IsVisible = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested)
                _status.Text = "Картинка недоступна. Нажмите, чтобы открыть повторно.";
        }
    }

    void Clear()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
