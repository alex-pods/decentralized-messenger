namespace MessengerBackend;

public class ReactionRow
{
    public long Id { get; set; }
    public long Message_Id { get; set; }
    public long User_Id { get; set; }
    public string Emoji { get; set; } = "";
}
