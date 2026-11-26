namespace MessengerDesktop;

public partial class ShellViewModel
{
    public async Task<string?> OpenUserConversation(long userId)
    {
        if (userId == _me.Id) return "Это ваш профиль";
        try
        {
            var chat = await FindOrCreateDirect(userId);
            CloseOverlay();
            ResetWorkspace();
            SelectedChat = chat;
            return null;
        }
        catch (Exception ex) { return Friendly(ex); }
    }
}
