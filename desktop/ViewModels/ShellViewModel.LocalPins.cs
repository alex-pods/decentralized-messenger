using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public sealed record PinListItem(long MessageId, string Text, bool Local, bool CanRemove = true)
{
    public string Scope => Local ? "Только для меня · на этом устройстве" : "Для всех";
}

public partial class ShellViewModel
{
    private readonly List<PinListItem> _stripPins = new();
    public ObservableCollection<PinListItem> PinList { get; } = new();
    public bool PinListEmpty => PinList.Count == 0;
    public bool CanPinLocally => HasChat && SelectedMessage is { IsDeleted: false, IsNotStored: false };
    public string LocalPinActionLabel => SelectedChat != null && SelectedMessage != null &&
        ReadLocalPins().Any(p => p.ChatId == SelectedChat.Id && p.MessageId == SelectedMessage.Id)
            ? "Открепить у себя" : "Закрепить для себя";

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
            var shared = _pins.Select(p => new PinListItem(p.MessageId, p.Text, false, CanManagePins));
            var local = ReadLocalPins().Where(p => p.ChatId == chat.Id)
                .Select(p => new PinListItem(p.MessageId, p.Text, true));
            foreach (var pin in shared.Concat(local).OrderByDescending(p => p.MessageId)) PinList.Add(pin);
        }
        _stripPins.Clear();
        _stripPins.AddRange(PinList.DistinctBy(pin => pin.MessageId));
        var index = _stripPins.FindIndex(pin => pin.MessageId == previous);
        _pinIndex = SelectedChat == null || index < 0 ? 0 : index;
        NotifyPinStrip();
        OnPropertyChanged(nameof(PinListEmpty));
        OnPropertyChanged(nameof(LocalPinActionLabel));
    }

    void UpdateLocalPin(long chatId, long messageId, string? text)
    {
        try
        {
            var pins = LocalPinStore.Load(_api.ServerBase, _me.Id);
            var index = pins.FindIndex(p => p.ChatId == chatId && p.MessageId == messageId);
            if (index < 0) return;
            if (text == null) pins.RemoveAt(index);
            else pins[index] = new(chatId, messageId, text);
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
        Overlay = "pins";
    }

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
                pins.Add(new(chat.Id, message.Id, message.Text));
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
        Overlay = "";
        await JumpToPin(chat, pin.MessageId);
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
