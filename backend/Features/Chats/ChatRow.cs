namespace MessengerBackend;

public class ChatRow
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string? Title { get; set; }
    public string Created_At { get; set; } = "";
    public long? Created_By { get; set; }
}
