namespace MessengerDesktop;

public partial class ShellViewModel
{
    readonly Dictionary<string, string> _drafts = new();
    string _beforeEdit = "";

    partial void OnSelectedChatChanging(ChatItem? value)
    {
        if (_suppress || SelectedChat == null) return;
        _drafts[SelectedChat.Id] = Editing != null ? _beforeEdit : Draft;
    }

    void RestoreDraft(ChatItem? chat) => Draft = chat != null && _drafts.TryGetValue(chat.Id, out var saved) ? saved : "";

    void CompleteCompose(string chatId, string submitted, bool editing)
    {
        if (SelectedChat?.Id != chatId)
        {
            if (!editing && _drafts.GetValueOrDefault(chatId) == submitted) _drafts.Remove(chatId);
            return;
        }
        if (Draft == submitted) Draft = editing ? _beforeEdit : "";
        Editing = null;
        ReplyTo = null;
    }
}
