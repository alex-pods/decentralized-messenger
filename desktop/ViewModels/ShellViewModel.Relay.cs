using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Messenger.Identifiers;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    private const int MaxLiveMessages = 200;
    private readonly List<MessageDto> _liveMessages = [];
    private readonly HashSet<string> _relayDrafts = [];
    private readonly Dictionary<string, RelayAttempt> _relayAttempts = new();
    private sealed record RelayAttempt(string Id, string Text);

    public bool RelayAvailable => _api.RelaySupported;
    public bool RelayArmed => SelectedChat is { } chat && _relayDrafts.Contains(chat.Id);
    public bool CanToggleRelay => RelayAvailable && HasChat && !Busy &&
        (RelayArmed || (Editing == null && ReplyTo == null));
    public string ComposerWatermark => RelayArmed ? "Без хранения на сервере…" : "Напишите сообщение…";

    private void OnRelayCapabilityChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(RelayAvailable));
        OnPropertyChanged(nameof(CanToggleRelay));
    });

    private void NotifyRelayDraft()
    {
        OnPropertyChanged(nameof(RelayArmed));
        OnPropertyChanged(nameof(ComposerWatermark));
        OnPropertyChanged(nameof(CanToggleRelay));
    }

    [RelayCommand]
    private void ToggleRelay()
    {
        if (!CanToggleRelay || SelectedChat is not { } chat) return;
        if (!_relayDrafts.Remove(chat.Id)) _relayDrafts.Add(chat.Id);
        else _relayAttempts.Remove(chat.Id);
        NotifyRelayDraft();
    }

    private async Task SendRelay(ChatItem chat, string text, string submitted)
    {
        if (ReplyTo != null || Editing != null)
        {
            Error = "Закройте ответ или редактирование, чтобы отправить сообщение без хранения.";
            return;
        }

        if (!_relayAttempts.TryGetValue(chat.Id, out var attempt) || attempt.Text != text)
            _relayAttempts[chat.Id] = attempt = new(EntityIds.NewMessage(chat.Id), text);

        var receipt = await _api.RelayMessage(chat.Id, attempt.Id, text);
        if (receipt.MessageId != attempt.Id || receipt.Sequence <= 0 || receipt.QueuedConnections <= 0)
            throw new ApiException(502, "Сервер не подтвердил отправку.");

        _relayAttempts.Remove(chat.Id);
        var draftWasSent = SelectedChat?.Id == chat.Id ? Draft == submitted : _drafts.GetValueOrDefault(chat.Id) == submitted;
        CompleteCompose(chat.Id, submitted, editing: false);
        if (draftWasSent) _relayDrafts.Remove(chat.Id);
        NotifyRelayDraft();
        ReceiveRelay(chat.Id, new MessageDto
        {
            Id = receipt.MessageId, ChatId = chat.Id, SenderId = _me.Id, Sequence = receipt.Sequence,
            SentAt = receipt.SentAt, Text = text, ContentState = "relayed"
        });
    }

    private void ReceiveRelay(string chatId, MessageDto message)
    {
        if (_closed || message.ChatId != chatId || message.ContentState != "relayed" ||
            message.Sequence <= 0 || !EntityIds.IsMessageInChat(message.Id, chatId) ||
            _liveMessages.Any(item => item.Id == message.Id) ||
            Chats.FirstOrDefault(chat => chat.Id == chatId) is not { } target) return;

        _liveMessages.Add(message);
        if (_liveMessages.Count > MaxLiveMessages) DismissRelay(_liveMessages[0].Id);
        if (SelectedChat?.Id == chatId)
        {
            InsertInOrder(MessageMapper.ToItem(message, _me.Id, NameOf));
            if (Messages.LastOrDefault()?.Id == message.Id) RequestScroll();
        }
        else if (message.SenderId != _me.Id) target.Unread = true;
    }

    private void RestoreRelays(string chatId)
    {
        foreach (var message in _liveMessages.Where(item => item.ChatId == chatId))
            InsertInOrder(MessageMapper.ToItem(message, _me.Id, NameOf));
    }

    private void DismissRelay(string messageId)
    {
        _liveMessages.RemoveAll(item => item.Id == messageId);
        var item = Messages.FirstOrDefault(message => message.IsRelayed && message.Id == messageId);
        if (item == null) return;
        if (SelectedMessage == item) SelectedMessage = null;
        Messages.Remove(item);
    }

    private void ClearRelays(string chatId)
    {
        _liveMessages.RemoveAll(item => item.ChatId == chatId);
        if (_relayDrafts.Remove(chatId))
        {
            _drafts.Remove(chatId);
            if (SelectedChat?.Id == chatId) Draft = "";
        }
        _relayAttempts.Remove(chatId);
    }

    private MessageItem? LastStoredMessage() => Messages.LastOrDefault(message => !message.IsRelayed);
    private MessageItem? FirstStoredMessage() => Messages.FirstOrDefault(message => !message.IsRelayed);

    private void InsertInOrder(MessageItem item)
    {
        var index = Messages.Count;
        while (index > 0 && Messages[index - 1].Sequence > item.Sequence) index--;
        if (index > 0 && Messages[index - 1].Id == item.Id) return;
        Messages.Insert(index, item);
    }
}
