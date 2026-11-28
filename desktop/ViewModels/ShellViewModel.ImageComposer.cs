using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    internal const string ImageComposerOverlay = "image-composer";
    readonly Dictionary<long, ImageComposerDraft> _pendingImageSends = new();

    [ObservableProperty] ImageComposerDraft? _imageComposer;

    public bool CanOpenImageComposer => HasChat && Editing == null && !Busy && !AntispamRequired;

    [RelayCommand]
    void OpenImageComposer()
    {
        if (!CanOpenImageComposer || OverlayOpen || SelectedChat is not { } chat) return;

        ImageComposer = _pendingImageSends.GetValueOrDefault(chat.Id) ?? new(chat.Id, Draft, ReplyTo?.Id);
        Error = null;
        Overlay = ImageComposerOverlay;
    }

    void CloseImageComposer()
    {
        var composer = ImageComposer;
        ImageComposer = null;
        if (composer != null && !_pendingImageSends.ContainsKey(composer.ChatId))
            composer.Dispose();
    }

    [RelayCommand]
    async Task SendImages()
    {
        if (ImageComposer is not { CanSend: true } composer || SelectedChat is not { } chat ||
            chat.Id != composer.ChatId || Busy || _api.AntispamBlocked) return;

        var firstAttempt = composer.Attempt == null;
        var caption = composer.Caption?.Trim();
        var attempt = composer.Attempt ??= new(string.IsNullOrEmpty(caption) ? "Фото" : caption);
        var images = composer.Images.Select(image => image.Upload).ToArray();
        _pendingImageSends[chat.Id] = composer;
        composer.Error = null;
        composer.IsSending = true;
        var sent = false;
        try
        {
            if (attempt.MessageId is { } previousId)
            {
                // A successful upload may have lost its response. Check before repeating it.
                var page = previousId == 1
                    ? await _api.GetMessages(chat.Id, 1, beforeId: 2)
                    : await _api.GetMessages(chat.Id, 1, afterId: previousId - 1);
                var stored = page.FirstOrDefault(message => message.Id == previousId);
                if (stored is not { ContentState: "stored" })
                {
                    composer.Attempt = null;
                    throw new InvalidOperationException("Исходное сообщение больше недоступно. Фото можно отправить заново.");
                }
                if (stored.Attachments is { Count: > 0 } attachments)
                {
                    if (!MatchesUpload(attachments, previousId, images))
                        throw new InvalidOperationException("На сервере уже есть другой набор вложений к этому сообщению.");
                    sent = true;
                }
            }
            else
                attempt.MessageId = await _api.SendMessage(chat.Id, attempt.Text, composer.ReplyToId, attempt.ClientRef);

            if (!sent)
            {
                var messageId = attempt.MessageId!.Value;
                var uploaded = await _api.UploadImages(messageId, images);
                if (!MatchesUpload(uploaded, messageId, images))
                    throw new InvalidOperationException("Сервер не подтвердил загрузку всех фото.");
                sent = true;
            }
        }
        catch (Exception ex)
        {
            if (firstAttempt && attempt.MessageId == null &&
                ex is ApiException { Status: >= 400 and < 500 and not 408, Code: not "antispam_required" })
                composer.Attempt = null;
            composer.Error = attempt.MessageId != null && composer.Attempt != null
                ? $"Сообщение создано. Не удалось подтвердить отправку фото. {Friendly(ex)}"
                : Friendly(ex);

            if (ReferenceEquals(ImageComposer, composer) && ex is ApiException { Code: "antispam_required" })
                CloseOverlay();
        }
        finally
        {
            composer.IsSending = false;
            if (sent || composer.Attempt == null)
            {
                _pendingImageSends.Remove(chat.Id);
                if (!ReferenceEquals(ImageComposer, composer)) composer.Dispose();
            }
        }

        if (!sent || _closed) return;
        if (SelectedChat?.Id != chat.Id || Editing == null && Draft == composer.OriginalDraft && ReplyTo?.Id == composer.ReplyToId)
            CompleteCompose(chat.Id, composer.OriginalDraft, editing: false);
        if (ReferenceEquals(ImageComposer, composer)) CloseOverlay();
        if (SelectedChat is { } current && current.Id == chat.Id)
            await MergeAfter(current, attempt.MessageId!.Value - 1);
        else await ReloadChats();
    }

    static bool MatchesUpload(
        IReadOnlyList<AttachmentDto> attachments, long messageId,
        IReadOnlyList<(string FileName, string ContentType, byte[] Bytes)> images) =>
        attachments.Count == images.Count && attachments.Zip(images).All(pair =>
            pair.First.MessageId == messageId &&
            pair.First.FileName == pair.Second.FileName &&
            pair.First.FileType == pair.Second.ContentType &&
            pair.First.FileSizeBytes == pair.Second.Bytes.LongLength);
}
