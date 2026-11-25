namespace MessengerDesktop;

public partial class ShellViewModel
{
    public async Task<string?> OpenUserConversation(long userId)
    {
        if (userId == _me.Id) return "Это ваш профиль";
        try
        {
            var id = await _api.CreateDirect(userId);
            await ReloadChats();
            var chat = Chats.FirstOrDefault(c => c.Id == id);
            if (chat == null) return "Не удалось открыть чат. Попробуйте ещё раз.";
            CloseOverlay();
            SelectedChat = chat;
            return null;
        }
        catch (Exception ex) { return Friendly(ex); }
    }
}
