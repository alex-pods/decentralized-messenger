using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public sealed record PinListItem(long MessageId, string Text, bool Local, bool CanRemove,
    bool Mine, string? Sender, string? Time, long? Sequence = null, DateTimeOffset? SentAt = null)
{
    public string Scope => Local ? "На этом устройстве" : "Для всех";
    public string OpenLabel => "Перейти к сообщению: " + MessageMapper.Trim(Text, 120);
    public string RemoveLabel => Local ? "Открепить на этом устройстве" : "Открепить для всех";
    public string Attribution => string.Join(" · ", new[] { Mine ? "Вы" : Sender, Time }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public partial class ShellViewModel
{
    private readonly List<PinListItem> _stripPins = new();
    public ObservableCollection<PinListItem> PinList { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WorkspaceTitle))]
    private bool _showingPinnedMessages;

    public bool PinListEmpty => PinList.Count == 0;
    public bool CanPinLocally => HasChat && SelectedMessage is { IsDeleted: false, IsNotStored: false };
    public string LocalPinActionLabel => SelectedChat != null && SelectedMessage != null &&
        ReadLocalPins().Any(p => p.ChatId == SelectedChat.Id && p.MessageId == SelectedMessage.Id)
            ? "Открепить на устройстве" : "Закрепить на устройстве";

    List<LocalPinStore.Entry> ReadLocalPins()
    {
        try { return LocalPinStore.Load(_api.ServerBase, _me.Id); }
        catch (Exception ex)
        {
            Error = "Не удалось прочитать личные закрепления: " + ex.Message;
            return new();
        }
    }

    void RefreshPinList()
    {
        var previous = ShownPin?.MessageId;
        PinList.Clear();
        if (SelectedChat is { } chat)
        {
            var loaded = Messages.Where(message => !message.IsDeleted)
                .ToDictionary(message => message.Id);
            var shared = _pins.Select(p => MakePin(p.MessageId, p.Text, false, CanManagePins, p.SenderId, p.SentAt, p.Sequence));
            var local = ReadLocalPins().Where(p => p.ChatId == chat.Id)
                .Select(p =>
                {
                    loaded.TryGetValue(p.MessageId, out var message);
                    return MakePin(p.MessageId, p.Text, true, true,
                        p.SenderId ?? message?.SenderId, p.SentAt ?? message?.SentAt, message?.Sequence ?? p.Sequence);
                });
            foreach (var pin in shared.Concat(local).OrderByDescending(p => p.Sequence)
                         .ThenByDescending(p => p.SentAt)) PinList.Add(pin);
        }
        _stripPins.Clear();
        _stripPins.AddRange(PinList.DistinctBy(pin => pin.MessageId));
        var index = _stripPins.FindIndex(pin => pin.MessageId == previous);
        _pinIndex = SelectedChat == null || index < 0 ? 0 : index;
        NotifyPinStrip();
        OnPropertyChanged(nameof(PinListEmpty));
        OnPropertyChanged(nameof(LocalPinActionLabel));
    }

    PinListItem MakePin(long id, string text, bool local, bool canRemove, long? senderId, DateTimeOffset? sentAt, long? sequence)
    {
        var mine = senderId == _me.Id;
        var name = senderId is { } sender && !mine && IsGroup && _names.TryGetValue(sender, out var knownName)
            ? knownName : null;
        return new(id, text, local, canRemove, mine, name, sentAt?.ToLocalTime().ToString("dd.MM.yyyy · HH:mm"), sequence, sentAt);
    }

    // Old JSON bookmarks have no sequence. Resolve once by paging the visible history;
    // never guess order from an ID, and merge into the latest file to preserve user edits.
    async Task UpgradeLocalPinSequences(ChatItem chat, int generation)
    {
        var legacy = ReadLocalPins().Where(p => p.ChatId == chat.Id && p.Sequence == null)
            .Select(p => p.MessageId).ToHashSet();
        if (legacy.Count == 0) return;
        var known = Messages.ToDictionary(m => m.Id, m => m.Sequence);
        foreach (var pin in _pins) known[pin.MessageId] = pin.Sequence;
        long? before = null;
        while (legacy.Any(id => !known.ContainsKey(id)))
        {
            var page = await _api.GetMessages(chat.Id, 100, beforeSequence: before);
            if (!SameChat(generation, chat.Id)) return;
            foreach (var message in page) known[message.Id] = message.Sequence;
            if (page.Count < 100) break;
            before = page[0].Sequence;
        }
        await Ui(() =>
        {
            if (!SameChat(generation, chat.Id)) return;
            var pins = LocalPinStore.Load(_api.ServerBase, _me.Id);
            for (var i = 0; i < pins.Count; i++)
                if (pins[i].ChatId == chat.Id && pins[i].Sequence == null)
                    pins[i] = pins[i] with { Sequence = known.GetValueOrDefault(pins[i].MessageId) };
            LocalPinStore.Save(_api.ServerBase, _me.Id, pins);
            RefreshPinList();
        });
    }

    void UpdateLocalPin(long chatId, long messageId, string? text)
    {
        try
        {
            var pins = LocalPinStore.Load(_api.ServerBase, _me.Id);
            var index = pins.FindIndex(p => p.ChatId == chatId && p.MessageId == messageId);
            if (index < 0) return;
            if (text == null) pins.RemoveAt(index);
            else pins[index] = pins[index] with { Text = text };
            LocalPinStore.Save(_api.ServerBase, _me.Id, pins);
            RefreshPinList();
        }
        catch (Exception ex) { Error = "Не удалось обновить личные закрепы: " + ex.Message; }
    }

    [RelayCommand]
    void ShowPinList()
    {
        if (!HasChat) return;
        RefreshPinList();
        ShowingPinnedMessages = true;
    }

    [RelayCommand]
    void ClosePinList() => ShowingPinnedMessages = false;

    [RelayCommand]
    void ToggleLocalPin()
    {
        if (!CanPinLocally || SelectedChat is not { } chat || SelectedMessage is not { } message) return;
        try
        {
            // Читаем напрямую: при повреждённом файле нельзя затирать его пустым списком.
            var pins = LocalPinStore.Load(_api.ServerBase, _me.Id);
            var added = pins.RemoveAll(p => p.ChatId == chat.Id && p.MessageId == message.Id) == 0;
            if (added)
                pins.Add(new(chat.Id, message.Id, message.Text, message.SenderId, message.SentAt, message.Sequence));
            LocalPinStore.Save(_api.ServerBase, _me.Id, pins);
            RefreshPinList();
            if (added && _stripPins.FirstOrDefault()?.MessageId == message.Id)
            {
                _pinIndex = 0;
                NotifyPinStrip();
            }
        }
        catch (Exception ex) { Error = "Не удалось сохранить личный закреп: " + ex.Message; }
    }

    [RelayCommand]
    async Task OpenPinFromList(PinListItem pin)
    {
        if (SelectedChat is not { } chat) return;
        ResetWorkspace();
        ClosePinList();
        await JumpToMessage(chat, pin.MessageId, cyclePin: true);
    }

    [RelayCommand]
    async Task RemovePinFromList(PinListItem pin)
    {
        if (SelectedChat is not { } chat) return;
        try
        {
            if (pin.Local)
            {
                var pins = LocalPinStore.Load(_api.ServerBase, _me.Id);
                pins.RemoveAll(p => p.ChatId == chat.Id && p.MessageId == pin.MessageId);
                LocalPinStore.Save(_api.ServerBase, _me.Id, pins);
            }
            else
            {
                if (!CanManagePins) return;
                await _api.UnpinMessage(chat.Id, pin.MessageId);
                if (SelectedChat?.Id == chat.Id) RemovePin(pin.MessageId);
            }
            RefreshPinList();
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }
}
