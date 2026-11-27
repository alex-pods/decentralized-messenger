using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    public ObservableCollection<ImagePreviewItem> ImagePreviews { get; } = new();
    CancellationTokenSource? _imageLoad;
    long? _imageMessageId;

    [RelayCommand]
    void ShowImages(MessageItem? message)
    {
        if (message?.HasImages != true || !Messages.Contains(message)) return;
        ClearImagePreviews();
        Error = null;
        Overlay = "images";
        _imageMessageId = message.Id;
        _imageLoad = new CancellationTokenSource();
        var items = message.Images.Select(a => new ImagePreviewItem(a)).ToArray();
        foreach (var item in items) ImagePreviews.Add(item);
        _ = LoadImagePreviews(items, _imageLoad.Token);
    }

    async Task LoadImagePreviews(IEnumerable<ImagePreviewItem> items, CancellationToken ct)
    {
        // Decode one at a time; do not retain the full-size files after rendering.
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var bytes = await _api.GetAttachment(item.Attachment.Id, ct);
                var bitmap = await Task.Run(() => ImagePreviewItem.Decode(bytes), ct);
                if (ct.IsCancellationRequested) { bitmap.Dispose(); return; }
                item.Bitmap = bitmap;
                item.Status = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception)
            {
                if (ct.IsCancellationRequested) return;
                item.Status = "Не удалось открыть картинку. Закройте панель и попробуйте снова.";
            }
        }
    }

    void ClearImagePreviews()
    {
        _imageLoad?.Cancel();
        _imageLoad?.Dispose();
        _imageLoad = null;
        _imageMessageId = null;
        DisposeItems(ImagePreviews);
    }

    void AttachmentAdded(WsEventDto e)
    {
        if (e.ChatId != SelectedChat?.Id || e.Attachment is not { IsImage: true } attachment ||
            e.MessageId != attachment.MessageId) return;
        var message = Messages.FirstOrDefault(m => m.Id == attachment.MessageId);
        if (message == null || message.IsDeleted || message.IsNotStored || message.Images.Any(a => a.Id == attachment.Id)) return;
        message.Images = [.. message.Images, attachment];
        if (_imageMessageId == message.Id && _imageLoad != null)
        {
            var item = new ImagePreviewItem(attachment);
            ImagePreviews.Add(item);
            _ = LoadImagePreviews([item], _imageLoad.Token);
        }
    }
}

public partial class ImagePreviewItem(AttachmentDto attachment) : ObservableObject, IDisposable
{
    public AttachmentDto Attachment { get; } = attachment;
    public string Name => string.IsNullOrWhiteSpace(Attachment.FileName) ? "Картинка" : Attachment.FileName;
    [ObservableProperty] private Bitmap? _bitmap;
    [ObservableProperty] private string? _status = "Загрузка картинки…";

    internal static Bitmap Decode(byte[] bytes, int maxDimension = 1200)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            (long)codec.Info.Width * codec.Info.Height > 32_000_000)
            throw new InvalidDataException("Изображение повреждено или превышает 32 мегапикселя");
        using var stream = new MemoryStream(bytes);
        return codec.Info.Width >= codec.Info.Height
            ? Bitmap.DecodeToWidth(stream, Math.Min(codec.Info.Width, maxDimension))
            : Bitmap.DecodeToHeight(stream, Math.Min(codec.Info.Height, maxDimension));
    }

    public void Dispose()
    {
        var old = Bitmap;
        Bitmap = null;
        old?.Dispose();
    }
}
