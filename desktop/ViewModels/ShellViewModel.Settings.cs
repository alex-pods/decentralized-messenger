using MessengerDesktop.Settings;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    /// <summary>Включает или выключает поиск по тегу сразу на сервере. Возвращает текст ошибки или null.</summary>
    public async Task<string?> SetTagSearchAsync(bool allow)
    {
        try
        {
            await _api.UpdateProfile(null, null, allow);
            var me = await _api.GetMe();
            await Ui(() =>
            {
                _me = me;
                AllowTagSearch = me.AllowTagSearch;
            });
            return null;
        }
        catch (Exception ex) { return Friendly(ex); }
    }

    /// <summary>Действие по двойному клику на сообщение. Возвращает true, если нужно вернуть фокус в поле ввода.</summary>
    public bool ActivateMessage(MessageItem message)
    {
        if (!HasChat || message.IsDeleted || message.IsNotStored) return false;
        var prefs = AppPreferences.Current;
        switch (prefs.DoubleClick)
        {
            case DoubleClickAction.Reply:
                SelectedMessage = message;
                Reply();
                return true;
            case DoubleClickAction.React:
                SelectedMessage = message;
                _ = ToggleReactionFor(message, prefs.DoubleClickReaction);
                return false;
            default:
                return false;
        }
    }
}
