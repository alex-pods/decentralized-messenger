using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace MessengerDesktop;

public sealed partial class ImageComposerDraft : ObservableObject, IDisposable
{
    const int MaxImages = 10;
    const int MaxImageBytes = 10 * 1024 * 1024;
    readonly ObservableCollection<SelectedImage> _images = new();
    CancellationTokenSource? _selection;
    bool _disposed;
    ImageSendAttempt? _attempt;

    internal ImageComposerDraft(long chatId, string draft, long? replyToId)
    {
        ChatId = chatId;
        OriginalDraft = draft;
        ReplyToId = replyToId;
        _caption = draft;
        Images = new(_images);
    }

    internal long ChatId { get; }
    internal string OriginalDraft { get; }
    internal long? ReplyToId { get; }
    internal ImageSendAttempt? Attempt
    {
        get => _attempt;
        set
        {
            _attempt = value;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanChoose));
            OnPropertyChanged(nameof(SendLabel));
        }
    }

    public ReadOnlyObservableCollection<SelectedImage> Images { get; }
    public bool HasImages => _images.Count > 0;
    public string SelectionCount => $"Выбрано: {_images.Count} из {MaxImages}";
    public string ChooseLabel => HasImages ? "Добавить ещё" : "Выбрать фото";
    public string SendLabel => IsSending ? "Отправляем…" : Attempt != null ? "Повторить отправку" : "Отправить";
    public bool CanEdit => !_disposed && !IsSelecting && !IsSending && Attempt == null;
    public bool CanChoose => CanEdit && _images.Count < MaxImages;
    public bool CaptionTooLong => (Caption?.Trim().Length ?? 0) > 4096;
    public bool CanSend => !_disposed && !IsSelecting && !IsSending && HasImages && !CaptionTooLong;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSend), nameof(CaptionTooLong))]
    string _caption;

    [ObservableProperty] string? _error;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanChoose), nameof(CanSend))]
    bool _isSelecting;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanChoose), nameof(CanSend), nameof(SendLabel))]
    bool _isSending;

    public async Task ChooseImages(Func<Task<IReadOnlyList<IStorageFile>>> openPicker)
    {
        if (!CanChoose) return;
        using var selection = new CancellationTokenSource();
        _selection = selection;
        IReadOnlyList<IStorageFile> files = [];
        IsSelecting = true;
        Error = null;
        try
        {
            files = await openPicker();
            var errors = new List<string>();
            foreach (var file in files)
            {
                if (selection.IsCancellationRequested) return;
                if (_images.Count == MaxImages)
                {
                    errors.Add("В одно сообщение можно добавить до 10 фото.");
                    break;
                }

                try
                {
                    var image = await ReadImage(file, selection.Token);
                    if (selection.IsCancellationRequested)
                    {
                        image.Dispose();
                        return;
                    }
                    _images.Add(image);
                    NotifySelection();
                }
                catch (InvalidDataException ex) { errors.Add($"«{file.Name}»: {ex.Message}"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"Не удалось прочитать «{file.Name}».");
                }
            }
            if (!selection.IsCancellationRequested && errors.Count > 0)
                Error = errors.Count == 1 ? errors[0] : $"{errors[0]} Не добавлено файлов: {errors.Count}.";
        }
        catch (OperationCanceledException) when (selection.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!selection.IsCancellationRequested) Error = "Не удалось выбрать фото. Попробуйте снова.";
        }
        finally
        {
            foreach (var file in files) file.Dispose();
            _selection = null;
            IsSelecting = false;
        }
    }

    [RelayCommand]
    void RemoveImage(SelectedImage? image)
    {
        if (!CanEdit || image == null || !_images.Remove(image)) return;
        image.Dispose();
        NotifySelection();
    }

    void NotifySelection()
    {
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(ChooseLabel));
        OnPropertyChanged(nameof(CanChoose));
        OnPropertyChanged(nameof(CanSend));
    }

    static async Task<SelectedImage> ReadImage(IStorageFile file, CancellationToken ct)
    {
        var name = Path.GetFileName(file.Name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            throw new InvalidDataException("Недопустимое имя файла.");

        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > MaxImageBytes)
            throw new InvalidDataException("Размер файла превышает 10 МБ.");

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes)
                throw new InvalidDataException("Размер файла превышает 10 МБ.");
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        return await Task.Run(() =>
        {
            Bitmap preview;
            SKEncodedImageFormat format;
            try { preview = ImagePreviewItem.Decode(bytes, 320, out format); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException("Не удалось открыть изображение или оно превышает 32 мегапикселя.", ex);
            }

            var contentType = format switch
            {
                SKEncodedImageFormat.Jpeg => "image/jpeg",
                SKEncodedImageFormat.Png => "image/png",
                SKEncodedImageFormat.Webp => "image/webp",
                SKEncodedImageFormat.Gif => "image/gif",
                SKEncodedImageFormat.Bmp => "image/bmp",
                _ => null
            };
            if (contentType != null) return new SelectedImage(name, contentType, bytes, preview);
            preview.Dispose();
            throw new InvalidDataException("Поддерживаются JPG, PNG, WebP, GIF и BMP.");
        }, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selection?.Cancel();
        var images = _images.ToArray();
        _images.Clear();
        foreach (var image in images) image.Dispose();
    }
}

public sealed class SelectedImage(string name, string contentType, byte[] bytes, Bitmap preview) : IDisposable
{
    public string Name { get; } = name;
    public Bitmap Preview { get; } = preview;
    internal (string FileName, string ContentType, byte[] Bytes) Upload { get; } = (name, contentType, bytes);
    public void Dispose() => Preview.Dispose();
}

internal sealed class ImageSendAttempt(string text)
{
    public string Text { get; } = text;
    public string ClientRef { get; } = Guid.NewGuid().ToString("N");
    public long? MessageId { get; set; }
}
